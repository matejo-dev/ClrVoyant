using ClrVoyant.Core;
using ClrVoyant.Server.Tools;
using ClrVoyant.Tests.Fakes;

namespace ClrVoyant.Tests;

public class BoundedTextTailTests
{
    [Fact]
    public void Keeps_only_the_most_recent_characters_and_reports_the_drop()
    {
        var tail = new BoundedTextTail(10);
        tail.Append("0123456789");
        Assert.False(tail.Dropped);

        tail.Append("ABC");

        Assert.True(tail.Dropped);
        Assert.Equal(10, tail.Length);
        Assert.Equal("3456789ABC", tail.GetTail(100));
        Assert.Equal("9ABC", tail.GetTail(4));
    }

    [Fact]
    public void A_single_append_larger_than_the_capacity_keeps_its_end()
    {
        var tail = new BoundedTextTail(4);
        tail.Append("abcdefgh");
        Assert.Equal("efgh", tail.GetTail(100));
    }

    [Fact]
    public void Clear_forgets_text_and_the_dropped_flag()
    {
        var tail = new BoundedTextTail(3);
        tail.Append("abcdef");
        tail.Clear();
        Assert.Equal("", tail.GetTail(10));
        Assert.False(tail.Dropped);
    }
}

/// <summary>Debuggee output, exit code and the stop policy (kill vs detach) through Session/SessionManager.</summary>
public class SessionOutputAndStopTests
{
    static (SessionManager mgr, List<FakeDebugEngine> engines) NewManager()
    {
        var engines = new List<FakeDebugEngine>();
        return (new SessionManager(() => { var f = new FakeDebugEngine(); engines.Add(f); return f; }), engines);
    }

    // ---- output ----

    [Fact]
    public void Output_keeps_stdout_and_stderr_but_not_the_debuggers_own_console_chatter()
    {
        var fake = new FakeDebugEngine();
        var s = new Session("s1", fake);

        fake.RaiseOutput("stdout", "out\n");
        fake.RaiseOutput("console", "Loaded 'System.Private.CoreLib.dll'\n");
        fake.RaiseOutput("stderr", "err\n");

        var o = s.GetOutput(1000);
        Assert.Equal("out\nerr\n", o.Text);
        Assert.False(o.Truncated);
    }

    [Fact]
    public void Output_is_bounded_dropping_the_oldest_and_says_it_is_truncated()
    {
        var fake = new FakeDebugEngine();
        var s = new Session("s1", fake);

        fake.RaiseOutput("stdout", "OLDEST-MARKER");
        fake.RaiseOutput("stdout", new string('x', 70 * 1024)); // more than the 64 KiB cap
        fake.RaiseOutput("stdout", "NEWEST-MARKER");

        var o = s.GetOutput(int.MaxValue);
        Assert.DoesNotContain("OLDEST-MARKER", o.Text);
        Assert.EndsWith("NEWEST-MARKER", o.Text);
        Assert.True(o.Text.Length <= 64 * 1024);
        Assert.True(o.Truncated);
    }

    [Fact]
    public void A_smaller_maxChars_returns_the_end_and_flags_truncation()
    {
        var fake = new FakeDebugEngine();
        var s = new Session("s1", fake);
        fake.RaiseOutput("stdout", "0123456789");

        var o = s.GetOutput(4);

        Assert.Equal("6789", o.Text);
        Assert.True(o.Truncated);
    }

    [Fact]
    public void GetOutput_rejects_a_non_positive_limit()
    {
        var s = new Session("s1", new FakeDebugEngine());
        Assert.Throws<ArgumentOutOfRangeException>(() => s.GetOutput(0));
    }

    [Fact]
    public async Task The_get_output_tool_reads_the_active_sessions_output_state_and_exit_code()
    {
        var (mgr, engines) = NewManager();
        await mgr.LaunchAsync(new LaunchRequest("a.dll"));
        engines[0].RaiseOutput("stdout", "hello");
        engines[0].RaiseExited(5);

        var o = DebugTools.GetOutput(mgr);

        Assert.Equal("hello", o.Text);
        Assert.Equal(SessionState.Exited, o.State);
        Assert.Equal(5, o.ExitCode);
    }

    // ---- exit code ----

