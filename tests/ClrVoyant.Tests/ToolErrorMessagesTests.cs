using ClrVoyant.Tests.Fakes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ClrVoyant.Tests;

/// <summary>
/// The recovery messages the code base relies on to steer an LLM, as they arrive through the real tool
/// registration. <see cref="ToolExceptionTests"/> pins the mapping rule; these pin that the messages of
/// real tools survive it — each is something the caller can act on.
/// </summary>
public class ToolErrorMessagesTests
{
    static async Task<string> FailureOf(McpRun run, string tool, params (string name, object? value)[] args)
    {
        var result = await run.Client.CallToolAsync(tool, args.ToDictionary(a => a.name, a => a.value), cancellationToken: run.Ct);
        Assert.True(result.IsError, $"{tool} was expected to fail");
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    static Task Launch(McpRun run) =>
        run.Client.CallToolAsync("debug_launch", new Dictionary<string, object?> { ["program"] = "app.dll" }, cancellationToken: run.Ct).AsTask();

    [Fact]
    public async Task Introspection_while_the_target_runs_says_the_state_and_that_it_must_be_stopped()
    {
        await McpInMemory.RunRealAsync(new FakeEngineFactory(), async run =>
        {
            await Launch(run);
            Assert.Equal(
                "Session 's1' is 'Running', not stopped. This operation requires a stopped target.",
                await FailureOf(run, "get_threads"));
        });
    }

    [Fact]
    public async Task The_task_tools_refuse_a_running_target_before_touching_its_heap()
    {
        await McpInMemory.RunRealAsync(new FakeEngineFactory(), async run =>
        {
            await Launch(run);
            string expected = "Session 's1' is 'Running', not stopped. This operation requires a stopped target.";
            Assert.Equal(expected, await FailureOf(run, "list_tasks"));
            Assert.Equal(expected, await FailureOf(run, "get_async_graph"));
            Assert.Equal(expected, await FailureOf(run, "get_task", ("address", "0x1")));
            Assert.Equal(expected, await FailureOf(run, "get_async_callstack", ("address", "0x1")));
        });
    }

    [Fact]
    public async Task An_ambiguous_breakpoint_content_lists_the_candidate_lines_and_asks_for_a_hint()
    {
        string source = Path.Combine(Path.GetTempPath(), $"clrvoyant-{Guid.NewGuid():N}.cs");
        File.WriteAllLines(source, ["int a = 0;", "x++;", "x++;"]);
        try
        {
            await McpInMemory.RunRealAsync(new FakeEngineFactory(), async run =>
            {
                await Launch(run);
                Assert.Equal(
                    "Content \"x++;\" matches 2 lines (2, 3); pass a 'line' hint to pick the intended one.",
                    await FailureOf(run, "set_breakpoint", ("file", source), ("content", "x++;")));

                // With the hint the same call succeeds: the message told the caller the truth.
                var ok = await run.Client.CallToolAsync("set_breakpoint",
                    new Dictionary<string, object?> { ["file"] = source, ["content"] = "x++;", ["line"] = 3 }, cancellationToken: run.Ct);
                Assert.True(ok.IsError != true);
            });
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task A_missing_source_file_names_the_file()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"clrvoyant-missing-{Guid.NewGuid():N}.cs");
        await McpInMemory.RunRealAsync(new FakeEngineFactory(), async run =>
        {
            await Launch(run);
            Assert.Contains(missing, await FailureOf(run, "set_breakpoint", ("file", missing), ("content", "x")));
        });
    }

    [Fact]
    public async Task An_unknown_session_id_is_named()
    {
        await McpInMemory.RunRealAsync(new FakeEngineFactory(), async run =>
        {
            await Launch(run);
            Assert.Equal("Unknown sessionId 'nope'.", await FailureOf(run, "debug_status", ("sessionId", "nope")));
        });
    }

    [Fact]
    public async Task Without_an_active_session_the_message_lists_the_sessions_to_choose_from()
    {
        await McpInMemory.RunRealAsync(new FakeEngineFactory(), async run =>
        {
            await Launch(run); // s1
            await Launch(run); // s2
            await Launch(run); // s3, the active one
            await run.Client.CallToolAsync("debug_stop", new Dictionary<string, object?> { ["sessionId"] = "s3" }, cancellationToken: run.Ct);

            // Two sessions remain and none is "the" active one: the caller must say which.
            Assert.Equal(
                "No active debug session; 2 sessions exist (s1, s2): pass sessionId.",
                await FailureOf(run, "debug_status"));
        });
    }

    [Fact]
    public async Task A_lost_debugger_connection_is_reported_with_its_cause_and_the_way_out()
    {
        var engines = new FakeEngineFactory();
        await McpInMemory.RunRealAsync(engines, async run =>
        {
            await Launch(run);
            engines.Engines[0].RaiseFaulted("adapter exited with code 9");

            string message = await FailureOf(run, "continue");
            Assert.Contains("lost its debugger connection (adapter exited with code 9)", message);
            Assert.Contains("debug_stop", message);
        });
    }
}
