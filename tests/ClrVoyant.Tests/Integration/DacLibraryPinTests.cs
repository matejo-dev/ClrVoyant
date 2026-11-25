using System.Diagnostics;
using ClrVoyant.Inspection;

namespace ClrVoyant.Tests.Integration;

public class DacLibraryPinTests
{
    // Regression guard for a native crash: ClrMD unloads the DAC when a snapshot is disposed, and the
    // DAC's PAL leaves a per-thread destructor pointing into it, so any thread that touched the DAC
    // took the whole process down when it later exited (intermittently, in an unrelated test). The
    // fix is to keep the DAC mapped; this asserts that it still is after the inspector is gone.
    [UnixFact(Timeout = 90000)]
    public async Task The_dac_stays_loaded_after_the_inspector_that_used_it_is_disposed()
    {
        if (!OperatingSystem.IsLinux()) return; // [UnixFact] skips Windows; the check below reads /proc, which is Linux

        using var target = Process.Start(new ProcessStartInfo("dotnet", $"\"{TestPaths.SampleAppDll}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        try
        {
            // Wait for the app to announce itself (runtime up, Tasks parked), then freeze it: the
            // DAC must only ever read a stopped target, as it does under the debugger.
            while (await target.StandardOutput.ReadLineAsync() is { } line && !line.StartsWith("SampleApp PID=", StringComparison.Ordinal)) { }
            using (var stop = Process.Start("kill", $"-STOP {target.Id}")!) await stop.WaitForExitAsync();

            using (var inspector = new TaskInspector())
                Assert.NotEmpty(inspector.EnumerateTasks(target.Id, 1, null, 0, 10).Items);

            Assert.Contains(File.ReadLines("/proc/self/maps"), l => l.EndsWith("/libmscordaccore.so", StringComparison.Ordinal));
        }
        finally { try { target.Kill(true); } catch { } }
    }
}
