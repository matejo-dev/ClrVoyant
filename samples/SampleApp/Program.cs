using System.Diagnostics;

// A small, long-lived target for ClrVoyant integration tests. It holds a few
// Tasks in distinct states (rooted so they survive), then loops computing values
// in a method with locals. Useful for breakpoints, variable inspection, and the
// ClrMD Task enumeration.
var roots = new List<object>();

// WaitingForActivation: an async method awaiting a TCS that never completes.
var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
roots.Add(gate);
roots.Add(AwaitForever(gate.Task));

// Faulted: observed so it doesn't crash on exit.
var faulted = Task.Run(() => throw new InvalidOperationException("boom"));
try { await Task.WhenAny(faulted); } catch { }
roots.Add(faulted);

await Task.Delay(200); // let the awaits park

Console.WriteLine($"SampleApp PID={Process.GetCurrentProcess().Id}");

// Test hook: announce the PID the way a VSTEST_HOST_DEBUG test host does, so the debug_test flow
// can be driven with this app standing in for a real test run.
if (Environment.GetEnvironmentVariable("SAMPLE_ANNOUNCE_TESTHOST") is not null)
    Console.WriteLine($"Process Id: {Environment.ProcessId}, Name: SampleApp");

// Test hook: when set, exit right away with this code, so tests can check that the
// debugger reports the exit code and the stdout printed above.
if (Environment.GetEnvironmentVariable("SAMPLE_EXIT_CODE") is { } exitCode)
    return int.Parse(exitCode);

int i = 0;
while (true)
{
    int result = Calc.Compute(i);
    Console.WriteLine($"tick {i} -> {result}");
    await Task.Delay(500);
    i++;
}

static async Task<int> AwaitForever(Task<int> gate) => await gate + 1;

// A real method on a named type (NOT a top-level local function, whose compiled name
// is mangled): integration tests break here both by source line (the marker below)
// and by function name ("Calc.Compute"), the no-source path.
static class Calc
{
    public static int Compute(int n)
    {
        int squared = n * n;
        int offset = squared + 7;   // BREAKPOINT-TARGET
        return offset;
    }
}
