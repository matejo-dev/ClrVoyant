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
/// The HTTP transport can launch processes and <c>evaluate</c> code, so the security
/// model leans on every <c>tools/call</c> being logged (tool name + outcome) to make
/// sessions attributable. This drives a real MCP server (built like the host, with the
/// audit filter wired) over an in-memory duplex stream pair and asserts the audit lines
/// are emitted with the right outcome — covering both failure paths: a tool that returns
/// an IsError result and one that throws (the exception propagates through the filter, is
/// logged as an error, and the SDK still reports IsError to the client).
/// </summary>
public class AuditLogTests
{
    [Fact]
    public async Task Logs_each_tool_call_with_its_outcome()
    {
        var captured = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddProvider(captured);
            b.SetMinimumLevel(LogLevel.Information);
        });

        var okTool = McpServerTool.Create(
            () => "fine", new McpServerToolCreateOptions { Name = "ok_tool" });
        var boomTool = McpServerTool.Create(
            (Func<string>)(() => throw new InvalidOperationException("boom")),
            new McpServerToolCreateOptions { Name = "boom_tool" });

        services.AddMcpServer()
            .WithTools([okTool, boomTool])
            .WithRequestFilters(AuditLog.Register);

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        // In-memory duplex: c2s carries client->server, s2c carries server->client.
        var c2s = new Pipe();
        var s2c = new Pipe();
        var serverTransport = new StreamServerTransport(
            c2s.Reader.AsStream(), s2c.Writer.AsStream(), "audit-test-server", loggerFactory);
        await using var server = McpServer.Create(serverTransport, options, loggerFactory, provider);

        var runTask = server.RunAsync(ct);

        var clientTransport = new StreamClientTransport(
            c2s.Writer.AsStream(), s2c.Reader.AsStream(), loggerFactory);
        await using (var client = await McpClient.CreateAsync(clientTransport, cancellationToken: ct))
        {
            var ok = await client.CallToolAsync("ok_tool", cancellationToken: ct);
            Assert.True(ok.IsError != true);

            var boom = await client.CallToolAsync("boom_tool", cancellationToken: ct);
            // A throwing tool propagates through the filter (logged as error there) and
            // the SDK still surfaces the failure to the client as an IsError result.
            Assert.True(boom.IsError == true);
        }

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { /* expected on shutdown */ }

        var audit = captured.Lines.Where(l => l.StartsWith(AuditLog.Category + "|")).ToList();
        Assert.Contains("tools/call ok_tool -> ok", string.Join("\n", audit));
        Assert.Contains("tools/call boom_tool -> error", string.Join("\n", audit));
    }

    sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Lines);
        public void Dispose() { }

        sealed class CapturingLogger(string category, ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => sink.Enqueue($"{category}|{formatter(state, exception)}");
        }
    }
}
