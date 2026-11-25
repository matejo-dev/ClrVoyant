using ClrVoyant.Core;
using ClrVoyant.Dap;
using ClrVoyant.Inspection;
using ClrVoyant.Server.Tools;
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task The_snapshot_of_a_process_is_released_when_its_session_ends()
    {
        var mgr = new SessionManager(() => new DapEngine(TestPaths.Netcoredbg));
        using var inspector = new TaskInspector();
        mgr.DebuggeeEnded += inspector.Evict; // the wiring HostFactory does
        try
        {
            var info = await DebugTools.DebugLaunch(mgr, TestPaths.SampleAppDll);
            await DebugTools.SetBreakpoint(mgr, TestPaths.SampleAppSrc, TestPaths.BreakpointLine);
            await DebugTools.Continue(mgr, threadId: null, timeoutMs: 20000);
            var s = mgr.Resolve(info.SessionId);
            Assert.NotEmpty(inspector.EnumerateTasks(s.Pid, s.StopId));
            Assert.Contains(s.Pid, inspector.CachedPids); // taking the snapshot cached it

            await mgr.StopAsync(info.SessionId);

            Assert.DoesNotContain(s.Pid, inspector.CachedPids);
        }
        finally { await mgr.DisposeAsync(); }
    }
}
