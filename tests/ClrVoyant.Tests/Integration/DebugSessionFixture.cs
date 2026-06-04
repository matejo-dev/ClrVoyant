using ClrVoyant.Core;
using ClrVoyant.Dap;
using ClrVoyant.Inspection;
using ClrVoyant.Server.Tools;

namespace ClrVoyant.Tests.Integration;

/// <summary>
/// Launches SampleApp under a real netcoredbg-backed engine and stops it at the
/// BREAKPOINT-TARGET line. Shared (IClassFixture) across read-only inspection
/// tests so we pay the launch cost once.
/// </summary>
public sealed class DebugSessionFixture : IAsyncLifetime
{
    public SessionManager Mgr { get; private set; } = default!;
    public TaskInspector Inspector { get; } = new();
    public StopOutcome Stop { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        Mgr = new SessionManager(() => new DapEngine(TestPaths.Netcoredbg));
        await DebugTools.DebugLaunch(Mgr, TestPaths.SampleAppDll);
        await DebugTools.SetBreakpoint(Mgr, TestPaths.SampleAppSrc, TestPaths.BreakpointLine);
        Stop = await DebugTools.Continue(Mgr, threadId: null, timeoutMs: 20000);
    }

    public async Task DisposeAsync()
    {
        try { await DebugTools.DebugStop(Mgr); } catch { }
        await Mgr.DisposeAsync();
        Inspector.Dispose();
    }
}
