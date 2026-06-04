using System.Diagnostics;
using ClrVoyant.Core;
using ClrVoyant.Dap;
using ClrVoyant.Server.Tools;

namespace ClrVoyant.Tests.Integration;

[Trait("Category", "Integration")]
public class IntegrationControlTests
{
    static SessionManager NewManager() => new(() => new DapEngine(TestPaths.Netcoredbg));

    static async Task<SessionManager> StartStoppedAsync()
    {
        var mgr = NewManager();
        await DebugTools.DebugLaunch(mgr, TestPaths.SampleAppDll);
        await DebugTools.SetBreakpoint(mgr, TestPaths.SampleAppSrc, TestPaths.BreakpointLine);
        await DebugTools.Continue(mgr, null, 20000);
        return mgr;
    }

    [Fact]
    public async Task Set_breakpoint_reports_verified()
    {
        var mgr = NewManager();
        try
        {
            await DebugTools.DebugLaunch(mgr, TestPaths.SampleAppDll);
            var bp = await DebugTools.SetBreakpoint(mgr, TestPaths.SampleAppSrc, TestPaths.BreakpointLine);
            Assert.True(bp.Verified); // binds asynchronously; engine waits for the flip
        }
        finally { await Cleanup(mgr); }
    }

    [Fact]
    public async Task Step_over_advances_to_next_line()
    {
        var mgr = await StartStoppedAsync();
        try
        {
            var outcome = await DebugTools.StepOver(mgr, null, 20000);
            Assert.Equal(SessionState.Stopped, outcome.State);
            Assert.Equal("step", outcome.Stop!.Reason);
            Assert.True(outcome.Stop!.Line > TestPaths.BreakpointLine);
        }
        finally { await Cleanup(mgr); }
    }

    [Fact]
    public async Task Remove_breakpoint_shrinks_the_list()
    {
        var mgr = await StartStoppedAsync();
        try
        {
            var extra = await DebugTools.SetBreakpoint(mgr, TestPaths.SampleAppSrc, TestPaths.BreakpointLine + 1);
            var before = await DebugTools.ListBreakpoints(mgr);
            Assert.True(await DebugTools.RemoveBreakpoint(mgr, extra.Id));
            var after = await DebugTools.ListBreakpoints(mgr);
            Assert.Equal(before.Count - 1, after.Count);
        }
        finally { await Cleanup(mgr); }
    }

    [Fact]
    public async Task Set_exception_breakpoints_succeeds()
    {
        var mgr = await StartStoppedAsync();
        try
        {
            var result = await DebugTools.SetExceptionBreakpoints(mgr, new[] { "all" });
            Assert.Equal("ok", result);
        }
        finally { await Cleanup(mgr); }
    }

    [Fact]
    public async Task Pause_then_wait_for_any_reports_a_stop()
    {
        // Realistic pause: a thread id must be known. We get one from an initial
        // breakpoint stop, then let the app run freely and pause it.
        var mgr = await StartStoppedAsync();
        try
        {
            var bps = await DebugTools.ListBreakpoints(mgr);
            foreach (var b in bps) await DebugTools.RemoveBreakpoint(mgr, b.Id);

            await DebugTools.Continue(mgr, null, 500); // times out -> running freely
            await DebugTools.Pause(mgr);

            var stop = await DebugTools.WaitForAnyStop(mgr, 5000);
            Assert.IsType<SessionStop>(stop);
        }
        finally { await Cleanup(mgr); }
    }

    [Fact]
    public async Task Attach_to_an_external_process()
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{TestPaths.SampleAppDll}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        var proc = Process.Start(psi)!;
        var mgr = NewManager();
        try
        {
            var info = await DebugTools.DebugAttach(mgr, proc.Id);
            Assert.Equal(proc.Id, info.Pid);
            var status = DebugTools.DebugStatus(mgr, info.SessionId);
            Assert.Equal(info.SessionId, status.SessionId);
        }
        finally
        {
            await Cleanup(mgr);
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
        }
    }

    [Fact]
    public async Task Multiple_concurrent_sessions()
    {
        var mgr = NewManager();
        try
        {
            var a = await DebugTools.DebugLaunch(mgr, TestPaths.SampleAppDll);
            await DebugTools.DebugLaunch(mgr, TestPaths.SampleAppDll);
            Assert.Equal(2, DebugTools.ListSessions(mgr).Count);

            await DebugTools.DebugStop(mgr, a.SessionId);
            Assert.Single(DebugTools.ListSessions(mgr));
        }
        finally { await Cleanup(mgr); }
    }

    [Fact]
    public async Task Auto_attach_discovers_a_child_process()
    {
        var mgr = NewManager();
        var opts = new ClrVoyant.Server.AutoAttachOptions { Enabled = true };
        var watcher = new ClrVoyant.Server.ChildProcessWatcher(mgr, opts);
        try
        {
            // ParentApp spawns `dotnet SampleApp.dll` as a child .NET process.
            await DebugTools.DebugLaunch(mgr, TestPaths.ParentAppDll, new[] { TestPaths.SampleAppDll });

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline && mgr.List().Count < 2)
            {
                await watcher.ScanOnceAsync();
                await Task.Delay(300);
            }
            Assert.Equal(2, mgr.List().Count); // parent + auto-attached child
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
