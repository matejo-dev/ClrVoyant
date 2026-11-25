using System.Reflection;
using System.Text.Json;
using ClrVoyant.Core;
using ClrVoyant.Server;
using ClrVoyant.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClrVoyant.Tests;

/// <summary>
/// The tool surface as a client sees it: the real registration (the production DI wiring and MCP
/// pipeline) over <see cref="FakeDebugEngine"/> sessions, driven by a real MCP client. This is the
/// binding/serialisation layer — tool names, parameter binding, result JSON, annotations, errors —
/// that the unit tests of Session and the integration tests (which call the tool methods directly)
/// never go through.
/// </summary>
public class McpToolSurfaceTests
{
    // ---- the surface itself ----

    internal static readonly string[] ToolNames =
    [
        "get_debug_instructions", "debug_launch", "debug_attach", "list_processes", "debug_test",
        "set_auto_attach", "debug_status", "debug_stop", "get_output", "restart_debugging", "list_sessions",
        "set_breakpoint", "set_function_breakpoint", "list_function_breakpoints", "remove_breakpoint",
        "list_breakpoints", "clear_all_breakpoints", "set_exception_breakpoints", "list_methods",
        "continue", "step_over", "step_into", "step_out", "pause", "wait_for_any_stop",
        "get_threads", "get_callstack", "get_scopes", "get_variables", "evaluate", "get_exception_info",
        "list_tasks", "get_task", "get_async_graph", "get_async_callstack",
    ];

