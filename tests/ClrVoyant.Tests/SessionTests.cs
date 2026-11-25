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
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.GetVariablesAsync(1, 0, 10));
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

    // ---- source breakpoints by content ----

    static async Task<string> WithSource(params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"clrvoyant-{Guid.NewGuid():N}.cs");
        await File.WriteAllLinesAsync(path, lines);
        return path;
    }

    [Fact]
    public async Task A_breakpoint_by_content_is_set_on_the_line_the_text_is_found_on()
    {
        string path = await WithSource("a();", "b();", "c();");
        try
        {
            var fake = new FakeDebugEngine();
            var bp = await NewSession(fake).AddSourceBreakpointAsync(path, new BreakpointRequest(0, Condition: "i > 1"), "b();");

            Assert.Equal(2, bp.Line);
            Assert.Equal(2, Assert.Single(Assert.Single(fake.SetBreakpointCalls).bps).Line);
            Assert.Equal("i > 1", fake.SetBreakpointCalls[0].bps[0].Condition); // the other fields travel along
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task The_line_is_only_a_hint_when_content_is_given()
    {
        string path = await WithSource("x++;", "y++;", "x++;");
        try
        {
            var bp = await NewSession(new FakeDebugEngine()).AddSourceBreakpointAsync(path, new BreakpointRequest(3), "x++;");

            Assert.Equal(3, bp.Line); // two matches, the hint picked the nearer
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Without_content_the_line_is_used_as_given_and_must_be_valid()
    {
        var fake = new FakeDebugEngine();
        var s = NewSession(fake);

        Assert.Equal(17, (await s.AddSourceBreakpointAsync("never-read.cs", new BreakpointRequest(17), content: null)).Line);
        foreach (int invalid in new[] { 0, -1 })
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => s.AddSourceBreakpointAsync("never-read.cs", new BreakpointRequest(invalid), content: null));
            Assert.Equal("Provide either a 1-based 'line' or the 'content' of the line to match.", ex.Message);
        }
        Assert.Single(fake.SetBreakpointCalls); // the invalid ones never reached the engine
    }

    // ---- the thread an operation applies to ----

    [Fact]
    public async Task The_thread_defaults_to_the_one_of_the_last_stop_and_an_explicit_one_wins()
    {
        var fake = new FakeDebugEngine { StopOnResume = new("raw.cs", 10, "breakpoint", 7) };
        var s = NewSession(fake);
        await s.ContinueAsync(null, TimeSpan.FromSeconds(2));

        await s.StepAsync(StepKind.Over, null, TimeSpan.FromSeconds(2));
        await s.GetStackTraceAsync(null, 0, 5);
        await s.GetStackTraceAsync(99, 0, 5);

        Assert.Equal([7], fake.StepThreadIds);
        // (the first stack trace request is the stop's own enrichment, of thread 7 too)
        Assert.Equal([7, 7, 7, 99], fake.StackTraceThreadIds);
    }

    [Fact]
    public async Task Without_a_stop_there_is_no_thread_to_default_to()
    {
        var fake = new FakeDebugEngine { State = SessionState.Running, StopOnResume = null };
        var s = NewSession(fake);

        var ex = Assert.Throws<InvalidOperationException>(() => s.ResolveThread(null));
        Assert.Equal("Session 's1' has no current thread (it has not stopped yet); pass threadId.", ex.Message);
        Assert.Equal(5, s.ResolveThread(5));
        // Stepping resolves the thread before it arms a wait: nothing was sent, nothing is left armed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.StepAsync(StepKind.Over, null, TimeSpan.FromSeconds(1)));
        Assert.Empty(fake.Steps);
        Assert.Equal(SessionState.Running, (await s.ContinueAsync(null, TimeSpan.FromMilliseconds(50))).State);
    }

    // ---- variables are paged at the debugger, not sliced here ----

    [Fact]
    public async Task Variables_are_requested_as_a_window_with_one_lookahead_item()
    {
        var fake = new FakeDebugEngine { State = SessionState.Stopped };
        for (int i = 0; i < 12; i++) fake.Variables.Add(new Variable($"v{i}", "0", "int", 0)); // 13 in all with the default one
        var s = NewSession(fake);

        var page = await s.GetVariablesAsync(5, offset: 4, max: 5);

        Assert.Equal([(4, 6)], fake.VariablesRequests); // start=offset, count=max+1: never "everything"
        Assert.Equal(["v3", "v4", "v5", "v6", "v7"], page.Items.Select(v => v.Name));
        Assert.True(page.Truncated);
        Assert.Equal(9, page.NextOffset);
        Assert.Null(page.Total);

        var last = await s.GetVariablesAsync(5, offset: 9, max: 5);
        Assert.Equal(["v8", "v9", "v10", "v11"], last.Items.Select(v => v.Name));
        Assert.False(last.Truncated);
    }

    [Fact]
    public async Task A_bad_variables_window_is_refused_before_the_debugger_is_asked()
    {
        var fake = new FakeDebugEngine { State = SessionState.Stopped };
        var s = NewSession(fake);

        await Assert.ThrowsAsync<ArgumentException>(() => s.GetVariablesAsync(1, offset: 0, max: 0));
        await Assert.ThrowsAsync<ArgumentException>(() => s.GetVariablesAsync(1, offset: -1, max: 10));
        Assert.Empty(fake.VariablesRequests);
    }
}
