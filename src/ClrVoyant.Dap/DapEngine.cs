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
    int _pid;
    int _lastThreadId;

    public int Pid => _pid;
    public SessionState State { get; private set; } = SessionState.Starting;

    public event Action<StopLocation>? Stopped;
    public event Action<int>? Exited;

    public DapEngine(string netcoredbgPath)
    {
        _client = new DapClient(netcoredbgPath);
        _client.ProcessStarted += pid => { _pid = pid; _processStarted.TrySetResult(pid); };
        _client.Stopped += body =>
        {
            State = SessionState.Stopped;
            if (body.TryGetProperty("threadId", out var t)) _lastThreadId = t.GetInt32();
            Stopped?.Invoke(ToStopLocation(body));
        };
        _client.ExitedEvent += _ =>
        {
            State = SessionState.Exited;
            Exited?.Invoke(0);
        };
        _client.BreakpointChanged += body =>
        {
            // DAP 'breakpoint' event body is { reason, breakpoint: { id, verified, ... } }.
            if (body.TryGetProperty("breakpoint", out var bp)
                && bp.TryGetProperty("id", out var id)
                && bp.TryGetProperty("verified", out var v))
                _bpVerified[id.GetInt32()] = v.GetBoolean();
        };
    }

    static StopLocation ToStopLocation(JsonElement body)
    {
        string? reason = body.TryGetProperty("reason", out var r) ? r.GetString() : null;
        int? thread = body.TryGetProperty("threadId", out var t) ? t.GetInt32() : null;
        // file/line are resolved via a stackTrace request in a later phase.
        return new StopLocation(null, null, reason, thread);
    }

    public async Task LaunchAsync(LaunchRequest request, CancellationToken ct = default)
    {
        string program = Path.GetFullPath(request.Program);
        _client.Start();
        await _client.RequestAsync("initialize", InitArgs());
        // launch response may arrive after configurationDone; do not await it here.
        _ = _client.RequestAsync("launch", new
        {
            request = "launch",
            type = "coreclr",
            program,
            args = request.Args ?? Array.Empty<string>(),
            cwd = request.Cwd ?? Path.GetDirectoryName(program),
            stopAtEntry = request.StopAtEntry,
            justMyCode = false,
        });
        await _client.WaitInitializedAsync();
        await _client.RequestAsync("configurationDone");
        await WaitProcessAsync(ct);
        if (State == SessionState.Starting) State = SessionState.Running;
    }

    public async Task AttachAsync(int pid, CancellationToken ct = default)
    {
        _pid = pid;
        _client.Start();
        await _client.RequestAsync("initialize", InitArgs());
        _ = _client.RequestAsync("attach", new { request = "attach", type = "coreclr", processId = pid });
        await _client.WaitInitializedAsync();
        await _client.RequestAsync("configurationDone");
        if (State == SessionState.Starting) State = SessionState.Running;
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
    }

    public async Task TerminateAsync(CancellationToken ct = default)
    {
        try { await _client.RequestAsync("disconnect", new { terminateDebuggee = true }); }
        catch { /* engine may already be gone */ }
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

        var resp = await _client.RequestAsync("setBreakpoints", new { source = new { path }, breakpoints = bps });
        var result = new List<Breakpoint>();
        int i = 0;
        foreach (var e in resp.GetProperty("body").GetProperty("breakpoints").EnumerateArray())
        {
            int id = e.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
            bool verified = e.TryGetProperty("verified", out var v) && v.GetBoolean();
            int line = e.TryGetProperty("line", out var ln) ? ln.GetInt32() : breakpoints[i].Line;
            if (id != 0) _bpVerified[id] = verified;
            result.Add(new Breakpoint(id, verified, path, line));
            i++;
        }

        // netcoredbg often returns verified=false and binds the breakpoint
        // asynchronously via a 'breakpoint' event. Briefly wait for that flip so
        // the reported state is accurate.
        if (result.Any(b => !b.Verified && b.Id != 0))
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
            while (DateTime.UtcNow < deadline && result.Any(b => !b.Verified && b.Id != 0))
            {
                await Task.Delay(30, ct);
                for (int k = 0; k < result.Count; k++)
                    if (!result[k].Verified && result[k].Id != 0 && _bpVerified.TryGetValue(result[k].Id, out var nv) && nv)
                        result[k] = result[k] with { Verified = true };
            }
        }
        return result;
    }

    public Task SetExceptionBreakpointsAsync(IReadOnlyList<string> filters, CancellationToken ct = default)
        => _client.RequestAsync("setExceptionBreakpoints", new { filters = filters.ToArray() });

    // --- Execution control (send only) ---
    public Task ContinueAsync(int? threadId, CancellationToken ct = default)
    {
        State = SessionState.Running;
        return _client.RequestAsync("continue", new { threadId = threadId ?? _lastThreadId });
    }

    public Task StepAsync(StepKind kind, int threadId, CancellationToken ct = default)
    {
        State = SessionState.Running;
        string command = kind switch
        {
            StepKind.Over => "next",
            StepKind.Into => "stepIn",
            StepKind.Out => "stepOut",
            _ => "next",
        };
        return _client.RequestAsync(command, new { threadId });
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
            catch { /* threads may not be enumerable while running */ }
        }

        // netcoredbg requires a threadId. If we still have none (it does not
        // enumerate threads of a freely-running process), this will fail with a
        // clear error — pause works once a thread is known from a prior stop.
        await _client.RequestAsync("pause", new { threadId = tid });
    }

    // --- Introspection ---
    public async Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct = default)
    {
        var resp = await _client.RequestAsync("threads");
        var list = new List<ThreadInfo>();
        foreach (var t in resp.GetProperty("body").GetProperty("threads").EnumerateArray())
            list.Add(new ThreadInfo(t.GetProperty("id").GetInt32(), t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
        return list;
    }

    public async Task<IReadOnlyList<StackFrame>> GetStackTraceAsync(int threadId, int startFrame, int levels, CancellationToken ct = default)
    {
        var resp = await _client.RequestAsync("stackTrace", new { threadId, startFrame, levels });
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
        var resp = await _client.RequestAsync("scopes", new { frameId });
        var list = new List<Scope>();
        foreach (var s in resp.GetProperty("body").GetProperty("scopes").EnumerateArray())
            list.Add(new Scope(s.GetProperty("name").GetString() ?? "?", s.GetProperty("variablesReference").GetInt32()));
        return list;
    }

    public async Task<IReadOnlyList<Variable>> GetVariablesAsync(int variablesReference, CancellationToken ct = default)
    {
        var resp = await _client.RequestAsync("variables", new { variablesReference });
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
        var resp = await _client.RequestAsync("evaluate", args);
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
            var resp = await _client.RequestAsync("exceptionInfo", new { threadId });
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
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
