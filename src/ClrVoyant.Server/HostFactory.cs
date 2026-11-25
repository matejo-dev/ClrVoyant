using System.Security.Cryptography;
using System.Text;
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
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithRequestFilters(AuditLog.Register);

        return builder.Build();
    }

    static async Task RunHttpAsync(string[] args)
    {
        string? token = Environment.GetEnvironmentVariable("CLRVOYANT_AUTH_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "HTTP transport requires authentication: set CLRVOYANT_AUTH_TOKEN to a secret bearer token. " +
                "An unauthenticated debug server that can launch processes and evaluate code is a remote-code-execution risk.");

        var app = BuildHttpApp(args, token, Environment.GetEnvironmentVariable("CLRVOYANT_HTTP_URL") ?? "http://0.0.0.0:3001");
        await app.RunAsync();
    }

    /// <summary>Build (without starting) the authenticated Streamable-HTTP host. Split out of
    /// <see cref="RunHttpAsync"/> so tests can start the real wiring on an ephemeral port.</summary>
    internal static WebApplication BuildHttpApp(string[] args, string token, string url)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(url);

        AddDebugServices(builder.Services);
        builder.Services
            .AddMcpServer()
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
            })
            .WithToolsFromAssembly()
            .WithRequestFilters(AuditLog.Register);

        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            const string prefix = "Bearer ";
            string header = ctx.Request.Headers.Authorization.ToString();
            string presented = header.StartsWith(prefix, StringComparison.Ordinal) ? header[prefix.Length..] : "";
            if (!FixedTimeEquals(presented, token))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                await ctx.Response.WriteAsync("Unauthorized: missing or invalid bearer token.");
                return;
            }
            await next();
        });
        app.MapMcp();

        return app;
    }

    /// <summary>Register the transport-independent services: one SessionManager for
    /// the whole server (it manufactures a DapEngine per session over netcoredbg,
    /// resolved lazily so the host can be built/inspected without the engine
    /// present; it tells the ClrMD TaskInspector when a process is gone), the
    /// TaskInspector, and the opt-in child-process auto-attach.</summary>
    static void AddDebugServices(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            string netcoredbg = NetcoredbgLocator.Resolve();
            var sessions = new SessionManager(() => new DapEngine(netcoredbg));
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

    static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
