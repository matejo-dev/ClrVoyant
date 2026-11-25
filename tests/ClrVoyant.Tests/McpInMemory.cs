using System.Collections.Concurrent;
using System.IO.Pipelines;
using ClrVoyant.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClrVoyant.Tests;

/// <summary>
/// Runs a real MCP server (with the audit filter wired, as in the host) against a real
/// client over an in-memory duplex pipe, and hands back the audit lines it logged.
/// </summary>
static class McpInMemory
{
    public static async Task<IReadOnlyList<string>> RunAsync(
        IEnumerable<McpServerTool> tools, Func<McpClient, CancellationToken, Task> act)
    {
        var audit = new ConcurrentQueue<string>();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddProvider(new AuditCapture(audit));
            b.SetMinimumLevel(LogLevel.Information);
        });
        services.AddMcpServer().WithTools(tools).WithRequestFilters(AuditLog.Register);

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
            await act(client, cts.Token);
        }

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { /* expected on shutdown */ }
        return audit.ToArray();
    }

    /// <summary>Captures only the audit category, formatted message only.</summary>
    sealed class AuditCapture(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, sink);
        public void Dispose() { }

        sealed class Capture(string category, ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => category == AuditLog.Category;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (category == AuditLog.Category) sink.Enqueue(formatter(state, exception));
            }
        }
    }
}
