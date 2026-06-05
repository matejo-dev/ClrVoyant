using ClrVoyant.Core;
using ClrVoyant.Dap;
using ClrVoyant.Server.Tools;

namespace ClrVoyant.Tests.Integration;

/// <summary>
/// Pins down the no-symbols boundary: with the DLLs/EXE but NO PDB, netcoredbg cannot
/// place breakpoints (it resolves them through symbols), so neither a function nor a
/// source breakpoint binds and a resumed target never stops on them. (The ClrMD heap/
/// Task view still works after a pause, since it reads runtime metadata, not the PDB —
/// covered by the inspection tests and demonstrated manually.) This is the regression
/// guard behind the README's "PDB is what matters" matrix.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NoPdbDebuggingTests
{
    [Fact]
    public async Task Without_a_pdb_breakpoints_do_not_bind_or_hit()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"clrvoyant-nopdb-{Guid.NewGuid():N}");
        var mgr = new SessionManager(() => new DapEngine(TestPaths.Netcoredbg));
        try
        {
            CopyWithoutPdb(Path.GetDirectoryName(TestPaths.SampleAppDll)!, dir);
            string dll = Path.Combine(dir, "SampleApp.dll");
            Assert.False(File.Exists(Path.ChangeExtension(dll, ".pdb")), "the PDB must be absent for this test");

            await DebugTools.DebugLaunch(mgr, dll);

            var fb = await DebugTools.SetFunctionBreakpoint(mgr, "Calc.Compute");
            Assert.False(fb.Verified, "a function breakpoint cannot bind without a PDB");

            var lb = await DebugTools.SetBreakpoint(mgr, TestPaths.SampleAppSrc, content: "int offset = squared + 7;");
            Assert.False(lb.Verified, "a source breakpoint cannot bind without a PDB");

            // Nothing is bound, so the target keeps running and the resume times out.
            var outcome = await DebugTools.Continue(mgr, null, 4000);
            Assert.Equal(SessionState.Running, outcome.State);
            Assert.Null(outcome.Stop);
        }
        finally
        {
            foreach (var s in mgr.List()) { try { await mgr.StopAsync(s.SessionId); } catch { } }
            await mgr.DisposeAsync();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    static void CopyWithoutPdb(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            if (string.Equals(Path.GetExtension(file), ".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
    }
}
