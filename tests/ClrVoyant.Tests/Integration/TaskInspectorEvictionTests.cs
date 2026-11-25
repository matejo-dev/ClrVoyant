using ClrVoyant.Inspection;
using Microsoft.Diagnostics.Runtime;

namespace ClrVoyant.Tests.Integration;

public class TaskInspectorEvictionTests
{
    [Fact]
    public void A_process_without_a_dotnet_runtime_gets_an_actionable_message_not_an_index_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TaskInspector.PickRuntime(Array.Empty<ClrInfo>(), 4711));

        Assert.Contains("Process 4711 has no .NET runtime loaded", ex.Message);
        Assert.Contains("list_processes", ex.Message);
    }
}
