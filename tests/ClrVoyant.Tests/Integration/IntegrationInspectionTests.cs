using ClrVoyant.Core;
using ClrVoyant.Server.Tools;

namespace ClrVoyant.Tests.Integration;

[Trait("Category", "Integration")]
public class IntegrationInspectionTests : IClassFixture<DebugSessionFixture>
{
    readonly DebugSessionFixture _fx;
    public IntegrationInspectionTests(DebugSessionFixture fx) => _fx = fx;

    [Fact]
    public void Stopped_at_breakpoint_line()
    {
        Assert.Equal(SessionState.Stopped, _fx.Stop.State);
        Assert.Equal(TestPaths.BreakpointLine, _fx.Stop.Stop!.Line);
        Assert.Equal("breakpoint", _fx.Stop.Stop!.Reason);
    }

    [Fact]
    public async Task Threads_and_callstack()
    {
        var threads = await InspectionTools.GetThreads(_fx.Mgr);
        Assert.NotEmpty(threads);

        var frames = await InspectionTools.GetCallstack(_fx.Mgr, threadId: null, startFrame: 0, levels: 20);
        Assert.NotEmpty(frames);
        Assert.Contains("Compute", frames[0].Function);
        Assert.Equal(TestPaths.BreakpointLine, frames[0].Line);
    }

    [Fact]
    public async Task Scopes_variables_and_evaluate()
    {
        var frames = await InspectionTools.GetCallstack(_fx.Mgr, null, 0, 1);
        var scopes = await InspectionTools.GetScopes(_fx.Mgr, frames[0].Id);
        Assert.Contains(scopes, s => s.Name == "Locals");

        var locals = (await InspectionTools.GetVariables(_fx.Mgr, scopes[0].VariablesReference)).Items;
        Assert.Contains(locals, v => v.Name == "n");
        Assert.Contains(locals, v => v.Name == "squared");

        var eval = await InspectionTools.Evaluate(_fx.Mgr, "n * n", frames[0].Id, "repl");
        Assert.True(int.TryParse(eval.Result, out _));
    }

    // netcoredbg itself must honour the DAP start/count the server relies on to bound a variables answer.
    [Fact]
    public async Task Variables_are_paged_by_the_real_debugger()
    {
        var frames = await InspectionTools.GetCallstack(_fx.Mgr, null, 0, 1);
        var scopes = await InspectionTools.GetScopes(_fx.Mgr, frames[0].Id);
        int locals = scopes.First(s => s.Name == "Locals").VariablesReference;
        var all = (await InspectionTools.GetVariables(_fx.Mgr, locals)).Items;
        Assert.True(all.Count >= 3, "Compute has n, squared and offset in scope");

        var first = await InspectionTools.GetVariables(_fx.Mgr, locals, max: 1);
        Assert.Equal(all.Take(1).Select(v => v.Name), first.Items.Select(v => v.Name));
        Assert.True(first.Truncated);
        Assert.Equal(1, first.NextOffset);

        var middle = await InspectionTools.GetVariables(_fx.Mgr, locals, max: 1, offset: 1);
        Assert.Equal(all.Skip(1).Take(1).Select(v => v.Name), middle.Items.Select(v => v.Name));

        var rest = await InspectionTools.GetVariables(_fx.Mgr, locals, max: 100, offset: 1);
        Assert.Equal(all.Skip(1).Select(v => v.Name), rest.Items.Select(v => v.Name));
        Assert.False(rest.Truncated);
    }

    [Fact]
    public async Task Tasks_and_the_async_graph_are_paged_from_the_heap_and_cancellable()
    {
        var everything = await TaskTools.ListTasks(_fx.Mgr, _fx.Inspector, max: 1000);
        Assert.True(everything.Items.Count >= 2, "the sample app has several Tasks");

        var first = await TaskTools.ListTasks(_fx.Mgr, _fx.Inspector, max: 1);
        Assert.Equal(everything.Items[0], first.Items.Single());
        Assert.Equal(everything.Items.Count, first.Total);
        Assert.True(first.Truncated);
        Assert.Equal(1, first.NextOffset);
        var second = await TaskTools.ListTasks(_fx.Mgr, _fx.Inspector, max: 1, offset: 1);
        Assert.Equal(everything.Items[1], second.Items.Single());

        var graph = await TaskTools.GetAsyncGraph(_fx.Mgr, _fx.Inspector, max: 1000);
        var graphFirst = await TaskTools.GetAsyncGraph(_fx.Mgr, _fx.Inspector, max: 1);
        Assert.Equal(graph.Items[0].Address, graphFirst.Items.Single().Address);
        Assert.Equal(graph.Items.Count, graphFirst.Total);
        Assert.True(graph.Items.Count >= 2, "the sample app has several async nodes");
        var graphSecond = await TaskTools.GetAsyncGraph(_fx.Mgr, _fx.Inspector, max: 1, offset: 1);
        Assert.Equal(graph.Items[1].Address, graphSecond.Items.Single().Address);

        // A heap walk that is cancelled ends as a cancellation.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TaskTools.ListTasks(_fx.Mgr, _fx.Inspector, cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task Exception_info_is_null_at_a_plain_breakpoint()
    {
        // Not stopped on an exception -> engine returns no info (catch path).
        var info = await InspectionTools.GetExceptionInfo(_fx.Mgr, threadId: null);
        Assert.Null(info);
    }

    // The positive ClrMD task / async-graph inspection tests live in TargetRuntimeTests,
    // where they run against every supported target runtime; the negative cases below
    // are runtime-independent and reuse this fixture's single launch.

    [Fact]
    public async Task Get_task_returns_null_for_non_task_address()
    {
        var got = await TaskTools.GetTask(_fx.Mgr, _fx.Inspector, "0x1");
        Assert.Null(got);
    }

    [Fact]
    public async Task Async_callstack_is_empty_for_non_task_address()
    {
        var chain = await TaskTools.GetAsyncCallstack(_fx.Mgr, _fx.Inspector, "0x1");
        Assert.Empty(chain);
    }

    [Fact]
    public async Task List_breakpoints_reports_the_breakpoint()
    {
        var bps = await BreakpointTools.ListBreakpoints(_fx.Mgr);
        Assert.Contains(bps, b => b.Line == TestPaths.BreakpointLine);
    }

    [Fact]
    public void Status_and_sessions_reflect_the_running_session()
    {
        var status = LifecycleTools.DebugStatus(_fx.Mgr);
        Assert.Equal(SessionState.Stopped, status.State);
        Assert.NotEmpty(LifecycleTools.ListSessions(_fx.Mgr));
    }
}
