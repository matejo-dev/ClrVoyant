using ClrVoyant.Core;
using ClrVoyant.Tests.Fakes;

namespace ClrVoyant.Tests;

/// <summary>
/// The wait model of <see cref="Session"/> when events happen LATER than the command that
/// provoked them (the fake stays quiet until the test raises them), and what happens to
/// events nobody is waiting for. Every wait here is bounded.
/// </summary>
public class SessionEventTests
{
    static readonly TimeSpan Long = TimeSpan.FromSeconds(8);
    static readonly StopLocation Loc1 = new("a.cs", 1, "breakpoint", 1);
    static readonly StopLocation Loc2 = new("b.cs", 2, "breakpoint", 1);

    sealed class Rig
    {
        public FakeDebugEngine Fake { get; } = new() { StopOnResume = null }; // quiet until told
        public Session Session { get; }
        public List<(SessionStop stop, long stopId)> Spontaneous { get; } = new();

        public Rig()
        {
            Session = new Session("s1", Fake);
            Session.SpontaneousStop += (_, stop, id) => Spontaneous.Add((stop, id));
        }

        /// <summary>Start a continue and return once the engine has the command (the waiter is armed).</summary>
        public async Task<Task<StopOutcome>> ContinueArmedAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        {
            var pending = Session.ContinueAsync(null, timeout ?? Long, ct);
            await Fake.ResumeCalledAsync();
            return pending;
        }
    }

    // ---- late events complete the waiter ----

    [Fact]
    public async Task A_stop_that_arrives_after_the_command_completes_the_wait_and_is_not_spontaneous()
    {
        var rig = new Rig();
        var pending = await rig.ContinueArmedAsync();

        await Task.Run(() => rig.Fake.RaiseStopped(Loc1)); // another thread, later

        var outcome = await pending.WaitAsync(Long);
        Assert.Equal(SessionState.Stopped, outcome.State);
        Assert.Equal("Enriched.cs", outcome.Stop!.File);
        Assert.Empty(rig.Spontaneous);
    }

    [Fact]
    public async Task An_exit_after_the_command_completes_the_wait_with_the_exit_code()
    {
        var rig = new Rig();
        var pending = await rig.ContinueArmedAsync();

        await Task.Run(() => rig.Fake.RaiseExited(3));

        var outcome = await pending.WaitAsync(Long);
        Assert.Equal(SessionState.Exited, outcome.State);
        Assert.Equal(3, outcome.ExitCode);
        Assert.Empty(rig.Spontaneous);
    }

    [Fact]
    public async Task Losing_the_debugger_connection_ends_a_waiting_resume_promptly_and_says_so()
    {
        var rig = new Rig();
        var pending = await rig.ContinueArmedAsync(); // would wait 30s if the loss went unnoticed

        await Task.Run(() => rig.Fake.RaiseFaulted("netcoredbg exited with code 139"));

        var outcome = await pending.WaitAsync(Long);
        Assert.Equal(SessionState.Faulted, outcome.State);
        Assert.Contains("debugger connection was lost", outcome.Message);
        Assert.Contains("netcoredbg exited with code 139", outcome.Message);
    }

    // ---- a resume that does not complete must not leave a waiter that swallows the next stop ----

    [Fact]
    public async Task A_rejected_resume_does_not_swallow_the_next_stop()
    {
        var rig = new Rig();
        rig.Fake.ResumeFailure = new InvalidOperationException("Thread is not stopped");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.ContinueAsync(null, Long));
        rig.Fake.RaiseStopped(Loc1);

