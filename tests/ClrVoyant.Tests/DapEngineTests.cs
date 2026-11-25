using System.Text.Json;
using ClrVoyant.Core;
using ClrVoyant.Dap;
using ClrVoyant.Tests.Fakes;

namespace ClrVoyant.Tests;

/// <summary>
/// DapEngine over a scripted in-memory adapter: only the translation between the
/// engine's contract and the DAP conversation that does not need a real netcoredbg.
/// </summary>
public class DapEngineTests : IDisposable
{
    static readonly TimeSpan Bound = FakeAdapter.Bound;

    readonly FakeAdapter _adapter = new();
    readonly DapEngine _engine;

    public DapEngineTests() => _engine = new DapEngine(_adapter.Client);

    public void Dispose()
    {
        _engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _adapter.Dispose();
    }

    // The standard launch/attach conversation: initialize -> launch -> 'initialized' ->
    // configurationDone -> (launch response + process event).
    void ScriptHandshake(int pid = 4321)
    {
        JsonElement? startRequest = null;
        _adapter.Script((req, a) =>
        {
            switch (req.GetProperty("command").GetString())
            {
                case "initialize": a.Respond(req); break;
                case "launch":
                case "attach": startRequest = req; a.Event("initialized"); break;
                case "configurationDone":
                    a.Respond(req);
                    a.Respond(startRequest!.Value);
                    a.Event("process", new { systemProcessId = pid });
                    break;
                default: a.Respond(req); break;
            }
        });
    }

    async Task LaunchedAsync(LaunchRequest? request = null)
    {
        ScriptHandshake();
        await _engine.LaunchAsync(request ?? new LaunchRequest("app.dll")).WaitAsync(Bound);
    }

    // ---- launch / attach ----

