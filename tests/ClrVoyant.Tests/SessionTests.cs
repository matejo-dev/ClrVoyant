using ClrVoyant.Core;
using ClrVoyant.Tests.Fakes;

namespace ClrVoyant.Tests;

public class SessionTests
{
    static Session NewSession(FakeDebugEngine engine, string id = "s1") => new(id, engine);

    [Fact]
    public async Task Continue_returns_stopped_with_enriched_location()
    {
        var fake = new FakeDebugEngine { StopOnResume = new("raw.cs", 10, "breakpoint", 1) };
        var s = NewSession(fake);

        var outcome = await s.ContinueAsync(null, TimeSpan.FromSeconds(2));

        Assert.Equal(SessionState.Stopped, outcome.State);
        Assert.Equal(1, fake.ContinueCalls);
        // EnrichAsync rewrites file/line from the top stack frame.
        Assert.Equal("Enriched.cs", outcome.Stop!.File);
        Assert.Equal(42, outcome.Stop!.Line);
        Assert.Equal("breakpoint", outcome.Stop!.Reason);
        Assert.Equal(1, s.StopId);
    }

    [Fact]
    public async Task Continue_times_out_and_leaves_target_running()
    {
        var fake = new FakeDebugEngine { StopOnResume = null }; // never stops
        var s = NewSession(fake);

        var outcome = await s.ContinueAsync(null, TimeSpan.FromMilliseconds(150));

        Assert.Equal(SessionState.Running, outcome.State);
        Assert.Null(outcome.Stop);
    }

    [Fact]
    public async Task Continue_reports_exit_when_process_exits()
    {
        var fake = new FakeDebugEngine { ExitOnResume = true };
        var s = NewSession(fake);

        var outcome = await s.ContinueAsync(null, TimeSpan.FromSeconds(2));

        Assert.Equal(SessionState.Exited, outcome.State);
        Assert.Null(outcome.Stop);
    }

    [Theory]
    [InlineData(StepKind.Over)]
    [InlineData(StepKind.Into)]
    [InlineData(StepKind.Out)]
    public async Task Step_records_kind_and_returns_stop(StepKind kind)
    {
        var fake = new FakeDebugEngine { StopOnResume = new("s.cs", 5, "step", 1) };
        var s = NewSession(fake);

        var outcome = await s.StepAsync(kind, threadId: 1, TimeSpan.FromSeconds(2));

        Assert.Equal(SessionState.Stopped, outcome.State);
        Assert.Equal(kind, Assert.Single(fake.Steps));
    }

    [Fact]
    public async Task Breakpoint_store_assigns_stable_ids_and_resends_full_set()
    {
        var fake = new FakeDebugEngine();
        var s = NewSession(fake);

        var bp1 = await s.AddBreakpointAsync("a.cs", new BreakpointRequest(10));
        var bp2 = await s.AddBreakpointAsync("a.cs", new BreakpointRequest(20));

        Assert.Equal(1, bp1.Id);
        Assert.Equal(2, bp2.Id);
        Assert.True(bp1.Verified);
        Assert.Equal(10, bp1.Line);
        Assert.Equal(20, bp2.Line);

        // Each add re-sends the FULL desired set for the file (DAP replace semantics).
        Assert.Equal(2, fake.SetBreakpointCalls.Count);
        Assert.Single(fake.SetBreakpointCalls[0].bps);
        Assert.Equal(2, fake.SetBreakpointCalls[1].bps.Count);

        var listed = await s.ListBreakpointsAsync();
        Assert.Equal(2, listed.Count);

        // Removing the first re-sends only the remaining breakpoint.
        Assert.True(await s.RemoveBreakpointAsync(bp1.Id));
        Assert.Equal(3, fake.SetBreakpointCalls.Count);
        Assert.Equal(20, Assert.Single(fake.SetBreakpointCalls[2].bps).Line);

        var afterRemove = await s.ListBreakpointsAsync();
        Assert.Equal(2, Assert.Single(afterRemove).Id);
    }

    [Fact]
    public async Task RemoveBreakpoint_returns_false_for_unknown_id()
    {
        var s = NewSession(new FakeDebugEngine());
        Assert.False(await s.RemoveBreakpointAsync(999));
    }

    [Fact]
    public async Task ClearBreakpoints_removes_all_across_files_and_resends_empty()
    {
        var fake = new FakeDebugEngine();
        var s = NewSession(fake);
        await s.AddBreakpointAsync("a.cs", new BreakpointRequest(10));
        await s.AddBreakpointAsync("a.cs", new BreakpointRequest(20));
        await s.AddBreakpointAsync("b.cs", new BreakpointRequest(5));

        int cleared = await s.ClearBreakpointsAsync();

        Assert.Equal(3, cleared);
        Assert.Empty(await s.ListBreakpointsAsync());
        // The last call per file must be an empty set (engine clears the file).
        Assert.Empty(fake.SetBreakpointCalls[^1].bps);
        Assert.Equal(0, (await s.ClearBreakpointsAsync())); // idempotent: nothing left
    }

