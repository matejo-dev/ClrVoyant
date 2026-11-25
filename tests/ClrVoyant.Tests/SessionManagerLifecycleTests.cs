using ClrVoyant.Core;
using ClrVoyant.Tests.Fakes;

namespace ClrVoyant.Tests;

/// <summary>
/// How <see cref="SessionManager"/> keeps its books when sessions fail to start, restart
/// badly, end, or leave events behind for wait_for_any_stop.
/// </summary>
public class SessionManagerLifecycleTests
{
    static readonly TimeSpan Long = TimeSpan.FromSeconds(8);
    static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200); // for waits that must come back empty

    sealed class Rig
    {
        public List<FakeDebugEngine> Engines { get; } = new();
        /// <summary>Applied to every engine the manager creates from now on.</summary>
        public Action<FakeDebugEngine>? Configure { get; set; }
        public SessionManager Mgr { get; }

        public Rig() => Mgr = new SessionManager(() =>
        {
            var f = new FakeDebugEngine();
            Configure?.Invoke(f);
            Engines.Add(f);
            return f;
        });
    }

    // ---- a failed start leaves no trace ----

    [Fact]
    public async Task A_failed_launch_leaves_no_session_restores_the_previous_active_one_and_kills_its_engine()
    {
        var rig = new Rig();
        await rig.Mgr.LaunchAsync(new LaunchRequest("ok.dll")); // s1
        rig.Configure = f => f.LaunchFailure = new FileNotFoundException("no such program");

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => rig.Mgr.LaunchAsync(new LaunchRequest("bad.dll")));

        Assert.Equal("no such program", ex.Message);
        Assert.Equal("s1", Assert.Single(rig.Mgr.List()).SessionId);
        Assert.Equal("s1", rig.Mgr.Resolve(null).Id);
        var failed = rig.Engines[1];
        Assert.Equal(1, failed.DisposeCalls);
        Assert.True(failed.LastTerminateDebuggee); // it may have started a process: end it
    }

    [Fact]
    public async Task A_failed_attach_detaches_instead_of_killing_and_leaves_no_session()
    {
        var rig = new Rig();
        rig.Configure = f => f.AttachFailure = new InvalidOperationException("attach denied");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Mgr.AttachAsync(4242));

        Assert.Empty(rig.Mgr.List()); // so a watcher does not see 4242 as already handled
        var failed = Assert.Single(rig.Engines);
        Assert.Equal(1, failed.DisposeCalls);
        Assert.False(failed.LastTerminateDebuggee); // never kill a process we only tried to attach to
        Assert.Throws<InvalidOperationException>(() => rig.Mgr.Resolve(null)); // no dangling active id
    }

    [Fact]
    public async Task A_stop_that_fires_during_a_start_that_then_fails_is_not_reported()
    {
        var rig = new Rig();
        rig.Configure = f =>
        {
            f.LaunchFailure = new InvalidOperationException("ready timeout");
            f.OnLaunch = () => f.RaiseStopped(new("x.cs", 1, "entry", 1));
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Mgr.LaunchAsync(new LaunchRequest("bad.dll")));

        Assert.Null(await rig.Mgr.WaitForAnyStopAsync(Short));
    }

    // ---- restart ----

    [Fact]
    public async Task A_failed_restart_leaves_an_exited_session_with_its_breakpoints_and_says_so()
    {
        var rig = new Rig();
        await rig.Mgr.LaunchAsync(new LaunchRequest("a.dll"));
        await rig.Mgr.Resolve("s1").AddBreakpointAsync("P.cs", new BreakpointRequest(5));
        rig.Configure = f => f.LaunchFailure = new IOException("netcoredbg did not become ready");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Mgr.RestartAsync("s1"));

        Assert.Contains("netcoredbg did not become ready", ex.Message);
        Assert.Contains("now Exited", ex.Message);
        Assert.Contains("restart_debugging", ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
        var failedNew = rig.Engines[1];
        Assert.Equal(1, failedNew.DisposeCalls); // no half-started engine left alive
        var info = Assert.Single(rig.Mgr.List());
        Assert.Equal(SessionState.Exited, info.State);
        Assert.Single(await rig.Mgr.Resolve("s1").ListBreakpointsAsync());

        // ...and the session is still restartable once the cause is gone.
        rig.Configure = null;
        var again = await rig.Mgr.RestartAsync("s1");
        Assert.Equal(SessionState.Running, again.State);
        Assert.Equal(5, Assert.Single(Assert.Single(rig.Engines[2].SetBreakpointCalls).bps).Line);
    }

    [Fact]
    public async Task A_restart_cancelled_midway_stays_a_cancellation_and_tears_the_new_engine_down()
    {
        var rig = new Rig();
        await rig.Mgr.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Configure = f => f.LaunchFailure = new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Mgr.RestartAsync("s1"));

        Assert.Equal(1, rig.Engines[1].DisposeCalls);
    }

    // ---- stop ----

    [Fact]
    public async Task A_stop_whose_engine_fails_still_removes_and_disposes_the_session_and_its_resource()
    {
        var rig = new Rig();
        var info = await rig.Mgr.AttachAsync(4242);
        var owned = new Flag();
        rig.Mgr.Resolve(info.SessionId).OwnedResource = owned;
        rig.Engines[0].TerminateFailure = new IOException("adapter is gone");

        await Assert.ThrowsAsync<IOException>(() => rig.Mgr.StopAsync(info.SessionId));

        Assert.Empty(rig.Mgr.List());
        Assert.Equal(1, rig.Engines[0].DisposeCalls);
        Assert.True(owned.Disposed);
    }

    [Fact]
    public async Task When_the_active_session_ends_with_several_left_none_is_guessed()
    {
        var rig = new Rig();
        await rig.Mgr.LaunchAsync(new LaunchRequest("a.dll")); // s1
        await rig.Mgr.LaunchAsync(new LaunchRequest("b.dll")); // s2
        await rig.Mgr.LaunchAsync(new LaunchRequest("c.dll")); // s3, active

        await rig.Mgr.StopAsync("s3");

        var ex = Assert.Throws<InvalidOperationException>(() => rig.Mgr.Resolve(null));
        Assert.Contains("s1, s2", ex.Message);
        Assert.Equal("s2", rig.Mgr.Resolve("s2").Id); // explicit ids keep working
    }

    // ---- wait_for_any_stop ----

    [Fact]
    public async Task An_exit_nobody_was_waiting_for_is_reported_by_wait_for_any_stop()
    {
        var rig = new Rig();
        await rig.Mgr.LaunchAsync(new LaunchRequest("a.dll"));
        await rig.Mgr.LaunchAsync(new LaunchRequest("b.dll"));

        rig.Engines[0].RaiseExited(3);

        var e = await rig.Mgr.WaitForAnyStopAsync(Long);
        Assert.Equal(new SessionStop("s1", null, SessionState.Exited, 3), e);
    }

    [Fact]
    public async Task A_connection_lost_while_nobody_was_waiting_is_reported_by_wait_for_any_stop()
    {
        var rig = new Rig();
        await rig.Mgr.LaunchAsync(new LaunchRequest("a.dll"));

        rig.Engines[0].RaiseFaulted("pipe closed");

        var e = await rig.Mgr.WaitForAnyStopAsync(Long);
        Assert.Equal(SessionState.Faulted, e!.State);
        Assert.Contains("pipe closed", e.Message);
    }

    [Fact]
    public async Task A_stop_the_agent_already_resumed_past_is_not_reported_later()
    {
        var rig = new Rig();
        await rig.Mgr.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Engines[0].RaiseStopped(new("a.cs", 1, "breakpoint", 1)); // queued
        rig.Engines[0].StopOnResume = null;

        await rig.Mgr.Resolve("s1").ContinueAsync(null, TimeSpan.Zero); // running again

        Assert.Null(await rig.Mgr.WaitForAnyStopAsync(Short));
    }

    [Fact]
    public async Task A_stop_replaced_by_a_later_stop_of_the_same_session_is_not_reported()
    {
        var rig = new Rig();
        await rig.Mgr.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Engines[0].RaiseStopped(new("a.cs", 1, "breakpoint", 1)); // queued
        // The agent continues; the next stop is delivered to that continue, not queued.
        await rig.Mgr.Resolve("s1").ContinueAsync(null, Long);

        // The session is Stopped again, but the queued event describes the previous stop.
        Assert.Null(await rig.Mgr.WaitForAnyStopAsync(Short));
    }

    [Fact]
    public async Task Events_of_a_session_that_was_stopped_or_restarted_are_not_reported_but_others_are()
    {
        var rig = new Rig();
        await rig.Mgr.LaunchAsync(new LaunchRequest("a.dll")); // s1: will be stopped
        await rig.Mgr.LaunchAsync(new LaunchRequest("b.dll")); // s2: will be restarted
        await rig.Mgr.LaunchAsync(new LaunchRequest("c.dll")); // s3: stays
        rig.Engines[0].RaiseStopped(new("a.cs", 1, "breakpoint", 1));
        rig.Engines[1].RaiseExited(1);
        rig.Engines[2].RaiseStopped(new("c.cs", 3, "breakpoint", 1));
        await rig.Mgr.StopAsync("s1");
        await rig.Mgr.RestartAsync("s2");

        var e = await rig.Mgr.WaitForAnyStopAsync(Long);

        Assert.Equal("s3", e!.SessionId);
        Assert.Null(await rig.Mgr.WaitForAnyStopAsync(Short));
    }

    [Fact]
    public async Task Cancelling_the_wait_is_cancellation_not_a_timeout()
    {
        var rig = new Rig();
        await rig.Mgr.LaunchAsync(new LaunchRequest("a.dll"));
        using var cts = new CancellationTokenSource();
        var waiting = rig.Mgr.WaitForAnyStopAsync(Long, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    // ---- the process-ended signal other layers hook to drop per-pid caches ----

    [Fact]
    public async Task DebuggeeEnded_names_the_pid_when_a_process_exits_is_replaced_or_its_session_stops()
    {
        var rig = new Rig();
        var ended = new List<int>();
        rig.Mgr.DebuggeeEnded += ended.Add;
        var attached = await rig.Mgr.AttachAsync(4242);
        rig.Engines[0].RaiseExited(0);
        Assert.Equal(new[] { 4242 }, ended);               // the process exited

        await rig.Mgr.StopAsync(attached.SessionId);
        Assert.Equal(new[] { 4242, 4242 }, ended);         // the session went away

        await rig.Mgr.LaunchAsync(new LaunchRequest("a.dll")); // fake pid 1234
        ended.Clear();
        await rig.Mgr.RestartAsync("s2");
        Assert.Equal(new[] { 1234 }, ended);               // the old process was replaced
    }

    [Fact]
    public async Task Disposing_the_manager_disposes_every_engine_even_when_one_fails()
    {
        var rig = new Rig();
        rig.Configure = f => f.DisposeFailure = new IOException("boom");
        await rig.Mgr.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Configure = null;
        await rig.Mgr.LaunchAsync(new LaunchRequest("b.dll"));

        await rig.Mgr.DisposeAsync();

        Assert.All(rig.Engines, e => Assert.Equal(1, e.DisposeCalls));
        Assert.Empty(rig.Mgr.List());
    }

    sealed class Flag : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
