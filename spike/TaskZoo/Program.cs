using System.Diagnostics;

// TaskZoo: a target process that holds Tasks in several distinct states on the
// heap, so the ClrMD probe can try to enumerate and classify them.
//
// We deliberately keep strong references to every Task in a static list so the
// GC cannot collect them while the probe attaches.

static class Zoo
{
    // Keep everything rooted so it survives until the probe snapshots us.
    public static readonly List<object> Roots = new();

    static async Task Main()
    {
        // 1) Running: a CPU-bound task that never finishes (status Running).
        var running = Task.Run(() =>
        {
            ulong x = 0;
            while (true) { x++; if (x == ulong.MaxValue) x = 0; }
        });
        Roots.Add(running);

        // 2) WaitingForActivation via an awaited TaskCompletionSource that is
        //    never completed (classic "hung await").
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Roots.Add(tcs);
        var awaitingTcs = AwaitForever(tcs.Task);
        Roots.Add(awaitingTcs);

        // 3) An async method parked on Task.Delay (timer-backed await).
        var awaitingDelay = AwaitDelay();
        Roots.Add(awaitingDelay);

        // 4) RanToCompletion.
        var completed = Task.FromResult(42);
        Roots.Add(completed);

        // 5) Faulted (exception captured, observed here to avoid crash on exit).
        var faulted = Task.Run(() => throw new InvalidOperationException("boom"));
        try { await Task.WhenAny(faulted); } catch { }
        Roots.Add(faulted);

        // Let the parked awaits actually reach their await point.
        await Task.Delay(300);

        Console.WriteLine($"PID={Process.GetCurrentProcess().Id}");
        Console.WriteLine($"ROOTS={Roots.Count}");
        Console.WriteLine("READY");
        Console.Out.Flush();

        // Block forever; the probe will snapshot us from outside.
        await Task.Delay(Timeout.Infinite);
    }

    // An async state machine that awaits a TCS that never completes.
    static async Task<int> AwaitForever(Task<int> gate)
    {
        var v = await gate;
        return v + 1;
    }

    // An async state machine parked on a long timer.
    static async Task AwaitDelay()
    {
        await Task.Delay(TimeSpan.FromHours(1));
    }
}
