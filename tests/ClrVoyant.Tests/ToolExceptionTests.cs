using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClrVoyant.Tests;

/// <summary>
/// Pins what an MCP client actually receives when a tool method throws, and how the audit
/// filter sees it. The tools use exception messages to tell the LLM how to recover (for
/// example "no such session"), so this matters: observed on ModelContextProtocol 2.4.0
/// (identical on 1.3.0), the SDK replaces the message of every exception type except
/// <see cref="McpException"/> with a generic text. These expectations document CURRENT
/// behaviour; a change that makes tool errors informative must update them consciously.
/// </summary>
public class ToolExceptionTests
{
    public static IEnumerable<object[]> Throwables() =>
    [
        // Message is DROPPED: the client only learns that the invocation failed.
        [new InvalidOperationException("xyz"), "An error occurred invoking 'boom'."],
        [new ArgumentException("xyz"), "An error occurred invoking 'boom'."],
        [new KeyNotFoundException("xyz"), "An error occurred invoking 'boom'."],
        [new TimeoutException("xyz"), "An error occurred invoking 'boom'."],
        // McpException is the one type whose message is forwarded to the client.
        [new McpException("xyz"), "An error occurred invoking 'boom': xyz"],
    ];

    [Theory]
    [MemberData(nameof(Throwables))]
    public async Task Client_receives_this_text_and_audit_catch_branch_runs(Exception thrown, string expectedClientText)
    {
        var boom = McpServerTool.Create(
            (Func<string>)(() => throw thrown), new McpServerToolCreateOptions { Name = "boom" });

        CallToolResult? result = null;
        var audit = await McpInMemory.RunAsync([boom], async (client, ct) =>
            result = await client.CallToolAsync("boom", cancellationToken: ct));

        // The failure reaches the client as a tool error result, not as a protocol error.
        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Equal(expectedClientText, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);

        // The exception propagates THROUGH the audit filter (the SDK converts it to an error
        // result outside the filter chain): the audit's catch branch ran and saw the original
        // message. Only that branch logs the message; the IsError branch would log a bare "error".
        Assert.Equal(["tools/call boom -> error (xyz)"], audit);
    }
}
