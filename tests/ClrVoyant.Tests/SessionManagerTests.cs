using ClrVoyant.Core;
using ClrVoyant.Tests.Fakes;

namespace ClrVoyant.Tests;

public class SessionManagerTests
{
    static (SessionManager mgr, List<FakeDebugEngine> created) NewManager()
    {
        var created = new List<FakeDebugEngine>();
        var mgr = new SessionManager(() => { var f = new FakeDebugEngine(); created.Add(f); return f; });
        return (mgr, created);
    }

    [Fact]
    public async Task Launch_creates_running_session_and_sets_active()
    {
        var (mgr, created) = NewManager();
        var info = await mgr.LaunchAsync(new LaunchRequest("app.dll"));

        Assert.Equal("s1", info.SessionId);
        Assert.Equal(SessionState.Running, info.State);
        Assert.Single(created);
        Assert.Equal(info.SessionId, mgr.Resolve(null).Id); // active
    }

    [Fact]
    public async Task Attach_uses_given_pid()
    {
        var (mgr, created) = NewManager();
        var info = await mgr.AttachAsync(4242);
        Assert.Equal(4242, info.Pid);
        Assert.Equal(4242, created[0].Pid);
    }

    [Fact]
    public async Task Resolve_unknown_throws_and_empty_throws()
    {
        var (mgr, _) = NewManager();
        Assert.Throws<InvalidOperationException>(() => mgr.Resolve(null)); // no sessions
        await mgr.LaunchAsync(new LaunchRequest("a.dll"));
        Assert.Throws<KeyNotFoundException>(() => mgr.Resolve("nope"));
    }

    [Fact]
    public async Task List_returns_all_sessions()
    {
        var (mgr, _) = NewManager();
        await mgr.LaunchAsync(new LaunchRequest("a.dll"));
        await mgr.LaunchAsync(new LaunchRequest("b.dll"));
        Assert.Equal(2, mgr.List().Count);
    }

    [Fact]
    public async Task Stop_terminates_removes_and_reassigns_active()
    {
        var (mgr, created) = NewManager();
        await mgr.LaunchAsync(new LaunchRequest("a.dll")); // s1
        var s2 = await mgr.LaunchAsync(new LaunchRequest("b.dll")); // s2 active

        await mgr.StopAsync(s2.SessionId);

        Assert.Equal(1, created[1].TerminateCalls);
        Assert.Single(mgr.List());
        Assert.Equal("s1", mgr.Resolve(null).Id); // active fell back to remaining
    }

    [Fact]
    public async Task Restart_swaps_engine_keeps_id_and_reapplies_breakpoints()
    {
        var (mgr, created) = NewManager();
        var launched = await mgr.LaunchAsync(new LaunchRequest("a.dll")); // s1, engine[0]
        await mgr.Resolve("s1").AddBreakpointAsync("Prog.cs", new BreakpointRequest(12));

        var restarted = await mgr.RestartAsync("s1");

        Assert.Equal(launched.SessionId, restarted.SessionId);     // same id
        Assert.Equal(2, created.Count);                            // a fresh engine
        Assert.Equal(1, created[0].TerminateCalls);                // old one terminated
        // Breakpoints re-applied to the NEW engine.
        var resent = Assert.Single(created[1].SetBreakpointCalls);
        Assert.Equal(12, Assert.Single(resent.bps).Line);
        Assert.Single(await mgr.Resolve("s1").ListBreakpointsAsync());
    }

    [Fact]
    public async Task Restart_rejects_attached_session()
    {
        var (mgr, _) = NewManager();
        await mgr.AttachAsync(4242); // s1, no LaunchInfo
        await Assert.ThrowsAsync<InvalidOperationException>(() => mgr.RestartAsync("s1"));
    }

    [Fact]
    public async Task Stop_disposes_the_sessions_owned_resource()
    {
        var (mgr, _) = NewManager();
        var info = await mgr.AttachAsync(4242);
        var owned = new DisposeFlag();
        mgr.Resolve(info.SessionId).OwnedResource = owned;

        await mgr.StopAsync(info.SessionId);

        Assert.True(owned.Disposed); // e.g. the 'dotnet test' driver gets killed
    }

    sealed class DisposeFlag : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task WaitForAnyStop_returns_enriched_stop_from_the_session_that_broke()
    {
        var (mgr, created) = NewManager();
        await mgr.LaunchAsync(new LaunchRequest("a.dll")); // s1
        await mgr.LaunchAsync(new LaunchRequest("b.dll")); // s2

        // s1 breaks spontaneously.
        created[0].RaiseStopped(new("raw.cs", 1, "breakpoint", 9));

        var stop = await mgr.WaitForAnyStopAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(stop);
        Assert.Equal("s1", stop!.SessionId);
        Assert.Equal("Enriched.cs", stop.Stop.File); // enriched via stack frame
        Assert.Equal("s1", mgr.Resolve(null).Id);    // most-recently-stopped is active
    }

    [Fact]
    public async Task WaitForAnyStop_times_out_to_null()
    {
        var (mgr, _) = NewManager();
        await mgr.LaunchAsync(new LaunchRequest("a.dll"));
        var stop = await mgr.WaitForAnyStopAsync(TimeSpan.FromMilliseconds(150));
        Assert.Null(stop);
    }

    [Fact]
    public async Task DisposeAsync_is_safe()
    {
        var (mgr, _) = NewManager();
        await mgr.LaunchAsync(new LaunchRequest("a.dll"));
        await mgr.DisposeAsync(); // should not throw
        Assert.Empty(mgr.List());
    }
}
