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
        await LifecycleTools.DebugLaunch(mgr, TestPaths.SampleAppDll);
        await BreakpointTools.SetBreakpoint(mgr, TestPaths.SampleAppSrc, TestPaths.BreakpointLine);
        await ExecutionTools.Continue(mgr, null, 20000);
        return mgr;
    }

    [Fact]
    public async Task Set_breakpoint_reports_verified()
    {
        var mgr = NewManager();
        try
        {
            await LifecycleTools.DebugLaunch(mgr, TestPaths.SampleAppDll);
            var bp = await BreakpointTools.SetBreakpoint(mgr, TestPaths.SampleAppSrc, TestPaths.BreakpointLine);
            Assert.True(bp.Verified); // binds asynchronously; engine waits for the flip
        }
        finally { await Cleanup(mgr); }
    }

    [Fact]
    public async Task Function_breakpoint_binds_and_hits_without_a_source_line()
    {
        // The no-source path: break by method name only — no file, no line. netcoredbg
        // binds it from the PDB and stops inside SampleApp.Compute.
        var mgr = NewManager();
        try
        {
            await LifecycleTools.DebugLaunch(mgr, TestPaths.SampleAppDll);
            var fb = await BreakpointTools.SetFunctionBreakpoint(mgr, "Calc.Compute");
            Assert.Equal("Calc.Compute", fb.FunctionName);

            // The proof is the HIT: function breakpoints bind lazily (when the module
            // loads), so the running target should stop inside Compute, no line given.
            var outcome = await ExecutionTools.Continue(mgr, null, 20000);
            Assert.Equal(SessionState.Stopped, outcome.State);

            var stack = await InspectionTools.GetCallstack(mgr, null, 0, 5);
            Assert.Contains(stack, f => f.Function.Contains("Compute", StringComparison.OrdinalIgnoreCase));

            var listed = await BreakpointTools.ListFunctionBreakpoints(mgr);
            Assert.Contains(listed, b => b.Id == fb.Id && b.FunctionName == "Calc.Compute");
        }
        finally { await Cleanup(mgr); }
    }

    [Fact]
    public async Task Step_over_advances_to_next_line()
    {
        var mgr = await StartStoppedAsync();
        try
        {
            var outcome = await ExecutionTools.StepOver(mgr, null, 20000);
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
            var extra = await BreakpointTools.SetBreakpoint(mgr, TestPaths.SampleAppSrc, TestPaths.BreakpointLine + 1);
            var before = await BreakpointTools.ListBreakpoints(mgr);
            Assert.True(await BreakpointTools.RemoveBreakpoint(mgr, extra.Id));
            var after = await BreakpointTools.ListBreakpoints(mgr);
            Assert.Equal(before.Count - 1, after.Count);
        }
        finally { await Cleanup(mgr); }
    }

    [Fact]
    public async Task Exception_info_reports_the_thrown_exception()
    {
        // Break on the first-chance exception SampleApp throws at startup and read it.
        // stopAtEntry lets us arm the exception filter before any user code runs.
        var mgr = NewManager();
        try
        {
            await LifecycleTools.DebugLaunch(mgr, TestPaths.SampleAppDll, stopAtEntry: true);
            if (LifecycleTools.DebugStatus(mgr).State != SessionState.Stopped)
                await ExecutionTools.WaitForAnyStop(mgr, 10000);

            await BreakpointTools.SetExceptionBreakpoints(mgr, new[] { "all" });
            var outcome = await ExecutionTools.Continue(mgr, null, 20000);
            Assert.Equal(SessionState.Stopped, outcome.State);

            var info = await InspectionTools.GetExceptionInfo(mgr, null);
            Assert.NotNull(info);
            Assert.Contains("InvalidOperationException", info!.TypeName + " " + info.Description);
        }
        finally { await Cleanup(mgr); }
    }

    [Fact]
    public async Task Clear_all_removes_both_source_and_function_breakpoints()
    {
        var mgr = NewManager();
        try
        {
            await LifecycleTools.DebugLaunch(mgr, TestPaths.SampleAppDll);
            await BreakpointTools.SetBreakpoint(mgr, TestPaths.SampleAppSrc, TestPaths.BreakpointLine);
            var fb = await BreakpointTools.SetFunctionBreakpoint(mgr, "Calc.Compute");
            Assert.Contains(await BreakpointTools.ListFunctionBreakpoints(mgr), b => b.Id == fb.Id);

            var msg = await BreakpointTools.ClearAllBreakpoints(mgr);
            Assert.Contains("2", msg); // "cleared 2 breakpoint(s)" — one source + one function
            Assert.Empty(await BreakpointTools.ListBreakpoints(mgr));
            Assert.Empty(await BreakpointTools.ListFunctionBreakpoints(mgr));
        }
        finally { await Cleanup(mgr); }
    }

    [Fact]
    public async Task Set_exception_breakpoints_succeeds()
    {
        var mgr = await StartStoppedAsync();
        try
        {
            var result = await BreakpointTools.SetExceptionBreakpoints(mgr, new[] { "all" });
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
            var bps = await BreakpointTools.ListBreakpoints(mgr);
            foreach (var b in bps) await BreakpointTools.RemoveBreakpoint(mgr, b.Id);

            await ExecutionTools.Continue(mgr, null, 500); // times out -> running freely
            await ExecutionTools.Pause(mgr);

            var stop = await ExecutionTools.WaitForAnyStop(mgr, 5000);
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
            var info = await LifecycleTools.DebugAttach(mgr, proc.Id);
            Assert.Equal(proc.Id, info.Pid);
            var status = LifecycleTools.DebugStatus(mgr, info.SessionId);
            Assert.Equal(info.SessionId, status.SessionId);
        }
        finally
        {
            await Cleanup(mgr);
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
        }
    }

    [Fact(Timeout = 40000)]
    public async Task Attach_to_a_dead_pid_surfaces_a_real_error()
    {
        // Regression guard: a rejected attach used to be fire-and-forget, so its
        // DapException became an unobserved exception and the caller saw either a
        // hang or a generic message. The engine must now surface a concrete reason
        // (netcoredbg's rejection, or a ready-timeout) and never hang.
        var mgr = NewManager();
        try
        {
            int deadPid = FindUnusedPid();
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => LifecycleTools.DebugAttach(mgr, deadPid));
            Assert.True(ex is DapException or TimeoutException,
                $"expected a DAP/timeout failure, got {ex.GetType().Name}: {ex.Message}");
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
        finally { await Cleanup(mgr); }
    }

    /// <summary>A process id that is not currently in use, so attaching to it must fail.</summary>
    static int FindUnusedPid()
    {
        for (int candidate = 999_000; candidate > 1000; candidate -= 7)
        {
            try { using var _ = Process.GetProcessById(candidate); }
            catch (ArgumentException) { return candidate; } // no such process
        }
        throw new InvalidOperationException("could not find an unused pid for the test.");
    }

    [Fact]
    public async Task Multiple_concurrent_sessions()
    {
        var mgr = NewManager();
        try
        {
            var a = await LifecycleTools.DebugLaunch(mgr, TestPaths.SampleAppDll);
            await LifecycleTools.DebugLaunch(mgr, TestPaths.SampleAppDll);
            Assert.Equal(2, LifecycleTools.ListSessions(mgr).Count);

            await LifecycleTools.DebugStop(mgr, a.SessionId);
            Assert.Single(LifecycleTools.ListSessions(mgr));
        }
        finally { await Cleanup(mgr); }
    }

    [Fact]
    public async Task Auto_attach_discovers_a_child_process_and_ends_it_with_the_launched_tree()
    {
        var mgr = NewManager();
        var opts = new ClrVoyant.Server.AutoAttachOptions { Enabled = true };
        var watcher = new ClrVoyant.Server.ChildProcessWatcher(mgr, opts);
        Process? child = null;
        try
        {
            // ParentApp spawns `dotnet SampleApp.dll` as a child .NET process.
            var parent = await LifecycleTools.DebugLaunch(mgr, TestPaths.ParentAppDll, new[] { TestPaths.SampleAppDll });

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline && mgr.List().Count < 2)
            {
                await watcher.ScanOnceAsync();
                await Task.Delay(300);
            }
            var sessions = mgr.List();
            Assert.Equal(2, sessions.Count); // parent + auto-attached child

            // The attached session is the real child of the launched parent, not some other process.
            var attached = sessions.Single(s => s.SessionId != parent.SessionId);
            Assert.Contains(attached.Pid,
                ClrVoyant.Server.ProcessLister.Enumerate().Where(p => p.parentPid == parent.Pid).Select(p => p.pid));
            child = Process.GetProcessById(attached.Pid);
        }
        finally { await Cleanup(mgr); }

        // The child belongs to the process tree the server started: stopping its session ends it.
        Assert.NotNull(child);
        using (child)
            Assert.True(child.WaitForExit(15000), "the auto-attached child was left running after its session ended");
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
