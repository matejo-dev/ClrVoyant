using ClrVoyant.Dap;
using ClrVoyant.Server;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClrVoyant.Tests;

/// <summary>
/// What an MCP client receives when a tool method throws, through the production pipeline. The tools
/// use exception messages to tell the LLM how to recover (which sessionId to pass, that the target
/// must be stopped), but the SDK alone (observed on ModelContextProtocol 2.4.0) replaces the message of
/// every exception except <see cref="McpException"/> with "An error occurred invoking '&lt;tool&gt;'".
/// <see cref="ToolErrors"/> restores it at the dispatch boundary; these tests pin that decision.
/// </summary>
public class ToolExceptionTests
{
    static McpServerTool Throwing(Exception thrown, string name = "boom")
        => McpServerTool.Create((Func<string>)(() => throw thrown), new McpServerToolCreateOptions { Name = name });

    public static IEnumerable<object[]> Expected() =>
    [
        // The kinds this code base throws on purpose to explain a condition: the message, unchanged.
        [new InvalidOperationException("xyz")],
        [new ArgumentException("xyz")],
        [new KeyNotFoundException("xyz")],
        [new TimeoutException("xyz")],
        [new FileNotFoundException("xyz")],
        [new DapException("variables", "xyz")],
        [new McpException("xyz")],
    ];

    [Theory]
    [MemberData(nameof(Expected))]
    public async Task An_expected_failure_reaches_the_client_as_its_own_message(Exception thrown)
    {
        CallToolResult? result = null;
        var audit = await McpInMemory.RunAsync([Throwing(thrown)], async (client, ct) =>
            result = await client.CallToolAsync("boom", cancellationToken: ct));

        Assert.True(result!.IsError);
        Assert.Equal(thrown.Message, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        // The audit filter sits inside the mapping one, so it still sees the original exception and its message.
        Assert.Equal([$"tools/call boom -> error ({thrown.Message})"], audit);
    }

    [Fact]
    public async Task An_unexpected_failure_names_its_type_and_logs_the_stack_trace_only_on_the_server()
    {
        var thrown = new NullReferenceException("xyz");
        CallToolResult? result = null;
        var logs = await McpInMemory.RunAsync([Throwing(thrown)], async run =>
            result = await run.Client.CallToolAsync("boom", cancellationToken: run.Ct));

        Assert.True(result!.IsError);
        Assert.Equal("Unexpected NullReferenceException: xyz", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        var logged = Assert.Single(logs.Entries, e => e.Category == ToolErrors.Category);
        Assert.Equal(LogLevel.Error, logged.Level);
        Assert.Same(thrown, logged.Exception);
        Assert.Equal(["tools/call boom -> error (xyz)"], logs.Audit);
    }

    [Fact]
    public async Task An_expected_failure_is_not_logged_as_a_server_error()
    {
        var logs = await McpInMemory.RunAsync([Throwing(new InvalidOperationException("xyz"))], async run =>
            await run.Client.CallToolAsync("boom", cancellationToken: run.Ct));

        Assert.DoesNotContain(logs.Entries, e => e.Category == ToolErrors.Category);
    }

    [Fact]
    public async Task A_cancellation_that_nobody_requested_is_a_failure_not_a_cancelled_call()
    {
        // An internal timeout surfacing as OperationCanceledException is not the client cancelling.
        CallToolResult? result = null;
        await McpInMemory.RunAsync([Throwing(new OperationCanceledException("xyz"))], async (client, ct) =>
            result = await client.CallToolAsync("boom", cancellationToken: ct));

        Assert.True(result!.IsError);
        Assert.Equal("Unexpected OperationCanceledException: xyz", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task A_protocol_error_stays_a_protocol_error()
    {
        var tool = Throwing(new McpProtocolException("bad params", McpErrorCode.InvalidParams));
        var audit = await McpInMemory.RunAsync([tool], async (client, ct) =>
        {
            var ex = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("boom", cancellationToken: ct).AsTask());
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        });

        Assert.Equal(["tools/call boom -> error (bad params)"], audit);
    }
}
