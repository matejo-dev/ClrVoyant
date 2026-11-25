using System.IO.Pipelines;
using ClrVoyant.Server;
using ClrVoyant.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClrVoyant.Tests;

/// <summary>What a test sees of a running in-memory MCP server: a real client connected to it, the
/// server's service provider, and what it logged.</summary>
sealed record McpRun(McpClient Client, IServiceProvider Services, ServerLogs Logs, CancellationToken Ct);

/// <summary>The server's log entries, with a signal-based wait (no polling) for one to appear.</summary>
sealed class ServerLogs
{
    public sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception);

    readonly object _gate = new();
    readonly List<Entry> _entries = new();
    readonly List<(Func<Entry, bool> Match, TaskCompletionSource Done)> _waiters = new();

    public IReadOnlyList<Entry> Entries { get { lock (_gate) return _entries.ToArray(); } }

    /// <summary>The audit lines (formatted message only), in order.</summary>
    public IReadOnlyList<string> Audit => Entries.Where(e => e.Category == AuditLog.Category).Select(e => e.Message).ToList();

    internal void Add(Entry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            foreach (var w in _waiters.Where(w => w.Match(entry)).ToList())
            {
                w.Done.TrySetResult();
                _waiters.Remove(w);
            }
        }
    }

    /// <summary>Completes when an entry matching <paramref name="match"/> has been logged (now or later).</summary>
    public Task WaitForAsync(Func<Entry, bool> match, CancellationToken ct)
    {
        TaskCompletionSource done;
        lock (_gate)
        {
            if (_entries.Any(match)) return Task.CompletedTask;
            done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((match, done));
        }
        return done.Task.WaitAsync(ct);
    }

    public Task WaitForAuditAsync(string line, CancellationToken ct)
        => WaitForAsync(e => e.Category == AuditLog.Category && e.Message == line, ct);
}

/// <summary>
/// Runs a real MCP server against a real client over an in-memory duplex pipe, wired with the
/// production pipeline (<see cref="McpRegistration"/>): either around tools the test supplies, or
/// with the real tool registration over <see cref="FakeDebugEngine"/> sessions.
/// </summary>
static class McpInMemory
{
    /// <summary>The production pipeline around the given tools; returns the audit lines.</summary>
    public static async Task<IReadOnlyList<string>> RunAsync(
        IEnumerable<McpServerTool> tools, Func<McpClient, CancellationToken, Task> act)
        => (await RunAsync(tools, r => act(r.Client, r.Ct))).Audit;

    /// <summary>The production pipeline around the given tools; returns everything the server logged.</summary>
    public static Task<ServerLogs> RunAsync(IEnumerable<McpServerTool> tools, Func<McpRun, Task> act)
        => RunAsync(services => services.AddMcpServer().WithTools(tools).UseClrVoyantPipeline(), act);

    /// <summary>The real registration (every tool, the real DI wiring) over fake engines; returns the logs.</summary>
    public static Task<ServerLogs> RunRealAsync(FakeEngineFactory engines, Func<McpRun, Task> act)
        => RunAsync(services =>
        {
            HostFactory.AddDebugServices(services, engines.Create);
            services.AddClrVoyantMcpServer();
        }, act);

    /// <summary>The real registration with the real netcoredbg-backed engine, whose executable is
    /// looked up by <paramref name="locateEngine"/> (so a test can make it missing).</summary>
    public static Task<ServerLogs> RunRealAsync(Func<string> locateEngine, Func<McpRun, Task> act)
        => RunAsync(services =>
        {
            HostFactory.AddDebugServices(services, locateEngine: locateEngine);
            services.AddClrVoyantMcpServer();
        }, act);

    static async Task<ServerLogs> RunAsync(Action<IServiceCollection> configure, Func<McpRun, Task> act)
    {
        var logs = new ServerLogs();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddProvider(new CaptureProvider(logs));
            b.SetMinimumLevel(LogLevel.Information);
        });
        configure(services);

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var c2s = new Pipe();
        var s2c = new Pipe();
        await using var server = McpServer.Create(
            new StreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream(), "in-memory", loggerFactory),
            options, loggerFactory, provider);
        var runTask = server.RunAsync(cts.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream(), loggerFactory),
            cancellationToken: cts.Token))
        {
            await act(new McpRun(client, provider, logs, cts.Token));
        }

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { /* expected on shutdown */ }
        return logs;
    }

    sealed class CaptureProvider(ServerLogs sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, sink);
        public void Dispose() { }

        sealed class Capture(string category, ServerLogs sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            // Only the categories under test: the SDK's own chatter is not ours to pin.
            public bool IsEnabled(LogLevel logLevel) => category == AuditLog.Category || category == ToolErrors.Category;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) sink.Add(new ServerLogs.Entry(category, logLevel, formatter(state, exception), exception));
            }
        }
    }
}