    [Fact]
    public async Task The_registration_exposes_exactly_these_tools()
    {
        IList<McpClientTool>? tools = null;
        await McpInMemory.RunRealAsync(new FakeEngineFactory(), async run =>
            tools = await run.Client.ListToolsAsync(cancellationToken: run.Ct));

        Assert.Equal(ToolNames.Order(StringComparer.Ordinal), tools!.Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    // Behaviour hints, decided tool by tool. Read-only tools touch nothing. The others declare whether
    // they destroy something (kill a process, run arbitrary code) and whether repeating the call has
    // no further effect. Nothing here reaches outside the machine (openWorld false everywhere).
    // A tool added without a row here fails the test below: deciding its hints is part of adding it.
    static (bool? readOnly, bool? destructive, bool? idempotent, bool? openWorld) RO() => (true, null, null, false);
    static (bool? readOnly, bool? destructive, bool? idempotent, bool? openWorld) W(bool destructive, bool idempotent)
        => (false, destructive, idempotent, false);

    static readonly Dictionary<string, (bool? readOnly, bool? destructive, bool? idempotent, bool? openWorld)> Hints = new()
    {
        // lifecycle
        ["get_debug_instructions"] = RO(),
        ["debug_launch"] = W(destructive: false, idempotent: false),       // starts a process; each call is a new session
        ["debug_attach"] = W(destructive: false, idempotent: false),       // a new session each time
        ["list_processes"] = RO(),
        ["debug_test"] = W(destructive: false, idempotent: false),         // runs 'dotnet test' (build outputs) and a new session
        ["set_auto_attach"] = W(destructive: false, idempotent: true),     // sets a flag
        ["debug_status"] = RO(),
        ["debug_stop"] = W(destructive: true, idempotent: false),          // may kill the process; a second call finds no session
        ["get_output"] = RO(),
        ["restart_debugging"] = W(destructive: true, idempotent: false),   // kills the process and starts a new one
        ["list_sessions"] = RO(),
        // breakpoints
        ["set_breakpoint"] = W(destructive: false, idempotent: false),     // each call adds one (new id)
        ["set_function_breakpoint"] = W(destructive: false, idempotent: false),
        ["list_function_breakpoints"] = RO(),
        ["remove_breakpoint"] = W(destructive: false, idempotent: true),   // debugger configuration, trivially recreated
        ["list_breakpoints"] = RO(),
        ["clear_all_breakpoints"] = W(destructive: false, idempotent: true),
        ["set_exception_breakpoints"] = W(destructive: false, idempotent: true), // replaces the filter set
        ["list_methods"] = RO(),
        // execution: the program the user chose to debug runs; the tool itself destroys nothing
        ["continue"] = W(destructive: false, idempotent: false),
        ["step_over"] = W(destructive: false, idempotent: false),
        ["step_into"] = W(destructive: false, idempotent: false),
        ["step_out"] = W(destructive: false, idempotent: false),
        ["pause"] = W(destructive: false, idempotent: true),
        ["wait_for_any_stop"] = W(destructive: false, idempotent: false),  // a reported stop is consumed
        // inspection
        ["get_threads"] = RO(),
        ["get_callstack"] = RO(),
        ["get_scopes"] = RO(),
        ["get_variables"] = RO(),
        ["evaluate"] = W(destructive: true, idempotent: false),            // executes arbitrary code in the debuggee
        ["get_exception_info"] = RO(),
        // tasks (read from a heap snapshot)
        ["list_tasks"] = RO(),
        ["get_task"] = RO(),
        ["get_async_graph"] = RO(),
        ["get_async_callstack"] = RO(),
    };

    [Fact]
    public async Task Every_tool_declares_its_behaviour_hints_and_a_title()
    {
        IList<McpClientTool>? tools = null;
        await McpInMemory.RunRealAsync(new FakeEngineFactory(), async run =>
            tools = await run.Client.ListToolsAsync(cancellationToken: run.Ct));

        var actual = tools!.ToDictionary(
            t => t.Name,
            t => (t.ProtocolTool.Annotations?.ReadOnlyHint, t.ProtocolTool.Annotations?.DestructiveHint,
                  t.ProtocolTool.Annotations?.IdempotentHint, t.ProtocolTool.Annotations?.OpenWorldHint));
        Assert.Equal(Hints.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, expected) in Hints)
            Assert.True(expected == actual[name], $"{name}: expected {expected}, advertised {actual[name]}");

        // The title is what a client shows a human; it must say something and differ from the id.
        Assert.All(tools!, t => Assert.False(string.IsNullOrWhiteSpace(t.Title) || t.Title == t.Name, $"{t.Name} has no title"));
    }

    [Fact]
    public async Task The_cancellation_token_is_not_part_of_any_tools_input_schema()
    {
        IList<McpClientTool>? tools = null;
        await McpInMemory.RunRealAsync(new FakeEngineFactory(), async run =>
            tools = await run.Client.ListToolsAsync(cancellationToken: run.Ct));

        Assert.All(tools!, t => Assert.DoesNotContain("cancellationToken", t.JsonSchema.GetRawText(), StringComparison.OrdinalIgnoreCase));
    }

    // A tool that awaits must be cancellable by the client. Whether each one passes its token on is
    // covered for `continue` (ToolCancellationTests); this keeps a new async tool from forgetting it.
    [Fact]
    public void Every_tool_that_returns_a_task_accepts_a_cancellation_token()
    {
        var withoutToken = typeof(McpRegistration).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Where(m => typeof(Task).IsAssignableFrom(m.ReturnType))
            .Where(m => m.GetParameters().All(p => p.ParameterType != typeof(CancellationToken)))
            .Select(m => m.Name);

        Assert.Empty(withoutToken);
    }

    // ---- the server instructions ----

    [Fact]
    public async Task The_handshake_carries_the_playbook_as_server_instructions_and_the_tool_serves_the_same_text()
    {
        string? instructions = null;
        string? fromTool = null;
        await McpInMemory.RunRealAsync(new FakeEngineFactory(), async run =>
        {
            instructions = run.Client.ServerInstructions;
            fromTool = Text(await run.Client.CallToolAsync("get_debug_instructions", cancellationToken: run.Ct));
        });

        Assert.StartsWith("ClrVoyant", instructions);
        Assert.Equal(instructions, fromTool);
    }

    // ---- a debug loop through the wire ----

    [Fact]
    public async Task Launch_set_breakpoint_by_content_continue_and_read_the_callstack()
    {
        string source = TempFile("class C {", "    int y = 1;", "    int x = y + 1;", "}");
        var engines = new FakeEngineFactory { Customize = e => e.StopOnResume = new("F.cs", 3, "breakpoint", 7) };
        try
        {
            await McpInMemory.RunRealAsync(engines, async run =>
            {
                // Arrays, the env map and a bool bind from JSON.
                var launch = Json(await Call(run, "debug_launch", ("program", "app.dll"), ("args", new[] { "--a", "b" }),
                    ("env", new Dictionary<string, string> { ["K"] = "V" }), ("stopAtEntry", false)));
                Assert.Equal("s1", launch.GetProperty("sessionId").GetString());
                var engine = Assert.Single(engines.Engines);
                Assert.Equal(["--a", "b"], engine.LastLaunch!.Args!);
                Assert.Equal("V", engine.LastLaunch.Env!["K"]);

                // 'content' is resolved to its line; the unset optionals stay null.
                var bp = Json(await Call(run, "set_breakpoint", ("file", source), ("content", "int x = y + 1;")));
                Assert.Equal(3, bp.GetProperty("line").GetInt32());
                var sent = Assert.Single(engine.SetBreakpointCalls).bps.Single();
                Assert.Equal(3, sent.Line);
                Assert.Null(sent.Condition);

                // A nullable int left out binds as null, and the thread comes from the stop.
                var stop = Json(await Call(run, "continue", ("timeoutMs", 5000)));
                Assert.Equal("breakpoint", stop.GetProperty("stop").GetProperty("reason").GetString());
                Assert.Equal(42, stop.GetProperty("stop").GetProperty("line").GetInt32()); // enriched from the top frame

                var frames = Json(await Call(run, "get_callstack", ("levels", 5)));
                Assert.Equal("Enriched.cs", frames[0].GetProperty("file").GetString());

                var scopes = Json(await Call(run, "get_scopes", ("frameId", 0)));
                int reference = scopes[0].GetProperty("variablesReference").GetInt32();
                var vars = Json(await Call(run, "get_variables", ("variablesReference", reference)));
                Assert.Equal("x", vars.GetProperty("items")[0].GetProperty("name").GetString());
            });
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task Each_step_tool_sends_its_own_kind_of_step_on_the_thread_of_the_stop()
    {
        var engines = new FakeEngineFactory { Customize = e => e.StopOnResume = new("F.cs", 2, "step", 7) };
        await McpInMemory.RunRealAsync(engines, async run =>
        {
            await Call(run, "debug_launch", ("program", "app.dll"));
            var engine = engines.Engines[0];
            engine.RaiseStopped(new StopLocation("F.cs", 1, "breakpoint", 7));

            foreach (string tool in new[] { "step_into", "step_over", "step_out" })
                Assert.Equal("Stopped", Json(await Call(run, tool, ("timeoutMs", 5000))).GetProperty("state").GetString());

            Assert.Equal([StepKind.Into, StepKind.Over, StepKind.Out], engine.Steps);
            Assert.Equal([7, 7, 7], engine.StepThreadIds);
        });
    }

    [Fact]
    public async Task A_session_state_is_serialised_as_a_word_not_a_number()
    {
        await McpInMemory.RunRealAsync(new FakeEngineFactory(), async run =>
        {
            var launch = Json(await Call(run, "debug_launch", ("program", "app.dll")));
            Assert.Equal(JsonValueKind.String, launch.GetProperty("state").ValueKind);
            Assert.Equal("Running", launch.GetProperty("state").GetString());
        });
    }

    [Fact]
    public async Task Exception_filters_the_pid_and_auto_attach_bind_and_take_effect()
    {
        var engines = new FakeEngineFactory();
        await McpInMemory.RunRealAsync(engines, async run =>
        {
            await Call(run, "debug_launch", ("program", "app.dll"));
            Assert.Equal("ok", Text(await Call(run, "set_exception_breakpoints", ("filters", new[] { "all", "user-unhandled" }))));
            Assert.Equal(["all", "user-unhandled"], Assert.Single(engines.Engines).ExceptionFilters);

            Assert.Equal("auto-attach enabled", Text(await Call(run, "set_auto_attach", ("enabled", true))));
            Assert.True(run.Services.GetRequiredService<AutoAttachOptions>().Enabled);
            Assert.Equal("auto-attach disabled", Text(await Call(run, "set_auto_attach", ("enabled", false))));
            Assert.False(run.Services.GetRequiredService<AutoAttachOptions>().Enabled);
        });
    }

    [Fact]
    public async Task Get_output_returns_the_captured_tail_with_the_state()
    {
        var engines = new FakeEngineFactory();
        await McpInMemory.RunRealAsync(engines, async run =>
        {
            await Call(run, "debug_launch", ("program", "app.dll"));
            engines.Engines[0].RaiseOutput("stdout", "hello from the debuggee\n");

            var all = Json(await Call(run, "get_output"));
            Assert.Equal("hello from the debuggee\n", all.GetProperty("text").GetString());
            Assert.False(all.GetProperty("truncated").GetBoolean());

            // maxChars keeps the most recent characters and says it cut.
            var tail = Json(await Call(run, "get_output", ("maxChars", 4)));
            Assert.Equal("ee\n".PadLeft(4, 'e').Length, tail.GetProperty("text").GetString()!.Length);
            Assert.EndsWith("ee\n", tail.GetProperty("text").GetString());
            Assert.True(tail.GetProperty("truncated").GetBoolean());
        });
    }

    // ---- bounded results: one page shape, paged at the source ----

    [Fact]
    public async Task Get_variables_returns_a_page_that_says_what_is_left()
    {
        var engines = new FakeEngineFactory();
        await McpInMemory.RunRealAsync(engines, async run =>
        {
            await Call(run, "debug_launch", ("program", "app.dll"));
            var engine = engines.Engines[0];
            for (int i = 1; i < 5; i++) engine.Variables.Add(new Variable($"v{i}", "0", "int", 0)); // 5 children
            engine.RaiseStopped(new StopLocation("F.cs", 1, "breakpoint", 1));

            var first = Json(await Call(run, "get_variables", ("variablesReference", 100), ("max", 2)));
            Assert.Equal(["x", "v1"], first.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("name").GetString()));
            Assert.True(first.GetProperty("truncated").GetBoolean());
            Assert.Equal(2, first.GetProperty("nextOffset").GetInt32());
            Assert.False(first.TryGetProperty("total", out _)); // unknown (the debugger does not say), so omitted: nulls are not serialised

            var rest = Json(await Call(run, "get_variables", ("variablesReference", 100), ("max", 10), ("offset", 2)));
            Assert.Equal(["v2", "v3", "v4"], rest.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("name").GetString()));
            Assert.False(rest.GetProperty("truncated").GetBoolean());
            Assert.False(rest.TryGetProperty("nextOffset", out _)); // nothing follows
        });
    }

    [Fact]
    public async Task List_methods_returns_the_same_page_shape_with_an_exact_total()
    {
        string assembly = typeof(McpToolSurfaceTests).Assembly.Location;
        await McpInMemory.RunRealAsync(new FakeEngineFactory(), async run =>
        {
            var page = Json(await Call(run, "list_methods", ("assemblyPath", assembly), ("max", 2)));

            Assert.Equal(2, page.GetProperty("items").GetArrayLength());
            Assert.True(page.GetProperty("total").GetInt32() > 2);
            Assert.True(page.GetProperty("truncated").GetBoolean());
            Assert.Equal(2, page.GetProperty("nextOffset").GetInt32());
            Assert.Equal("fullName", page.GetProperty("items")[0].EnumerateObject().First().Name);
        });
    }

    [Fact]
    public async Task A_window_outside_the_allowed_range_is_refused_with_the_limit()
    {
        var engines = new FakeEngineFactory();
        await McpInMemory.RunRealAsync(engines, async run =>
        {
            await Call(run, "debug_launch", ("program", "app.dll"));
            engines.Engines[0].RaiseStopped(new StopLocation("F.cs", 1, "breakpoint", 1));

            var tooBig = await Call(run, "get_variables", ("variablesReference", 100), ("max", 5000));
            Assert.True(tooBig.IsError);
            Assert.Equal("'max' must be between 1 and 1000 (got 5000).", Text(tooBig));

            var negative = await Call(run, "list_tasks", ("offset", -1));
            Assert.True(negative.IsError);
            Assert.Equal("'offset' must be 0 or greater (got -1).", Text(negative));
        });
    }

    // ---- argument validation reaches the caller as a readable error ----

    [Fact]
    public async Task Set_breakpoint_without_a_line_or_content_says_what_to_provide()
    {
        await McpInMemory.RunRealAsync(new FakeEngineFactory(), async run =>
        {
            await Call(run, "debug_launch", ("program", "app.dll"));

            var neither = await Call(run, "set_breakpoint", ("file", "x.cs"));
            Assert.True(neither.IsError);
            Assert.Equal("Provide either a 1-based 'line' or the 'content' of the line to match.", Text(neither));

            var negative = await Call(run, "set_breakpoint", ("file", "x.cs"), ("line", -3));
            Assert.True(negative.IsError);
            Assert.Contains("'line'", Text(negative));
        });
    }

    [Fact]
    public async Task Stepping_and_the_callstack_need_a_thread_from_a_stop_or_from_the_caller()
    {
        var engines = new FakeEngineFactory();
        await McpInMemory.RunRealAsync(engines, async run =>
        {
            await Call(run, "debug_launch", ("program", "app.dll"));
            // Running, never stopped: stepping has no thread to step.
            var step = await Call(run, "step_over");
            Assert.True(step.IsError);
            Assert.Contains("no current thread", Text(step));
            Assert.Contains("pass threadId", Text(step));

            // Stopped, but the stop did not name a thread: the callstack cannot default one either.
            engines.Engines[0].RaiseStopped(new StopLocation(null, null, "pause", null));
            var stack = await Call(run, "get_callstack");
            Assert.True(stack.IsError);
            Assert.Contains("no current thread", Text(stack));
            var exception = await Call(run, "get_exception_info");
            Assert.True(exception.IsError);
            Assert.Contains("no current thread", Text(exception));

            // Given a thread explicitly, it works.
            Assert.True((await Call(run, "get_callstack", ("threadId", 1))).IsError != true);
        });
    }

    [Fact]
    public async Task A_garbage_task_address_is_rejected_with_the_accepted_formats()
    {
        var engines = new FakeEngineFactory();
        await McpInMemory.RunRealAsync(engines, async run =>
        {
            await Call(run, "debug_launch", ("program", "app.dll"));
            engines.Engines[0].RaiseStopped(new StopLocation("F.cs", 1, "breakpoint", 1));

            foreach (string tool in new[] { "get_task", "get_async_callstack" })
            {
                var result = await Call(run, tool, ("address", "not-an-address"));
                Assert.True(result.IsError);
                Assert.Contains("'not-an-address' is not a heap address", Text(result));
            }
        });
    }

    [Fact]
    public async Task A_tool_called_with_a_wrongly_typed_argument_is_refused()
    {
        await McpInMemory.RunRealAsync(new FakeEngineFactory(), async run =>
        {
            // The SDK refuses it before the tool runs; whichever way it reports that, the call must not succeed.
            try
            {
                var result = await Call(run, "debug_attach", ("pid", "not-a-number"));
                Assert.True(result.IsError);
            }
            catch (McpException) { /* a protocol-level InvalidParams error is just as good */ }
        });
    }

    // ---- helpers ----

    static string TempFile(params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"clrvoyant-{Guid.NewGuid():N}.cs");
        File.WriteAllLines(path, lines);
        return path;
    }

    static async Task<CallToolResult> Call(McpRun run, string tool, params (string name, object? value)[] args)
        => await run.Client.CallToolAsync(tool, args.ToDictionary(a => a.name, a => a.value), cancellationToken: run.Ct);

    static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    static JsonElement Json(CallToolResult result)
    {
        Assert.True(result.IsError != true, "tool failed: " + string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text)));
        return JsonDocument.Parse(Text(result)).RootElement;
    }
}