    [Fact]
    public async Task Introspection_throws_when_not_stopped()
    {
        var fake = new FakeDebugEngine { State = SessionState.Running };
        var s = NewSession(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.GetThreadsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.GetStackTraceAsync(1, 0, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.GetScopesAsync(0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.GetVariablesAsync(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.EvaluateAsync("x", null, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.GetExceptionInfoAsync(1));
    }

    [Fact]
    public void Spontaneous_stop_raises_event_and_records_last_stop()
    {
        var fake = new FakeDebugEngine();
        var s = NewSession(fake);
        SessionStop? captured = null;
        s.SpontaneousStop += (sess, stop, _) => captured = stop;

        fake.RaiseStopped(new("x.cs", 7, "breakpoint", 3));

        Assert.NotNull(captured);
        Assert.Equal("s1", captured!.SessionId);
        Assert.Equal(7, s.LastStop!.Line);
        Assert.Equal(1, s.StopId);
    }

    [Fact]
    public async Task Two_spellings_of_one_path_share_one_breakpoint_set_where_the_file_system_ignores_case()
    {
        var fake = new FakeDebugEngine();
        var s = NewSession(fake);
        string upper = Path.Combine(Path.GetTempPath(), "FOO.cs");
        string lower = Path.Combine(Path.GetTempPath(), "foo.cs");

        await s.AddBreakpointAsync(upper, new BreakpointRequest(1));
        await s.AddBreakpointAsync(lower, new BreakpointRequest(2));

        var (file, bps) = fake.SetBreakpointCalls[^1];
        if (OperatingSystem.IsWindows())
        {
            // One file: the engine (replace-all per source) must get both lines, always
            // under the first spelling, or the two sets would coexist there.
            Assert.Equal(new[] { 1, 2 }, bps.Select(b => b.Line));
            Assert.All(fake.SetBreakpointCalls, c => Assert.Equal(upper, c.file));
        }
        else
        {
            // Case-sensitive file system: two different files.
            Assert.Equal(lower, file);
            Assert.Equal(2, Assert.Single(bps).Line);
        }
    }

    [Fact]
    public async Task A_breakpoint_the_engine_has_not_bound_is_reported_unverified_with_its_stable_id()
    {
        var fake = new FakeDebugEngine();
        fake.UnverifiedLines.Add(20);
        var s = NewSession(fake);

        var bound = await s.AddBreakpointAsync("a.cs", new BreakpointRequest(10));
        var pending = await s.AddBreakpointAsync("a.cs", new BreakpointRequest(20));

        Assert.True(bound.Verified);
        Assert.False(pending.Verified);
        Assert.Equal(new[] { (1, true), (2, false) }, (await s.ListBreakpointsAsync()).Select(b => (b.Id, b.Verified)));
    }

    [Fact]
    public async Task Function_breakpoints_share_the_id_space_can_be_removed_and_survive_a_restart()
    {
        var fake = new FakeDebugEngine();
        var s = NewSession(fake);
        var fileBp = await s.AddBreakpointAsync("a.cs", new BreakpointRequest(10));
        var fnBp = await s.AddFunctionBreakpointAsync(new FunctionBreakpointRequest("Ns.Type.Method"));
        Assert.NotEqual(fileBp.Id, fnBp.Id); // one id space across both kinds

        var replacement = new FakeDebugEngine();
        await s.RestartAsync(new LaunchRequest("a.dll"), replacement);
        // The new engine got both sets again, under the same stable ids.
        Assert.Equal("Ns.Type.Method", Assert.Single(Assert.Single(replacement.SetFunctionBreakpointCalls)).FunctionName);
        Assert.Equal(fnBp.Id, Assert.Single(await s.ListFunctionBreakpointsAsync()).Id);

        Assert.True(await s.RemoveBreakpointAsync(fnBp.Id));
        Assert.Empty(await s.ListFunctionBreakpointsAsync());
        Assert.Empty(replacement.SetFunctionBreakpointCalls[^1]); // the engine's set was emptied too
        Assert.Single(await s.ListBreakpointsAsync()); // the file breakpoint is untouched
    }

    [Fact]
    public async Task A_stop_is_still_reported_when_its_location_cannot_be_read()
    {
        var fake = new FakeDebugEngine { StopOnResume = new("raw.cs", 10, "breakpoint", 1), StackTraceFailure = new IOException("adapter busy") };
        var s = NewSession(fake);

        var outcome = await s.ContinueAsync(null, TimeSpan.FromSeconds(30));

        Assert.Equal(SessionState.Stopped, outcome.State);
        Assert.Equal("raw.cs", outcome.Stop!.File); // unenriched, but not lost
    }
}