    [Fact]
    public async Task Resume_that_ends_in_exit_returns_the_exit_code_and_status_keeps_it()
    {
        var fake = new FakeDebugEngine();
        var s = new Session("s1", fake);
        // Resume() in the fake raises Exited(0) when ExitOnResume; use a real code instead.
        fake.StopOnResume = null;
        var resume = s.ContinueAsync(null, TimeSpan.FromSeconds(10));
        fake.RaiseExited(42);

        var outcome = await resume;

        Assert.Equal(SessionState.Exited, outcome.State);
        Assert.Equal(42, outcome.ExitCode);
        Assert.Equal(42, s.ToInfo().ExitCode);
    }

    [Fact]
    public async Task Exit_without_a_known_code_reports_null()
    {
        var fake = new FakeDebugEngine { StopOnResume = null };
        var s = new Session("s1", fake);
        var resume = s.ContinueAsync(null, TimeSpan.FromSeconds(10));
        fake.RaiseExited(null);

        Assert.Null((await resume).ExitCode);
    }

    // ---- restart keeps env, clears the previous run ----

    [Fact]
    public async Task Restart_relaunches_with_the_same_environment_and_clears_the_previous_run()
    {
        var (mgr, engines) = NewManager();
        var env = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Staging" };
        var info = await mgr.LaunchAsync(new LaunchRequest("a.dll", Env: env));
        engines[0].RaiseOutput("stdout", "first run");
        engines[0].RaiseExited(1);

        await mgr.RestartAsync(info.SessionId);

        Assert.Equal(2, engines.Count);
        Assert.Same(env, engines[1].LastLaunch!.Env);
        var s = mgr.Resolve(info.SessionId);
        Assert.Equal("", s.GetOutput(100).Text);
        Assert.Null(s.ExitCode);
        Assert.True(engines[0].LastTerminateDebuggee); // restart kills the old debuggee
    }

    // ---- environment validation ----

    [Theory]
    [InlineData("")]
    [InlineData("A=B")]
    public async Task Launch_rejects_an_invalid_environment_variable_name_before_creating_a_session(string name)
    {
        var (mgr, engines) = NewManager();

        await Assert.ThrowsAsync<ArgumentException>(
            () => mgr.LaunchAsync(new LaunchRequest("a.dll", Env: new Dictionary<string, string> { [name] = "v" })));

        Assert.Empty(mgr.List());
        Assert.Empty(engines);
    }

    // ---- stop policy ----

    [Fact]
    public async Task Stopping_a_launched_session_terminates_the_debuggee_by_default()
    {
        var (mgr, engines) = NewManager();
        var info = await mgr.LaunchAsync(new LaunchRequest("a.dll"));

        await mgr.StopAsync(info.SessionId);

        Assert.True(engines[0].LastTerminateDebuggee);
    }

    [Fact]
    public async Task Stopping_an_attached_session_detaches_by_default()
    {
        var (mgr, engines) = NewManager();
        var info = await mgr.AttachAsync(4242);

        await mgr.StopAsync(info.SessionId);

        Assert.False(engines[0].LastTerminateDebuggee);
    }

    [Fact]
    public async Task Stopping_an_attached_session_that_owns_its_host_process_terminates_it()
    {
        // The debug_test case: attached to a test host that the server itself started.
        var (mgr, engines) = NewManager();
        var info = await mgr.AttachAsync(4242);
        mgr.Resolve(info.SessionId).OwnedResource = new NoopResource();

        await mgr.StopAsync(info.SessionId);

        Assert.True(engines[0].LastTerminateDebuggee);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_terminate_argument_overrides_the_default_for_attached_and_launched(bool terminate)
    {
        var (mgr, engines) = NewManager();
        var attached = await mgr.AttachAsync(1);
        var launched = await mgr.LaunchAsync(new LaunchRequest("a.dll"));

        await DebugTools.DebugStop(mgr, attached.SessionId, terminate);
        await DebugTools.DebugStop(mgr, launched.SessionId, terminate);

        Assert.Equal(terminate, engines[0].LastTerminateDebuggee);
        Assert.Equal(terminate, engines[1].LastTerminateDebuggee);
    }

    sealed class NoopResource : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
