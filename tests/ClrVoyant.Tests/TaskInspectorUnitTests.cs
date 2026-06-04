using ClrVoyant.Inspection;

namespace ClrVoyant.Tests;

public class TaskInspectorUnitTests
{
    [Theory]
    [InlineData(0x200000, "Faulted")]
    [InlineData(0x400000, "Canceled")]
    [InlineData(0x1000000, "RanToCompletion")]
    [InlineData(0x800000, "WaitingForChildrenToComplete")]
    [InlineData(0x20000, "Running")]
    [InlineData(0x10000, "WaitingToRun")]
    [InlineData(0x2000000, "WaitingForActivation")]
    [InlineData(0, "Created")]
    public void StatusOf_decodes_state_flags(int flags, string expected)
        => Assert.Equal(expected, TaskInspector.StatusOf(flags));

    [Fact]
    public void StatusOf_faulted_takes_priority_over_started()
        => Assert.Equal("Faulted", TaskInspector.StatusOf(0x200000 | 0x10000));

    [Theory]
    // ordinary async method
    [InlineData("...+AsyncStateMachineBox<Zoo+<DoWork>d__2>", "DoWork")]
    // top-level local function
    [InlineData("...+AsyncStateMachineBox<Program+<<<Main>$>g__AwaitForever|0_1>d>", "AwaitForever")]
    // top-level statements (Main)
    [InlineData("...+AsyncStateMachineBox<Program+<<Main>$>d__0>", "Main")]
    public void ExtractAsyncMethod_recovers_method_name(string typeName, string expected)
        => Assert.Equal(expected, TaskInspector.ExtractAsyncMethod(typeName));

    [Fact]
    public void ExtractAsyncMethod_returns_null_for_non_state_machine()
    {
        Assert.Null(TaskInspector.ExtractAsyncMethod("System.Threading.Tasks.Task<System.Int32>"));
        Assert.Null(TaskInspector.ExtractAsyncMethod(null));
    }
}