    [Fact]
    public async Task Launch_sends_program_args_cwd_and_the_environment()
    {
        await LaunchedAsync(new LaunchRequest("app.dll", ["--flag"], Cwd: "work", StopAtEntry: true,
            Env: new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Staging", ["EMPTY"] = "" }));

        var args = _adapter.Find("launch")!.Value.GetProperty("arguments");
        Assert.Equal(Path.GetFullPath("app.dll"), args.GetProperty("program").GetString());
        Assert.Equal("--flag", args.GetProperty("args")[0].GetString());
        Assert.Equal("work", args.GetProperty("cwd").GetString());
        Assert.True(args.GetProperty("stopAtEntry").GetBoolean());
        var env = args.GetProperty("env");
        Assert.Equal("Staging", env.GetProperty("ASPNETCORE_ENVIRONMENT").GetString());
        Assert.Equal("", env.GetProperty("EMPTY").GetString());
        Assert.Equal(4321, _engine.Pid);
        Assert.Equal(SessionState.Running, _engine.State);
    }

    [Fact]
    public async Task Launch_without_an_environment_sends_no_env_property()
    {
        await LaunchedAsync(new LaunchRequest("app.dll"));

        Assert.False(_adapter.Find("launch")!.Value.GetProperty("arguments").TryGetProperty("env", out _));
    }

    [Fact]
    public async Task Launch_rejected_by_the_adapter_surfaces_its_message()
    {
        _adapter.Script((req, a) =>
        {
            switch (req.GetProperty("command").GetString())
            {
                case "initialize": a.Respond(req); break;
                case "launch": a.Reject(req, "cannot start 'app.dll'"); break;
            }
        });

        var ex = await Assert.ThrowsAsync<DapException>(() => _engine.LaunchAsync(new LaunchRequest("app.dll")).WaitAsync(Bound));
        Assert.Contains("cannot start 'app.dll'", ex.Message);
    }

    [Fact]
    public async Task Launch_fails_at_once_when_the_adapter_dies_mid_handshake()
    {
        _adapter.Script((req, a) =>
        {
            if (req.GetProperty("command").GetString() == "initialize") { a.Respond(req); return; }
            a.Eof(); // dies on the launch request
        });

        // Bounded by the harness well below the 15 s ready timeout: it must not wait that out.
        var ex = await Assert.ThrowsAsync<IOException>(() => _engine.LaunchAsync(new LaunchRequest("app.dll")).WaitAsync(Bound));
        Assert.Contains("closed", ex.Message);
        Assert.Equal(SessionState.Faulted, _engine.State);
    }

    [Fact]
    public async Task Launch_honours_a_cancelled_token_instead_of_reporting_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        _adapter.Script((req, a) =>
        {
            if (req.GetProperty("command").GetString() == "initialize") a.Respond(req);
            // never answers launch, never sends 'initialized'
        });

        var launch = _engine.LaunchAsync(new LaunchRequest("app.dll"), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launch.WaitAsync(Bound));
    }

    [Fact]
    public async Task Attach_sends_the_pid_and_reaches_running()
    {
        ScriptHandshake();
        await _engine.AttachAsync(777).WaitAsync(Bound);

        Assert.Equal(777, _adapter.Find("attach")!.Value.GetProperty("arguments").GetProperty("processId").GetInt32());
        Assert.Equal(SessionState.Running, _engine.State);
    }

    // ---- exit and output ----

    [Fact]
    public async Task Exited_event_reports_the_real_exit_code_once_even_though_terminated_follows()
    {
        await LaunchedAsync();
        var exits = new List<int?>();
        _engine.Exited += code => exits.Add(code);

        _adapter.Event("exited", new { exitCode = 3 });
        _adapter.Event("terminated");
        await SyncAsync();

        Assert.Equal([3], exits);
        Assert.Equal(SessionState.Exited, _engine.State);
    }

    [Fact]
    public async Task Terminated_without_exited_reports_the_end_once_with_no_exit_code()
    {
        await LaunchedAsync();
        var exits = new List<int?>();
        _engine.Exited += code => exits.Add(code);

        _adapter.Event("terminated");
        _adapter.Event("terminated");
        await SyncAsync();

        Assert.Equal([(int?)null], exits);
        Assert.Equal(SessionState.Exited, _engine.State);
    }

    [Fact]
    public async Task Exit_code_zero_is_reported_as_zero_not_as_unknown()
    {
        await LaunchedAsync();
        var exits = new List<int?>();
        _engine.Exited += code => exits.Add(code);

        _adapter.Event("exited", new { exitCode = 0 });
        await SyncAsync();

        Assert.Equal([0], exits);
    }

    [Fact]
    public async Task Debuggee_output_is_forwarded_with_its_category()
    {
        await LaunchedAsync();
        var got = new List<DebugOutput>();
        _engine.Output += o => got.Add(o);

        _adapter.Event("output", new { category = "stdout", output = "hello\n" });
        _adapter.Event("output", new { category = "stderr", output = "bad\n" });
        await SyncAsync();

        Assert.Equal([new DebugOutput("stdout", "hello\n"), new DebugOutput("stderr", "bad\n")], got);
    }

    // ---- terminate ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Terminate_passes_the_terminate_flag_to_disconnect(bool terminate)
    {
        await LaunchedAsync();

        await _engine.TerminateAsync(terminate).WaitAsync(Bound);

        var args = _adapter.Find("disconnect")!.Value.GetProperty("arguments");
        Assert.Equal(terminate, args.GetProperty("terminateDebuggee").GetBoolean());
        Assert.Equal(SessionState.Exited, _engine.State);
    }

    [Fact]
    public async Task Terminate_returns_promptly_when_the_adapter_is_already_dead()
    {
        await LaunchedAsync();
        _adapter.Eof();
        await ClosedAsync();

        await _engine.TerminateAsync(true).WaitAsync(Bound); // no 5 s disconnect wait on a closed client

        Assert.Equal(SessionState.Exited, _engine.State);
    }

    // ---- state ----

    [Fact]
    public async Task A_dead_adapter_makes_a_running_session_faulted()
    {
        await LaunchedAsync();
        Assert.Equal(SessionState.Running, _engine.State);

        _adapter.Eof();
        await ClosedAsync();

        Assert.Equal(SessionState.Faulted, _engine.State);
    }

    [Fact]
    public async Task A_dead_adapter_does_not_overwrite_a_normal_exit()
    {
        await LaunchedAsync();
        _adapter.Event("exited", new { exitCode = 0 });
        await SyncAsync();
        Assert.Equal(SessionState.Exited, _engine.State);

        _adapter.Eof();
        await ClosedAsync();

        Assert.Equal(SessionState.Exited, _engine.State);
    }

    // ---- requests carry the caller's token ----

    [Fact]
    public async Task A_cancelled_evaluate_does_not_wait_for_the_adapter()
    {
        await LaunchedAsync();
        using var cts = new CancellationTokenSource();

        var eval = _engine.EvaluateAsync("Slow()", null, null, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => eval.WaitAsync(Bound));
    }

    // ---- breakpoint binding ----

    [Fact]
    public async Task Source_breakpoint_reported_unverified_becomes_verified_on_the_breakpoint_event()
    {
        await LaunchedAsync();
        ScriptBreakpointReply("setBreakpoints", id: 5, thenEvent: new { reason = "changed", breakpoint = new { id = 5, verified = true } });

        var result = await _engine.SetBreakpointsAsync("Program.cs", [new BreakpointRequest(10)]).WaitAsync(Bound);

        var bp = Assert.Single(result);
        Assert.Equal(5, bp.Id);
        Assert.True(bp.Verified);
    }

    [Fact]
    public async Task Function_breakpoint_reported_unverified_becomes_verified_on_the_breakpoint_event()
    {
        await LaunchedAsync();
        ScriptBreakpointReply("setFunctionBreakpoints", id: 8, thenEvent: new { reason = "changed", breakpoint = new { id = 8, verified = true } });

        var result = await _engine.SetFunctionBreakpointsAsync([new FunctionBreakpointRequest("Calc.Compute")]).WaitAsync(Bound);

        var bp = Assert.Single(result);
        Assert.Equal("Calc.Compute", bp.FunctionName);
        Assert.True(bp.Verified);
    }

    [Fact]
    public async Task A_verification_that_arrives_before_the_response_is_not_lost()
    {
        await LaunchedAsync();
        // Event first, then a response that still says unverified: the newer event must win.
        _adapter.Script((req, a) =>
        {
            if (req.GetProperty("command").GetString() != "setBreakpoints") { a.Respond(req); return; }
            a.Event("breakpoint", new { reason = "changed", breakpoint = new { id = 6, verified = true } });
            a.Respond(req, new { breakpoints = new[] { new { id = 6, verified = false, line = 10 } } });
        });

        var result = await _engine.SetBreakpointsAsync("Program.cs", [new BreakpointRequest(10)]).WaitAsync(Bound);

        Assert.True(Assert.Single(result).Verified);
    }

    [Fact]
    public async Task A_breakpoint_that_never_binds_is_reported_unverified_after_the_bounded_wait()
    {
        await LaunchedAsync();
        ScriptBreakpointReply("setBreakpoints", id: 5, thenEvent: null);

        var result = await _engine.SetBreakpointsAsync("Program.cs", [new BreakpointRequest(10)]).WaitAsync(Bound);

        Assert.False(Assert.Single(result).Verified);
    }

    // ---- helpers ----

    // Reply to a breakpoint request with one UNverified breakpoint, then optionally announce its binding.
    void ScriptBreakpointReply(string command, int id, object? thenEvent)
    {
        _adapter.Script((req, a) =>
        {
            if (req.GetProperty("command").GetString() != command) { a.Respond(req); return; }
            a.Respond(req, new { breakpoints = new[] { new { id, verified = false, line = 10 } } });
            if (thenEvent is not null) a.Event("breakpoint", thenEvent);
        });
    }

    // Flush the read loop: a request answered after earlier events (the handshake script
    // answers anything it does not know) means those events were dispatched.
    async Task SyncAsync() => await _adapter.Client.RequestAsync("sync").WaitAsync(Bound);

    // Returns once the client has noticed the adapter is gone: a request fails either at
    // once (closed) or when closing drains it.
    async Task ClosedAsync() => await Assert.ThrowsAsync<IOException>(() => _adapter.Client.RequestAsync("probe").WaitAsync(Bound));
}