        Assert.Equal(Loc1, Assert.Single(rig.Spontaneous).stop.Stop);
    }

    [Fact]
    public async Task A_timed_out_resume_disarms_so_the_later_stop_is_spontaneous()
    {
        var rig = new Rig();

        var outcome = await rig.Session.ContinueAsync(null, TimeSpan.Zero);
        rig.Fake.RaiseStopped(Loc1);

        Assert.Equal(SessionState.Running, outcome.State);
        Assert.Single(rig.Spontaneous);
    }

    [Fact]
    public async Task Cancellation_surfaces_as_cancellation_not_as_a_timeout_and_disarms_the_waiter()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        var pending = await rig.ContinueArmedAsync(ct: cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Long));
        rig.Fake.RaiseStopped(Loc1);
        Assert.Single(rig.Spontaneous);
    }

    [Fact]
    public async Task A_resume_with_an_already_cancelled_token_sends_nothing()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Session.ContinueAsync(null, Long, cts.Token));

        Assert.Equal(0, rig.Fake.ContinueCalls);
    }

    // ---- one resume at a time ----

    [Fact]
    public async Task A_second_resume_while_one_is_in_flight_is_rejected_and_the_first_still_completes()
    {
        var rig = new Rig();
        var first = await rig.ContinueArmedAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.StepAsync(StepKind.Over, 1, TimeSpan.Zero));

        Assert.Contains("already has a continue/step in progress", ex.Message);
        Assert.Empty(rig.Fake.Steps); // the rejected one never reached the engine
        rig.Fake.RaiseStopped(Loc1);
        Assert.Equal(SessionState.Stopped, (await first.WaitAsync(Long)).State);
    }

    // ---- events nobody waits for ----

    [Fact]
    public void An_exit_while_nobody_waits_is_reported_with_its_exit_code()
    {
        var rig = new Rig();

        rig.Fake.RaiseExited(7);

        var (stop, _) = Assert.Single(rig.Spontaneous);
        Assert.Equal(new SessionStop("s1", null, SessionState.Exited, 7), stop);
        Assert.Equal(7, rig.Session.ExitCode);
    }

    [Fact]
    public void Losing_the_connection_while_nobody_waits_is_reported()
    {
        var rig = new Rig();

        rig.Fake.RaiseFaulted("pipe closed");

        var (stop, _) = Assert.Single(rig.Spontaneous);
        Assert.Equal(SessionState.Faulted, stop.State);
        Assert.Contains("pipe closed", stop.Message);
    }

    [Fact]
    public async Task Resuming_a_faulted_or_exited_session_fails_without_sending_anything()
    {
        var faulted = new Rig();
        faulted.Fake.RaiseFaulted("pipe closed");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => faulted.Session.ContinueAsync(null, Long));
        Assert.Contains("lost its debugger connection (pipe closed)", ex.Message);
        Assert.Equal(0, faulted.Fake.ContinueCalls);

        var exited = new Rig();
        exited.Fake.RaiseExited(9);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => exited.Session.ContinueAsync(null, Long));
        Assert.Contains("has exited with code 9", ex.Message);
        Assert.Equal(0, exited.Fake.ContinueCalls);
    }

    [Fact]
    public async Task Introspection_on_a_faulted_session_explains_the_lost_connection()
    {
        var rig = new Rig();
        rig.Fake.RaiseFaulted("pipe closed");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.GetThreadsAsync());

        Assert.Contains("lost its debugger connection (pipe closed)", ex.Message);
        Assert.Contains("debug_stop", ex.Message);
    }

    // ---- IsCurrent: which queued events still describe the session ----

    [Fact]
    public void A_stop_is_current_until_a_later_stop_replaces_it()
    {
        var rig = new Rig();
        rig.Fake.RaiseStopped(Loc1);
        rig.Fake.RaiseStopped(Loc2);

        Assert.False(rig.Session.IsCurrent(rig.Spontaneous[0].stop, rig.Spontaneous[0].stopId));
        Assert.True(rig.Session.IsCurrent(rig.Spontaneous[1].stop, rig.Spontaneous[1].stopId));
    }

    [Fact]
    public async Task A_stop_is_no_longer_current_once_the_target_runs_again()
    {
        var rig = new Rig();
        rig.Fake.RaiseStopped(Loc1);
        var (stop, id) = Assert.Single(rig.Spontaneous);
        Assert.True(rig.Session.IsCurrent(stop, id));

        await rig.Session.ContinueAsync(null, TimeSpan.Zero); // resumes; nothing stops it

        Assert.False(rig.Session.IsCurrent(stop, id));
    }

    [Fact]
    public async Task An_enrichment_that_finishes_late_does_not_overwrite_a_newer_stop()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Fake.StackTraceGate = gate.Task;
        rig.Fake.RaiseStopped(Loc1);
        var enriching = rig.Session.EnrichAsync(Loc1);
        await rig.Fake.StackTraceCalledAsync();

        rig.Fake.RaiseStopped(Loc2); // the target moved on while the stack was being read
        gate.SetResult();
        await enriching.WaitAsync(Long);

        Assert.Equal(Loc2, rig.Session.LastStop);
    }
}
