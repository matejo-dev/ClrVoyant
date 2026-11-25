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
        var threads = await DebugTools.GetThreads(_fx.Mgr);
        Assert.NotEmpty(threads);

        var frames = await DebugTools.GetCallstack(_fx.Mgr, threadId: null, startFrame: 0, levels: 20);
        Assert.NotEmpty(frames);
        Assert.Contains("Compute", frames[0].Function);
        Assert.Equal(TestPaths.BreakpointLine, frames[0].Line);
    }

    [Fact]
    public async Task Scopes_variables_and_evaluate()
    {
        var frames = await DebugTools.GetCallstack(_fx.Mgr, null, 0, 1);
        var scopes = await DebugTools.GetScopes(_fx.Mgr, frames[0].Id);
        Assert.Contains(scopes, s => s.Name == "Locals");

        var locals = await DebugTools.GetVariables(_fx.Mgr, scopes[0].VariablesReference);
        Assert.Contains(locals, v => v.Name == "n");
        Assert.Contains(locals, v => v.Name == "squared");

        var eval = await DebugTools.Evaluate(_fx.Mgr, "n * n", frames[0].Id, "repl");
        Assert.True(int.TryParse(eval.Result, out _));
    }

    [Fact]
    public async Task Exception_info_is_null_at_a_plain_breakpoint()
    {
        // Not stopped on an exception -> engine returns no info (catch path).
        var info = await DebugTools.GetExceptionInfo(_fx.Mgr, threadId: null);
        Assert.Null(info);
    }

    // The positive ClrMD task / async-graph inspection tests live in TargetRuntimeTests,
    // where they run against every supported target runtime; the negative cases below
    // are runtime-independent and reuse this fixture's single launch.

    [Fact]
    public async Task Get_task_returns_null_for_non_task_address()
    {
        var got = await DebugTools.GetTask(_fx.Mgr, _fx.Inspector, "0x1");
        Assert.Null(got);
    }

    [Fact]
    public async Task Async_callstack_is_empty_for_non_task_address()
    {
        var chain = await DebugTools.GetAsyncCallstack(_fx.Mgr, _fx.Inspector, "0x1");
        Assert.Empty(chain);
    }

    [Fact]
    public async Task List_breakpoints_reports_the_breakpoint()
    {
        var bps = await DebugTools.ListBreakpoints(_fx.Mgr);
        Assert.Contains(bps, b => b.Line == TestPaths.BreakpointLine);
    }

    [Fact]
    public void Status_and_sessions_reflect_the_running_session()
    {
        var status = DebugTools.DebugStatus(_fx.Mgr);
        Assert.Equal(SessionState.Stopped, status.State);
        Assert.NotEmpty(DebugTools.ListSessions(_fx.Mgr));
    }
}
