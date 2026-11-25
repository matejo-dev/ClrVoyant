using System.Text.Json;
using ClrVoyant.Core;

namespace ClrVoyant.Dap;

/// <summary>
/// <see cref="IDebugEngine"/> backed by netcoredbg over DAP. Phase 1 implements
/// the lifecycle (launch / attach / terminate) and surfaces stop/exit events;
/// breakpoints, execution control and introspection are layered on in later
/// phases via the underlying <see cref="DapClient"/>.
/// </summary>
public sealed class DapEngine : IDebugEngine
{
    readonly DapClient _client;
    readonly TaskCompletionSource<int> _processStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Latest verified state per breakpoint id, updated by the async 'breakpoint' event.
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _bpVerified = new();
    // Completed (and replaced) on every 'breakpoint' event, so waiters wake on change.
    TaskCompletionSource _bpChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _pid;
    int _lastThreadId;
    int _exitRaised;
    int _faultRaised;
    // Set once we end the session ourselves (terminate/dispose): the adapter going away
    // after that is the expected outcome, not a lost connection.
    volatile bool _ending;
    volatile SessionState _state = SessionState.Starting;

    public int Pid => _pid;

    // A dead adapter means a dead session: report Faulted rather than a stale
    // Running/Stopped, unless the debuggee had already ended normally.
    public SessionState State => _client.IsClosed && _state != SessionState.Exited ? SessionState.Faulted : _state;

    public event Action<StopLocation>? Stopped;
    public event Action<int?>? Exited;
    public event Action<string>? Faulted;
    public event Action<DebugOutput>? Output;

    public DapEngine(string netcoredbgPath) : this(new DapClient(netcoredbgPath)) { }

    /// <summary>Engine over an existing client (the seam in-process tests use). The
    /// engine owns the client and disposes it.</summary>
    public DapEngine(DapClient client)
    {
        _client = client;
        _client.ProcessStarted += pid => { _pid = pid; _processStarted.TrySetResult(pid); };
        _client.Stopped += body =>
        {
            _state = SessionState.Stopped;
            if (body.TryGetProperty("threadId", out var t)) _lastThreadId = t.GetInt32();
            Stopped?.Invoke(ToStopLocation(body));
        };
        // netcoredbg reports 'exited' (with the exit code) and then 'terminated' (without
        // one). The session must hear about the end once, so whichever comes first raises
        // Exited; the code is only known from 'exited', which precedes 'terminated'.
        _client.ExitedEvent += body =>
            OnEnded(body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("exitCode", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out int exitCode) ? exitCode : null);
        _client.TerminatedEvent += _ => OnEnded(null);
        // A dead adapter before the debuggee's end was reported leaves the session in an
        // unknown state; say so, so a waiting resume does not sit out its whole timeout.
        _client.ConnectionClosed += cause =>
        {
            if (_ending || Volatile.Read(ref _exitRaised) == 1) return;
            if (Interlocked.Exchange(ref _faultRaised, 1) == 0) Faulted?.Invoke(cause.Message);
        };
        _client.Output += (category, text) => Output?.Invoke(new DebugOutput(category, text));
        _client.BreakpointChanged += body =>
        {
            // DAP 'breakpoint' event body is { reason, breakpoint: { id, verified, ... } }.
            if (body.TryGetProperty("breakpoint", out var bp)
                && bp.TryGetProperty("id", out var id)
                && bp.TryGetProperty("verified", out var v))
            {
                _bpVerified[id.GetInt32()] = v.GetBoolean();
                Interlocked.Exchange(ref _bpChanged, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                    .TrySetResult();
            }
        };
    }

    void OnEnded(int? exitCode)
    {
        _state = SessionState.Exited;
        if (Interlocked.Exchange(ref _exitRaised, 1) == 0) Exited?.Invoke(exitCode);
    }

    static StopLocation ToStopLocation(JsonElement body)
    {
        string? reason = body.TryGetProperty("reason", out var r) ? r.GetString() : null;
        int? thread = body.TryGetProperty("threadId", out var t) ? t.GetInt32() : null;
        // file/line are resolved via a stackTrace request in a later phase.
        return new StopLocation(null, null, reason, thread);
    }

    // How long we wait for netcoredbg to become ready / answer a launch/attach
    // before giving up rather than hanging forever.
    static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);

