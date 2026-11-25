using ClrVoyant.Core;
using ClrVoyant.Dap;
using ClrVoyant.Inspection;
using ClrVoyant.Server.Tools;

namespace ClrVoyant.Tests.Integration;

/// <summary>
/// The runtime-sensitive part of the suite, run against a debuggee built for each of
/// .NET 8, 9 and 10 (the runtimes ClrVoyant claims to debug). The control loop goes
/// through netcoredbg, whose behaviour depends on the target runtime's debugging
/// services; the inspection test reads the target heap through ClrMD, whose type
/// layouts (Task, async state machines) change between runtime versions. A runtime that
/// is not installed shows up as a skipped row, not a pass.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TargetRuntimeTests
{
    [TargetRuntimeTheory, AllTargetRuntimes]
    public async Task Breakpoint_stop_callstack_variables_loop(string tfm)
    {
        var mgr = new SessionManager(() => new DapEngine(TestPaths.Netcoredbg));
        try
        {
            await LifecycleTools.DebugLaunch(mgr, TestPaths.SampleAppDllFor(tfm));
            await BreakpointTools.SetBreakpoint(mgr, TestPaths.SampleAppSrc, TestPaths.BreakpointLine);

            var stop = await ExecutionTools.Continue(mgr, null, 30000);
            Assert.Equal(SessionState.Stopped, stop.State);
            Assert.Equal(TestPaths.BreakpointLine, stop.Stop!.Line);

            var frames = await InspectionTools.GetCallstack(mgr, null, 0, 20);
            Assert.Contains("Compute", frames[0].Function);
            Assert.Equal(TestPaths.BreakpointLine, frames[0].Line);

            var scopes = await InspectionTools.GetScopes(mgr, frames[0].Id);
            var locals = (await InspectionTools.GetVariables(mgr, scopes[0].VariablesReference)).Items;
            Assert.Contains(locals, v => v.Name == "n");
            Assert.Contains(locals, v => v.Name == "squared");

            var eval = await InspectionTools.Evaluate(mgr, "n * n", frames[0].Id, "repl");
            Assert.True(int.TryParse(eval.Result, out _));
        }
        finally { await Cleanup(mgr); }
    }

    [TargetRuntimeTheory, AllTargetRuntimes]
    public async Task ClrMd_task_and_async_graph_inspection(string tfm)
    {
        var mgr = new SessionManager(() => new DapEngine(TestPaths.Netcoredbg));
        using var inspector = new TaskInspector();
        try
        {
            await LifecycleTools.DebugLaunch(mgr, TestPaths.SampleAppDllFor(tfm));
            await BreakpointTools.SetBreakpoint(mgr, TestPaths.SampleAppSrc, TestPaths.BreakpointLine);
            var stop = await ExecutionTools.Continue(mgr, null, 30000);
            Assert.Equal(SessionState.Stopped, stop.State);

            // Task enumeration with status and the owning async method.
            var tasksPage = await TaskTools.ListTasks(mgr, inspector);
            var tasks = tasksPage.Items;
            Assert.NotEmpty(tasks);
            Assert.False(tasksPage.Truncated); // the sample has a handful of Tasks, far below the default window
            Assert.Equal(tasks.Count, tasksPage.Total);
            Assert.Contains(tasks, t => t.Status == "WaitingForActivation");
            Assert.Contains(tasks, t => t.AsyncMethod is "AwaitForever" or "Main");

            var waitingPage = await TaskTools.ListTasks(mgr, inspector, status: "waitingforactivation");
            var waiting = waitingPage.Items;
            Assert.NotEmpty(waiting);
            Assert.Equal(waiting.Count, waitingPage.Total); // the total counts the filtered set, not the whole heap
            Assert.All(waiting, t => Assert.Equal("WaitingForActivation", t.Status));

            // Lookup by address round-trips.
            var got = await TaskTools.GetTask(mgr, inspector, tasks[0].Address);
            Assert.NotNull(got);
            Assert.Equal(tasks[0].Address, got!.Address);

            // Async graph: an awaiting edge exists and the async call stack follows continuations.
            var graph = (await TaskTools.GetAsyncGraph(mgr, inspector)).Items;
            Assert.NotEmpty(graph);
            Assert.Contains(graph, n => n.Awaiting.Count > 0);

            var withCont = graph.FirstOrDefault(n => n.ContinuationAddress is not null) ?? graph[0];
            var chain = await TaskTools.GetAsyncCallstack(mgr, inspector, withCont.Address);
            Assert.NotEmpty(chain);
        }
        finally { await Cleanup(mgr); }
    }

    static async Task Cleanup(SessionManager mgr)
    {
        foreach (var s in mgr.List())
        {
            try { await mgr.StopAsync(s.SessionId); } catch { }
        }
        await mgr.DisposeAsync();
    }
}
