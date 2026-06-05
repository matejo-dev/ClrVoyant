using System.Diagnostics;
using ClrVoyant.Core;
using ClrVoyant.Dap;
using ClrVoyant.Server.Tools;

namespace ClrVoyant.Tests.Integration;

/// <summary>
/// Pins down the "optimized builds break debugging" assumption. netcoredbg disables
/// JIT optimization on module load, so an assembly compiled with optimizations ON —
/// as long as it ships a PDB — still binds line breakpoints at the right line and
/// exposes live locals. We build SampleApp with Optimize=true (overriding the
/// sample's debug-friendly default) and drive a real loop to prove it. This is the
/// regression guard behind the README's "PDB is what matters, not Debug-vs-Release".
/// </summary>
[Trait("Category", "Integration")]
public sealed class OptimizedBuildTests
{
    [Fact]
    public async Task Optimized_build_with_pdb_binds_line_breakpoint_and_exposes_locals()
    {
        string outDir = Path.Combine(Path.GetTempPath(), $"clrvoyant-opt-{Guid.NewGuid():N}");
        string csproj = Path.Combine(TestPaths.RepoRoot, "samples", "SampleApp", "SampleApp.csproj");
        var mgr = new SessionManager(() => new DapEngine(TestPaths.Netcoredbg));
        try
        {
            BuildOptimized(csproj, outDir);
            string dll = Path.Combine(outDir, "SampleApp.dll");
            Assert.True(File.Exists(Path.ChangeExtension(dll, ".pdb")), "optimized build must still ship a PDB");

            await DebugTools.DebugLaunch(mgr, dll);
            // Source breakpoint resolved by content (so it tracks the marker line).
            var bp = await DebugTools.SetBreakpoint(mgr, TestPaths.SampleAppSrc, content: "int offset = squared + 7;");
            Assert.True(bp.Verified, "line breakpoint must bind on the optimized build");

            var stop = await DebugTools.Continue(mgr, null, 20000);
            Assert.Equal(SessionState.Stopped, stop.State);
            Assert.Equal(bp.Line, stop.Stop!.Line);

            // Locals survive optimization here (they are all used): names + values readable.
            var frames = await DebugTools.GetCallstack(mgr, null, 0, 1);
            var scopes = await DebugTools.GetScopes(mgr, frames[0].Id);
            var locals = await DebugTools.GetVariables(mgr, scopes[0].VariablesReference);
            Assert.Contains(locals, v => v.Name == "n");
            Assert.Contains(locals, v => v.Name == "squared");
        }
        finally
        {
            foreach (var s in mgr.List()) { try { await mgr.StopAsync(s.SessionId); } catch { } }
            await mgr.DisposeAsync();
            try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
        }
    }

    static void BuildOptimized(string csproj, string outDir)
    {
        // -p:Optimize=true overrides the sample's <Optimize>false>; keep a portable PDB.
        var psi = new ProcessStartInfo("dotnet",
            $"build \"{csproj}\" -c Release -p:Optimize=true -p:DebugType=portable -p:DebugSymbols=true -o \"{outDir}\" --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120000)) { try { p.Kill(true); } catch { } throw new TimeoutException("optimized build timed out"); }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"optimized build failed (exit {p.ExitCode}):\n{outp.Result}\n{err.Result}");
    }
}