    public async Task LaunchAsync(LaunchRequest request, CancellationToken ct = default)
    {
        string program = Path.GetFullPath(request.Program);
        _client.Start();
        await _client.RequestAsync("initialize", InitArgs(), ct);
        // The launch response may legitimately arrive only after configurationDone
        // (DAP ordering), so we don't await it inline — but we KEEP the task so a
        // rejection surfaces with netcoredbg's real message instead of being lost.
        var launchArgs = new Dictionary<string, object>
        {
            ["request"] = "launch",
            ["type"] = "coreclr",
            ["program"] = program,
            ["args"] = request.Args ?? Array.Empty<string>(),
            ["cwd"] = request.Cwd ?? Path.GetDirectoryName(program)!,
            ["stopAtEntry"] = request.StopAtEntry,
            ["justMyCode"] = false,
        };
        // netcoredbg reads the debuggee environment from 'env' as a name -> value map; it
        // is merged over the environment the debuggee inherits.
        if (request.Env is { Count: > 0 } env) launchArgs["env"] = new Dictionary<string, string>(env);
        // No request timeout: this response is bounded by ReadyTimeout below instead.
        var launch = Observed(_client.RequestAsync("launch", launchArgs, ct, Timeout.InfiniteTimeSpan));
        await WaitReadyOrThrowAsync(launch, ct);
        await _client.RequestAsync("configurationDone", null, ct);
        await ObserveStartAsync(launch, ct);
        await WaitProcessAsync(ct);
        if (_state == SessionState.Starting) _state = SessionState.Running;
    }

    public async Task AttachAsync(int pid, CancellationToken ct = default)
    {
        _pid = pid;
        _client.Start();
        await _client.RequestAsync("initialize", InitArgs(), ct);
        var attach = Observed(_client.RequestAsync("attach", new { request = "attach", type = "coreclr", processId = pid },
            ct, Timeout.InfiniteTimeSpan)); // bounded by ReadyTimeout, as for launch
        await WaitReadyOrThrowAsync(attach, ct);
        await _client.RequestAsync("configurationDone", null, ct);
        await ObserveStartAsync(attach, ct);
        if (_state == SessionState.Starting) _state = SessionState.Running;
    }

