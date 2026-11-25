using ClrVoyant.Core;
using ClrVoyant.Dap;
using ClrVoyant.Tests.Fakes;
using ClrVoyant.Tests.Integration;

namespace ClrVoyant.Tests;

/// <summary>
/// The behaviour <see cref="Session"/> relies on from ANY <see cref="IDebugEngine"/>, asserted
/// against both the in-memory fake (so the fake cannot drift from what the real engine does)
/// and the real DapEngine over netcoredbg. Only properties something depends on are here:
/// <list type="bullet">
/// <item>a resume is followed by a Stopped event, with the state following along;</item>
/// <item>SetBreakpoints answers one result per request, in order (Session maps results by index);</item>
/// <item>Exited is raised exactly once per engine, with the exit code when there is one,
/// whether the debuggee ends by itself or TerminateAsync ends it (Session fans it out once).</item>
/// </list>
/// </summary>
public abstract class EngineContractTests
{
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    protected abstract IDebugEngine Create();
    /// <summary>A debuggee that keeps running and can stop at <see cref="BreakableLine"/> of <see cref="BreakableFile"/>.</summary>
    protected abstract LaunchRequest RunningRequest { get; }
    protected abstract LaunchRequest EndingRequest(int exitCode);
    /// <summary>Hook for engines whose debuggee does not end by itself.</summary>
    protected virtual void MakeEnd(IDebugEngine engine, int exitCode) { }
    protected abstract string BreakableFile { get; }
    protected abstract int BreakableLine { get; }

    [Fact]
    public async Task A_resume_is_followed_by_a_stop_and_the_state_follows()
    {
        await using var engine = Create();
        var stopped = new TaskCompletionSource<StopLocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Stopped += loc => stopped.TrySetResult(loc);
        await engine.LaunchAsync(RunningRequest).WaitAsync(Bound);
        Assert.Equal(SessionState.Running, engine.State);
        await engine.SetBreakpointsAsync(BreakableFile, [new BreakpointRequest(BreakableLine)]).WaitAsync(Bound);

        await engine.ContinueAsync(null).WaitAsync(Bound);
        var stop = await stopped.Task.WaitAsync(Bound);

        Assert.NotNull(stop.ThreadId);
        Assert.Equal(SessionState.Stopped, engine.State);
    }

    [Fact]
    public async Task SetBreakpoints_answers_one_result_per_request_in_order_and_an_empty_set_clears()
    {
        await using var engine = Create();
        await engine.LaunchAsync(RunningRequest).WaitAsync(Bound);

        var two = await engine.SetBreakpointsAsync(BreakableFile,
            [new BreakpointRequest(BreakableLine), new BreakpointRequest(BreakableLine, Condition: "true")]).WaitAsync(Bound);
        var none = await engine.SetBreakpointsAsync(BreakableFile, []).WaitAsync(Bound);

        Assert.Equal(2, two.Count);
        Assert.Equal(BreakableLine, two[0].Line);
        Assert.Empty(none);
    }

    [Fact]
    public async Task A_debuggee_that_ends_by_itself_raises_Exited_once_with_its_code()
    {
        await using var engine = Create();
        var exits = new List<int?>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Exited += code => { lock (exits) exits.Add(code); first.TrySetResult(); };

        await engine.LaunchAsync(EndingRequest(5)).WaitAsync(Bound);
        MakeEnd(engine, 5);
        await first.Task.WaitAsync(Bound);
        await engine.TerminateAsync(true).WaitAsync(Bound); // ending it again must not report it again
        await engine.DisposeAsync(); // nothing can arrive after this

        lock (exits) Assert.Equal(new int?[] { 5 }, exits);
        Assert.Equal(SessionState.Exited, engine.State);
    }

    [Fact]
    public async Task Terminate_raises_Exited_exactly_once_and_leaves_the_engine_exited()
    {
        await using var engine = Create();
        var exits = new List<int?>();
        engine.Exited += code => { lock (exits) exits.Add(code); };
        await engine.LaunchAsync(RunningRequest).WaitAsync(Bound);

        await engine.TerminateAsync(true).WaitAsync(Bound);
        await engine.DisposeAsync(); // nothing can arrive after this

        lock (exits) Assert.Single(exits);
        Assert.Equal(SessionState.Exited, engine.State);
    }
}

public class FakeEngineContractTests : EngineContractTests
{
    protected override IDebugEngine Create() => new FakeDebugEngine();
    protected override LaunchRequest RunningRequest => new("app.dll");
    protected override LaunchRequest EndingRequest(int exitCode) => new("app.dll");
    protected override void MakeEnd(IDebugEngine engine, int exitCode) => ((FakeDebugEngine)engine).RaiseExited(exitCode);
    protected override string BreakableFile => "Program.cs";
    protected override int BreakableLine => 12;
}

[Trait("Category", "Integration")]
public class DapEngineContractTests : EngineContractTests
{
    protected override IDebugEngine Create() => new DapEngine(TestPaths.Netcoredbg);
    protected override LaunchRequest RunningRequest => new(TestPaths.SampleAppDll);
    protected override LaunchRequest EndingRequest(int exitCode) => new(TestPaths.SampleAppDll,
        Env: new Dictionary<string, string> { ["SAMPLE_EXIT_CODE"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    protected override string BreakableFile => TestPaths.SampleAppSrc;
    protected override int BreakableLine => TestPaths.BreakpointLine;
}
