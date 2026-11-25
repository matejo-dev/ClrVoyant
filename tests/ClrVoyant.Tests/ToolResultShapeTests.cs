using System.Text.Json;
using ClrVoyant.Server.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClrVoyant.Tests;

/// <summary>
/// The 2.0 SDK stopped wrapping non-object structured results in <c>{"result": ...}</c>,
/// which only matters for tools that opt in to structured content. ClrVoyant tools do not:
/// clients get the value serialized as JSON text and no <c>outputSchema</c>. This pins that
/// wire contract, so turning structured content on (or an SDK default flipping) is a
/// conscious, visible change for existing clients.
/// </summary>
public class ToolResultShapeTests
{
    [Fact]
    public async Task Tools_return_json_text_and_advertise_no_output_schema()
    {
        var list = McpServerTool.Create(typeof(DebugTools).GetMethod(nameof(DebugTools.ListProcesses))!, target: null);
        var describe = McpServerTool.Create(typeof(DebugTools).GetMethod(nameof(DebugTools.GetDebugInstructions))!, target: null);

        CallToolResult? listResult = null, textResult = null;
        IList<McpClientTool>? advertised = null;
        await McpInMemory.RunAsync([list, describe], async (client, ct) =>
        {
            advertised = await client.ListToolsAsync(cancellationToken: ct);
            listResult = await client.CallToolAsync("list_processes",
                new Dictionary<string, object?> { ["dotnetOnly"] = true }, cancellationToken: ct);
            textResult = await client.CallToolAsync("get_debug_instructions", cancellationToken: ct);
        });

        Assert.Equal(2, advertised!.Count);
        Assert.All(advertised, t => Assert.Null(t.ProtocolTool.OutputSchema));

        // A list return is the serialized array as text, with no structuredContent and no wrapper object.
        Assert.Null(listResult!.StructuredContent);
        string json = Assert.IsType<TextContentBlock>(Assert.Single(listResult.Content)).Text;
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(json).RootElement.ValueKind);

        // A string return is the raw string, not JSON-quoted.
        Assert.Null(textResult!.StructuredContent);
        Assert.StartsWith("ClrVoyant", Assert.IsType<TextContentBlock>(Assert.Single(textResult.Content)).Text);
    }
}
