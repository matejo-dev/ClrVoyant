using ClrVoyant.Core;
using ClrVoyant.Dap;
using ClrVoyant.Inspection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace ClrVoyant.Server;

/// <summary>
/// Builds the ClrVoyant host. Two transports are supported, selected by the
/// CLRVOYANT_TRANSPORT env var:
///   - "stdio" (default): a console MCP server; the agent spawns it locally.
///   - "http": a Streamable-HTTP MCP server (e.g. shipped inside a POD next to the
///     app being debugged) reachable by a remote agent. HTTP REQUIRES a bearer
///     token (CLRVOYANT_AUTH_TOKEN): a debug server that can launch processes and
///     evaluate expressions is a remote-code-execution surface, so it fails closed.
///
/// The HTTP transport listens on loopback unless CLRVOYANT_HTTP_URL says otherwise.
///
/// For stdio, stdout is the JSON-RPC stream, so ALL logging goes to stderr.
/// </summary>
public static class HostFactory
{
    /// <summary>Run the server with the configured transport (blocks until exit).</summary>
    public static Task RunAsync(string[] args)
    {
        var transport = Environment.GetEnvironmentVariable("CLRVOYANT_TRANSPORT")?.Trim().ToLowerInvariant();
        return transport == "http" ? RunHttpAsync(args) : Create(args).RunAsync();
    }

    /// <summary>Build the stdio host. Kept public so DI wiring is testable without
    /// driving the stdio loop.</summary>
    public static IHost Create(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        AddDebugServices(builder.Services);
        builder.Services
            .AddClrVoyantMcpServer()
            .WithStdioServerTransport();

        return builder.Build();
    }

    static async Task RunHttpAsync(string[] args)
    {
        string? token = Environment.GetEnvironmentVariable("CLRVOYANT_AUTH_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "HTTP transport requires authentication: set CLRVOYANT_AUTH_TOKEN to a secret bearer token. " +
                "An unauthenticated debug server that can launch processes and evaluate code is a remote-code-execution risk.");

        await using var app = BuildHttpApp(args, token, ResolveHttpUrl(Environment.GetEnvironmentVariable("CLRVOYANT_HTTP_URL")));
        await app.StartAsync();
        LogBinding(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ClrVoyant.Host"), app.Urls);
        await app.WaitForShutdownAsync();
    }

    /// <summary>The URL to listen on: CLRVOYANT_HTTP_URL when set, else <see cref="DefaultHttpUrl"/>.</summary>
    internal static string ResolveHttpUrl(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? DefaultHttpUrl : configured;

    /// <summary>Where the HTTP transport listens unless CLRVOYANT_HTTP_URL says otherwise: this machine
    /// only. The server can launch processes and evaluate code, so reaching it from the network is a
    /// decision the operator makes (the container image makes it for its pod).</summary>
    internal const string DefaultHttpUrl = "http://127.0.0.1:3001";

    /// <summary>Say where the server is reachable, and warn when that is more than this machine.</summary>
    internal static void LogBinding(ILogger log, IEnumerable<string> urls)
    {
        foreach (string url in urls)
        {
            log.LogInformation("ClrVoyant HTTP transport listening on {Url}", url);
            if (!IsLoopbackUrl(url))
                log.LogWarning(
                    "{Url} is not a loopback address: anyone who can reach it and holds the bearer token can launch processes " +
                    "and evaluate code on this machine. Restrict the network path (firewall, NetworkPolicy, port-forward) or bind to loopback.",
                    url);
        }
    }

    internal static bool IsLoopbackUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;

    /// <summary>Build (without starting) the authenticated Streamable-HTTP host. Split out of
    /// <see cref="RunHttpAsync"/> so tests can start the real wiring on an ephemeral port.
    /// <paramref name="configure"/> lets a test adjust the builder (e.g. capture logs).</summary>
    internal static WebApplication BuildHttpApp(string[] args, string token, string url,
        Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(url);
        configure?.Invoke(builder);
        builder.Services.AddHttpContextAccessor(); // the audit filter reads the caller's address from it

        AddDebugServices(builder.Services);
        builder.Services
            .AddClrVoyantMcpServer()
            .WithHttpTransport(o =>
            {
                // SESSION MODE: stateless, set explicitly (2.x flipped the default; never rely on it).
                // Nothing here needs MCP session state: debug sessions live in the process-wide
                // SessionManager (addressed by the tools' own sessionId argument), no tool sends
                // notifications or server-to-client requests (sampling/elicitation/roots), and
                // every tool is a plain request/response. Stateless therefore loses nothing and
                // avoids the stateful failure modes of a long-lived sidecar: a 404 and a forced
                // re-initialize after the 2h idle timeout or a restart, and session affinity.
                // Measured on 2.4.0 with an HTTP client that cancels an in-flight call: a tool's
                // CancellationToken fired in stateless mode but NOT in stateful mode.
                // Revisit (SessionMode.Stateful) only if a feature needs unsolicited server push
                // (e.g. "stopped" events); stateless has no GET stream for those.
                o.SessionMode = HttpServerSessionMode.Stateless;
            });

        var app = builder.Build();
        app.UseBearerToken(token);
        app.MapMcp();

        return app;
    }

    /// <summary>Register the transport-independent services: one SessionManager for
    /// the whole server (it manufactures a DapEngine per session over netcoredbg; it tells
    /// the ClrMD TaskInspector when a process is gone), the TaskInspector, and the opt-in
    /// child-process auto-attach.
    ///
    /// netcoredbg is located on the FIRST session started, not when the host starts: the
    /// server must come up and answer initialize/tools/list even where the engine is missing
    /// (an MCP client only sees "server exited" when startup crashes), and a first-run download
    /// must not happen inside the client's initialize window. A failure surfaces as the error
    /// of the first debug_launch / debug_attach / debug_test and is retried on the next one.
    /// <paramref name="engineFactory"/> replaces the netcoredbg-backed engine and
    /// <paramref name="locateEngine"/> the lookup of its executable (tests); the wiring around
    /// them stays the real one.</summary>
    internal static void AddDebugServices(IServiceCollection services, Func<IDebugEngine>? engineFactory = null,
        Func<string>? locateEngine = null)
    {
        services.AddSingleton(sp =>
        {
            var factory = engineFactory;
            if (factory is null)
            {
                var locate = locateEngine ?? NetcoredbgLocator.Resolve;
                string? netcoredbg = null;
                object gate = new();
                // Only a success is remembered: a failure (nothing to fetch yet, no network) can be fixed
                // while the server runs, and the next session start looks again.
                factory = () =>
                {
                    string path;
                    lock (gate) path = netcoredbg ??= locate();
                    return new DapEngine(path);
                };
            }
            var sessions = new SessionManager(factory);
            // A process that is gone must not keep a heap snapshot (or lend it to a recycled pid).
            var inspector = sp.GetRequiredService<TaskInspector>();
            sessions.DebuggeeEnded += inspector.Evict;
            return sessions;
        });
        services.AddSingleton<TaskInspector>();
        services.AddSingleton<TestDebugLauncher>();

        services.AddSingleton<AutoAttachOptions>();
        services.AddSingleton<ChildProcessWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<ChildProcessWatcher>());
    }
}
