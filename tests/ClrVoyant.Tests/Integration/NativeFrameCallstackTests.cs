using ClrVoyant.Core;
using ClrVoyant.Dap;
using ClrVoyant.Server.Tools;

namespace ClrVoyant.Tests.Integration;

/// <summary>
/// Regression guard for GitHub issue #1: a call stack that crosses native code. The
/// WinForms target breaks in <c>OnLoad</c>, which the native window procedure calls
/// from inside the framework's <c>ShowWindow</c> P/Invoke. On newer desktop runtimes
/// that P/Invoke frame is managed but not IL, and netcoredbg before 3.2.0 aborted the
/// whole stack walk on it: <c>get_callstack</c> failed (0x80004002) and the stop lost
/// its file/line. The target rolls forward to the newest installed desktop runtime,
/// where that frame appears.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NativeFrameCallstackTests
{
    [WindowsFact]
    public async Task Callstack_walks_through_native_window_procedure_frames()
    {
        var mgr = new SessionManager(() => new DapEngine(TestPaths.Netcoredbg));
        try
        {
            await LifecycleTools.DebugLaunch(mgr, TestPaths.WinFormsAppDll);
            var bp = await BreakpointTools.SetBreakpoint(mgr, TestPaths.WinFormsAppSrc, content: "int loadMarker = 42;");

            var outcome = await ExecutionTools.Continue(mgr, null, 30000);
            Assert.Equal(SessionState.Stopped, outcome.State);
            // The stop is enriched from the top stack frame, so it carries a location
            // only if the stack walk itself succeeds.
            Assert.Equal(bp.Line, outcome.Stop!.Line);

            var stack = await InspectionTools.GetCallstack(mgr, null, 0, 50);
            Assert.Contains("OnLoad", stack[0].Function);
            // Frames on BOTH sides of the native transition: the walk got past it.
            Assert.Contains(stack, f => f.Function.Contains("NativeWindow.Callback", StringComparison.Ordinal));
            Assert.Contains(stack, f => f.Function.Contains("SetVisibleCore", StringComparison.Ordinal));
            Assert.Contains(stack, f => f.Function.Contains("Program.Main", StringComparison.Ordinal) && f.File is not null);
        }
        finally
        {
            foreach (var s in mgr.List()) { try { await mgr.StopAsync(s.SessionId); } catch { } }
            await mgr.DisposeAsync();
        }
    }
}