    // A launch/attach task is awaited only on the happy path; make sure a late failure
    // (e.g. the connection closing while it is still pending) is never left unobserved.
    static Task<JsonElement> Observed(Task<JsonElement> t)
    {
        _ = t.ContinueWith(static x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return t;
    }

    /// <summary>
    /// Wait until netcoredbg signals it is ready for <c>configurationDone</c> (the DAP
    /// <c>initialized</c> event). The launch/attach request is in flight: if it is
    /// REJECTED first — e.g. attach denied because another debugger (Visual Studio)
    /// already owns the process — that rejection lands here and we rethrow it so the
    /// caller sees netcoredbg's actual reason instead of an unobserved exception and a
    /// hang on an <c>initialized</c> event that will never come.
    /// </summary>
    async Task WaitReadyOrThrowAsync(Task<JsonElement> launchOrAttach, CancellationToken ct)
    {
        var initialized = _client.WaitInitializedAsync();
        var timeout = Task.Delay(ReadyTimeout, ct);
        var first = await Task.WhenAny(initialized, launchOrAttach, timeout);
        ct.ThrowIfCancellationRequested();
        if (first != timeout && first.IsFaulted)
            await first; // rethrows the DapException carrying netcoredbg's message, or the closed-connection error
        if (first == timeout)
            throw new TimeoutException($"netcoredbg did not become ready within {ReadyTimeout.TotalSeconds:0}s.");
        // Attach/launch may have answered (successfully) before 'initialized'; make
        // sure we still have the green light for configurationDone.
        if (!initialized.IsCompleted && await Task.WhenAny(initialized, timeout) == timeout)
            throw new TimeoutException($"netcoredbg did not become ready within {ReadyTimeout.TotalSeconds:0}s.");
        await initialized;
    }

    /// <summary>
    /// After configurationDone, observe the launch/attach response so a failure that
    /// is only reported at this late stage is surfaced. If the response succeeds it is
    /// consumed; if the adapter keeps it pending we attach a guard so an eventual
    /// failure never escapes as an unobserved task exception.
    /// </summary>
    static async Task ObserveStartAsync(Task<JsonElement> launchOrAttach, CancellationToken ct)
    {
        var first = await Task.WhenAny(launchOrAttach, Task.Delay(ReadyTimeout, ct));
        ct.ThrowIfCancellationRequested();
        if (first == launchOrAttach)
            await launchOrAttach; // rethrows if the start ultimately failed
        else
            _ = launchOrAttach.ContinueWith(static t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    static object InitArgs() => new
    {
        clientID = "clrvoyant",
        adapterID = "coreclr",
        linesStartAt1 = true,
        columnsStartAt1 = true,
        pathFormat = "path",
        supportsRunInTerminalRequest = false,
    };

    async Task WaitProcessAsync(CancellationToken ct)
    {
        // Resolve the target PID, but don't hang forever if no 'process' event.
        await Task.WhenAny(_processStarted.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
        ct.ThrowIfCancellationRequested();
    }

    // Bound for the disconnect round trip: it is answered quickly by a healthy adapter,
    // and a session being torn down must never hang on a dead or wedged one.
    static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(5);

    public async Task TerminateAsync(bool terminateDebuggee, CancellationToken ct = default)
    {
        _ending = true;
        try { await _client.RequestAsync("disconnect", new { terminateDebuggee }, ct, DisconnectTimeout); }
        catch { /* adapter already gone, wedged, or cancelled: the caller disposes the engine, which kills it */ }
        // Whatever the adapter did, the session is over: report it once (the code, if
        // netcoredbg sent 'exited' during the disconnect, was already reported).
        OnEnded(null);
    }

    // --- Breakpoints ---
    public async Task<IReadOnlyList<Breakpoint>> SetBreakpointsAsync(string file, IReadOnlyList<BreakpointRequest> breakpoints, CancellationToken ct = default)
    {
        string path = Path.GetFullPath(file);
        var bps = breakpoints.Select(b =>
        {
            var d = new Dictionary<string, object> { ["line"] = b.Line };
            if (b.Condition is not null) d["condition"] = b.Condition;
            if (b.HitCondition is not null) d["hitCondition"] = b.HitCondition;
            if (b.LogMessage is not null) d["logMessage"] = b.LogMessage;
            return d;
        }).ToArray();

        var resp = await _client.RequestAsync("setBreakpoints", new { source = new { path }, breakpoints = bps }, ct);
        var result = new List<Breakpoint>();
        int i = 0;
        foreach (var e in resp.GetProperty("body").GetProperty("breakpoints").EnumerateArray())
        {
            int id = e.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
            bool verified = e.TryGetProperty("verified", out var v) && v.GetBoolean();
            int line = e.TryGetProperty("line", out var ln) ? ln.GetInt32() : breakpoints[i].Line;
            if (id != 0) _bpVerified.AddOrUpdate(id, verified, (_, known) => known || verified);
            result.Add(new Breakpoint(id, verified, path, line));
            i++;
        }

        await ApplyLateVerificationAsync(result, b => b.Id, b => b.Verified, (b, v) => b with { Verified = v }, ct);
        return result;
    }

    public async Task<IReadOnlyList<FunctionBreakpoint>> SetFunctionBreakpointsAsync(IReadOnlyList<FunctionBreakpointRequest> breakpoints, CancellationToken ct = default)
    {
        var bps = breakpoints.Select(b =>
        {
            var d = new Dictionary<string, object> { ["name"] = b.FunctionName };
            if (b.Condition is not null) d["condition"] = b.Condition;
            if (b.HitCondition is not null) d["hitCondition"] = b.HitCondition;
            return d;
        }).ToArray();

        var resp = await _client.RequestAsync("setFunctionBreakpoints", new { breakpoints = bps }, ct);
        var result = new List<FunctionBreakpoint>();
        int i = 0;
        foreach (var e in resp.GetProperty("body").GetProperty("breakpoints").EnumerateArray())
        {
            int id = e.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
            bool verified = e.TryGetProperty("verified", out var v) && v.GetBoolean();
            string? file = e.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object && src.TryGetProperty("path", out var p)
                ? p.GetString() : null;
            int? line = e.TryGetProperty("line", out var ln) && ln.GetInt32() > 0 ? ln.GetInt32() : null;
            if (id != 0) _bpVerified.AddOrUpdate(id, verified, (_, known) => known || verified);
            result.Add(new FunctionBreakpoint(id, verified, breakpoints[i].FunctionName, file, line));
            i++;
        }

        await ApplyLateVerificationAsync(result, b => b.Id, b => b.Verified, (b, v) => b with { Verified = v }, ct);
        return result;
    }

    // How long to wait for netcoredbg to bind breakpoints it reported as unverified.
    static readonly TimeSpan BreakpointBindTimeout = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// netcoredbg often answers setBreakpoints/setFunctionBreakpoints with
    /// verified=false and binds asynchronously, announcing it in a 'breakpoint' event.
    /// Wait (event-driven, bounded by <see cref="BreakpointBindTimeout"/>) for the flip so
    /// the reported state is accurate, then fold it into <paramref name="items"/>.
    /// </summary>
    async Task ApplyLateVerificationAsync<T>(List<T> items, Func<T, int> idOf, Func<T, bool> verifiedOf,
        Func<T, bool, T> withVerified, CancellationToken ct)
    {
        var pending = items.Where(i => !verifiedOf(i) && idOf(i) != 0).Select(idOf).ToList();
        if (pending.Count == 0) return;

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(BreakpointBindTimeout);
        try
        {
            while (true)
            {
                // Take the signal BEFORE checking state, so an event in between is not missed.
                var changed = Volatile.Read(ref _bpChanged).Task;
                if (pending.All(IsVerified)) break;
                await changed.WaitAsync(window.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* bind window over */ }

        for (int k = 0; k < items.Count; k++)
            if (!verifiedOf(items[k]) && IsVerified(idOf(items[k])))
                items[k] = withVerified(items[k], true);
    }

    bool IsVerified(int id) => _bpVerified.TryGetValue(id, out var v) && v;

    public Task SetExceptionBreakpointsAsync(IReadOnlyList<string> filters, CancellationToken ct = default)
        => _client.RequestAsync("setExceptionBreakpoints", new { filters = filters.ToArray() }, ct);

    // --- Execution control (send only) ---
    public Task ContinueAsync(int? threadId, CancellationToken ct = default)
        => ResumeAsync("continue", new { threadId = threadId ?? _lastThreadId }, ct);

    public Task StepAsync(StepKind kind, int threadId, CancellationToken ct = default)
    {
        string command = kind switch
        {
            StepKind.Over => "next",
            StepKind.Into => "stepIn",
            StepKind.Out => "stepOut",
            _ => "next",
        };
        return ResumeAsync(command, new { threadId }, ct);
    }

    // The target counts as running from the moment the command is sent (its 'stopped' event
    // can beat the response). If the command is rejected the target did not move: put the
    // state back, unless a stop event already replaced it.
    async Task ResumeAsync(string command, object args, CancellationToken ct)
    {
        var before = _state;
        _state = SessionState.Running;
        try { await _client.RequestAsync(command, args, ct); }
        catch
        {
            if (_state == SessionState.Running) _state = before;
            throw;
        }
    }

    public async Task PauseAsync(int? threadId, CancellationToken ct = default)
    {
        // netcoredbg's pause requires a valid threadId. When the target is running
        // we have none cached, so discover one first.
        int tid = threadId ?? _lastThreadId;
        if (tid == 0)
        {
            try
            {
                var threads = await GetThreadsAsync(ct);
                if (threads.Count > 0) tid = threads[0].Id;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* threads may not be enumerable while running */ }
        }

        // netcoredbg requires a threadId. If we still have none (it does not
        // enumerate threads of a freely-running process), this will fail with a
        // clear error — pause works once a thread is known from a prior stop.
        await _client.RequestAsync("pause", new { threadId = tid }, ct);
    }

    // --- Introspection ---
    public async Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct = default)
    {
        var resp = await _client.RequestAsync("threads", null, ct);
        var list = new List<ThreadInfo>();
        foreach (var t in resp.GetProperty("body").GetProperty("threads").EnumerateArray())
            list.Add(new ThreadInfo(t.GetProperty("id").GetInt32(), t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
        return list;
    }

    public async Task<IReadOnlyList<StackFrame>> GetStackTraceAsync(int threadId, int startFrame, int levels, CancellationToken ct = default)
    {
        var resp = await _client.RequestAsync("stackTrace", new { threadId, startFrame, levels }, ct);
        var list = new List<StackFrame>();
        foreach (var f in resp.GetProperty("body").GetProperty("stackFrames").EnumerateArray())
        {
            string? file = f.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object && src.TryGetProperty("path", out var p)
                ? p.GetString() : null;
            int? line = f.TryGetProperty("line", out var ln) && ln.GetInt32() > 0 ? ln.GetInt32() : null;
            list.Add(new StackFrame(f.GetProperty("id").GetInt32(), f.GetProperty("name").GetString() ?? "?", file, line));
        }
        return list;
    }

    public async Task<IReadOnlyList<Scope>> GetScopesAsync(int frameId, CancellationToken ct = default)
    {
        var resp = await _client.RequestAsync("scopes", new { frameId }, ct);
        var list = new List<Scope>();
        foreach (var s in resp.GetProperty("body").GetProperty("scopes").EnumerateArray())
            list.Add(new Scope(s.GetProperty("name").GetString() ?? "?", s.GetProperty("variablesReference").GetInt32()));
        return list;
    }

    public async Task<IReadOnlyList<Variable>> GetVariablesAsync(int variablesReference, CancellationToken ct = default)
    {
        var resp = await _client.RequestAsync("variables", new { variablesReference }, ct);
        var list = new List<Variable>();
        foreach (var v in resp.GetProperty("body").GetProperty("variables").EnumerateArray())
            list.Add(new Variable(
                v.GetProperty("name").GetString() ?? "?",
                v.TryGetProperty("value", out var val) ? val.GetString() ?? "" : "",
                v.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "",
                v.TryGetProperty("variablesReference", out var vr) ? vr.GetInt32() : 0));
        return list;
    }

    public async Task<EvaluateResult> EvaluateAsync(string expression, int? frameId, string? context, CancellationToken ct = default)
    {
        var args = new Dictionary<string, object> { ["expression"] = expression, ["context"] = context ?? "repl" };
        if (frameId is int fid) args["frameId"] = fid;
        var resp = await _client.RequestAsync("evaluate", args, ct);
        var body = resp.GetProperty("body");
        return new EvaluateResult(
            body.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "",
            body.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "",
            body.TryGetProperty("variablesReference", out var vr) ? vr.GetInt32() : 0);
    }

    public async Task<ExceptionInfo?> GetExceptionInfoAsync(int threadId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _client.RequestAsync("exceptionInfo", new { threadId }, ct);
            var body = resp.GetProperty("body");
            string id = body.TryGetProperty("exceptionId", out var ei) ? ei.GetString() ?? "" : "";
            string desc = body.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            string? details = body.TryGetProperty("details", out var det) ? det.ToString() : null;
            return new ExceptionInfo(id, desc, details);
        }
        catch (DapException) { return null; }
    }

    public ValueTask DisposeAsync()
    {
        _ending = true;
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
