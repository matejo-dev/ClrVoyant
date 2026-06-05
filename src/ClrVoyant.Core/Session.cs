namespace ClrVoyant.Core;

/// <summary>
/// One debugged process: an id, its engine, and the wait/stop coordination.
///
/// Stop routing: a resume operation (continue/step) arms a "targeted waiter";
/// the next stop completes that waiter and is NOT treated as spontaneous. A stop
/// with no targeted waiter (e.g. a breakpoint hit while the agent was doing
/// something else, or stopAtEntry) raises <see cref="SpontaneousStop"/>, which
/// the SessionManager feeds into wait_for_any_stop. <see cref="LastStop"/> is
/// updated on every stop so debug_status always reflects the current location.
/// </summary>
public sealed class Session : IAsyncDisposable
{
    readonly object _gate = new();
    TaskCompletionSource<StopLocation?>? _targetedWaiter;

    // Server-side breakpoint store. DAP setBreakpoints replaces ALL breakpoints
    // for a source, so we keep the desired set per file and assign our own stable
    // ids (engine ids change on each re-send). All access is serialized by _bpLock.
    readonly SemaphoreSlim _bpLock = new(1, 1);
    readonly Dictionary<string, List<(int id, BreakpointRequest req)>> _bpsByFile = new();
    readonly Dictionary<int, (bool verified, int line, string file)> _bpResult = new();
    // Function breakpoints are global (DAP setFunctionBreakpoints replaces the whole
    // set), kept in their own list but sharing _bpCounter so ids are unique across
    // both kinds and _bpLock so all breakpoint mutations are serialized together.
    readonly List<(int id, FunctionBreakpointRequest req)> _fnBps = new();
    readonly Dictionary<int, FunctionBreakpoint> _fnResult = new();
    int _bpCounter;

    public string Id { get; }
    public IDebugEngine Engine { get; private set; }

    /// <summary>The request used to launch this session, or null if it was created
    /// by attaching to an existing process. Only launched sessions can be restarted.</summary>
    public LaunchRequest? LaunchInfo { get; internal set; }

    /// <summary>An external resource whose lifetime is tied to this session and is
    /// disposed when the session ends (e.g. the <c>dotnet test</c> driver process
    /// behind a test-debug session). Optional; null for ordinary sessions.</summary>
    public IAsyncDisposable? OwnedResource { get; set; }

    /// <summary>Bumped on every stop; invalidates per-stop handles from prior stops.</summary>
    public long StopId { get; private set; }
    public StopLocation? LastStop { get; private set; }

    /// <summary>Raised for a stop that no resume operation was waiting for.</summary>
    public event Action<Session, StopLocation>? SpontaneousStop;

    public Session(string id, IDebugEngine engine)
    {
        Id = id;
        Engine = engine;
        Subscribe(engine);
    }

    void Subscribe(IDebugEngine engine)
    {
        engine.Stopped += OnStopped;
        engine.Exited += OnExited;
    }

    void Unsubscribe(IDebugEngine engine)
    {
        engine.Stopped -= OnStopped;
        engine.Exited -= OnExited;
    }

    public SessionState State => Engine.State;
    public int Pid => Engine.Pid;
    public SessionInfo ToInfo() => new(Id, Pid, State, LastStop);

    void OnStopped(StopLocation loc)
    {
        lock (_gate) { StopId++; LastStop = loc; }
        var waiter = TakeWaiter();
        if (waiter is not null) waiter.TrySetResult(loc);
        else SpontaneousStop?.Invoke(this, loc);
    }

    void OnExited(int code)
    {
        var waiter = TakeWaiter();
        waiter?.TrySetResult(null); // null => exited
    }

    TaskCompletionSource<StopLocation?>? TakeWaiter()
    {
        lock (_gate) { var w = _targetedWaiter; _targetedWaiter = null; return w; }
    }

    /// <summary>Send a resume command and wait for the next stop, the process
    /// exiting, or the timeout. On timeout the target is left running.</summary>
    public async Task<StopOutcome> ResumeAndWaitAsync(Func<Task> sendCommand, TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<StopLocation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _targetedWaiter = tcs; }

        await sendCommand();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
        if (completed != tcs.Task)
        {
            TakeWaiter(); // drop the stale waiter so a later stop is spontaneous
            return new StopOutcome(State, null); // timed out: still running
        }

        var loc = await tcs.Task;
        return loc is null
            ? new StopOutcome(SessionState.Exited, null)
            : new StopOutcome(SessionState.Stopped, await EnrichAsync(loc, ct));
    }

    /// <summary>Fill file/line on a stop by reading the top stack frame.</summary>
    public async Task<StopLocation> EnrichAsync(StopLocation loc, CancellationToken ct = default)
    {
        if (loc.ThreadId is not int tid) return loc;
        try
        {
            var frames = await Engine.GetStackTraceAsync(tid, 0, 1, ct);
            if (frames.Count > 0)
            {
                var enriched = loc with { File = frames[0].File, Line = frames[0].Line };
                lock (_gate) { LastStop = enriched; }
                return enriched;
            }
        }
        catch { /* best effort */ }
        return loc;
    }

    public void EnsureStopped()
    {
        if (State != SessionState.Stopped)
            throw new InvalidOperationException($"Session '{Id}' is '{State}', not stopped. This operation requires a stopped target.");
    }

    // --- Breakpoints (allowed while running) ---
    public async Task<Breakpoint> AddBreakpointAsync(string file, BreakpointRequest req, CancellationToken ct = default)
    {
        string path = Path.GetFullPath(file);
        await _bpLock.WaitAsync(ct);
        try
        {
            int id = ++_bpCounter;
            if (!_bpsByFile.TryGetValue(path, out var list)) { list = new(); _bpsByFile[path] = list; }
            list.Add((id, req));
            await ResendAsync(path, ct);
            var r = _bpResult[id];
            return new Breakpoint(id, r.verified, r.file, r.line);
        }
        finally { _bpLock.Release(); }
    }

    /// <summary>Set a function (method-name) breakpoint. Binds from the PDB without a
    /// source file or line — the no-source case (deployed DLLs + PDBs). The whole set
    /// is re-sent on every change because DAP setFunctionBreakpoints is replace-all.</summary>
    public async Task<FunctionBreakpoint> AddFunctionBreakpointAsync(FunctionBreakpointRequest req, CancellationToken ct = default)
    {
        await _bpLock.WaitAsync(ct);
        try
        {
            int id = ++_bpCounter;
            _fnBps.Add((id, req));
            await ResendFunctionsAsync(ct);
            return _fnResult[id];
        }
        finally { _bpLock.Release(); }
    }

    public async Task<bool> RemoveBreakpointAsync(int bpId, CancellationToken ct = default)
    {
        await _bpLock.WaitAsync(ct);
        try
        {
            foreach (var (path, list) in _bpsByFile)
            {
                int idx = list.FindIndex(e => e.id == bpId);
                if (idx < 0) continue;
                list.RemoveAt(idx);
                _bpResult.Remove(bpId);
                await ResendAsync(path, ct);
                return true;
            }

            int fnIdx = _fnBps.FindIndex(e => e.id == bpId);
            if (fnIdx >= 0)
            {
                _fnBps.RemoveAt(fnIdx);
                _fnResult.Remove(bpId);
                await ResendFunctionsAsync(ct);
                return true;
            }
            return false;
        }
        finally { _bpLock.Release(); }
    }

    public async Task<IReadOnlyList<Breakpoint>> ListBreakpointsAsync(CancellationToken ct = default)
    {
        await _bpLock.WaitAsync(ct);
        try
        {
            var result = new List<Breakpoint>();
            foreach (var (_, list) in _bpsByFile)
                foreach (var (id, _) in list)
                    if (_bpResult.TryGetValue(id, out var r))
                        result.Add(new Breakpoint(id, r.verified, r.file, r.line));
            return result;
        }
        finally { _bpLock.Release(); }
    }

    public async Task<IReadOnlyList<FunctionBreakpoint>> ListFunctionBreakpointsAsync(CancellationToken ct = default)
    {
        await _bpLock.WaitAsync(ct);
        try
        {
            return _fnBps
                .Where(e => _fnResult.ContainsKey(e.id))
                .Select(e => _fnResult[e.id])
                .ToList();
        }
        finally { _bpLock.Release(); }
    }

    /// <summary>Remove every breakpoint in every file (sending an empty set to the
    /// engine per file). Returns how many breakpoints were cleared.</summary>
    public async Task<int> ClearBreakpointsAsync(CancellationToken ct = default)
    {
        await _bpLock.WaitAsync(ct);
        try
        {
            int count = _bpsByFile.Values.Sum(l => l.Count) + _fnBps.Count;
            foreach (var path in _bpsByFile.Keys.ToList())
            {
                _bpsByFile[path].Clear();
                await ResendAsync(path, ct); // empty set => engine clears the file
            }
            _bpsByFile.Clear();
            _bpResult.Clear();

            if (_fnBps.Count > 0)
            {
                _fnBps.Clear();
                await ResendFunctionsAsync(ct); // empty set => engine clears function bps
            }
            _fnResult.Clear();
            return count;
        }
        finally { _bpLock.Release(); }
    }

    // Re-send the full desired set for a file and remap results to our stable ids.
    async Task ResendAsync(string path, CancellationToken ct)
    {
        var list = _bpsByFile[path];
        var reqs = list.Select(e => e.req).ToList();
        var results = await Engine.SetBreakpointsAsync(path, reqs, ct);
        for (int i = 0; i < list.Count && i < results.Count; i++)
            _bpResult[list[i].id] = (results[i].Verified, results[i].Line, path);
    }

    // Re-send the full desired function-breakpoint set and remap onto stable ids.
    async Task ResendFunctionsAsync(CancellationToken ct)
    {
        var reqs = _fnBps.Select(e => e.req).ToList();
        var results = await Engine.SetFunctionBreakpointsAsync(reqs, ct);
        for (int i = 0; i < _fnBps.Count && i < results.Count; i++)
            _fnResult[_fnBps[i].id] = results[i] with { Id = _fnBps[i].id };
    }

    public Task SetExceptionBreakpointsAsync(IReadOnlyList<string> filters, CancellationToken ct = default)
        => Engine.SetExceptionBreakpointsAsync(filters, ct);

    // --- Execution control (wait-based) ---
    public Task<StopOutcome> ContinueAsync(int? threadId, TimeSpan timeout, CancellationToken ct = default)
        => ResumeAndWaitAsync(() => Engine.ContinueAsync(threadId, ct), timeout, ct);
    public Task<StopOutcome> StepAsync(StepKind kind, int threadId, TimeSpan timeout, CancellationToken ct = default)
        => ResumeAndWaitAsync(() => Engine.StepAsync(kind, threadId, ct), timeout, ct);
    public Task PauseAsync(int? threadId, CancellationToken ct = default)
        => Engine.PauseAsync(threadId, ct);

    // --- Introspection (target must be stopped) ---
    public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct = default)
    { EnsureStopped(); return Engine.GetThreadsAsync(ct); }
    public Task<IReadOnlyList<StackFrame>> GetStackTraceAsync(int threadId, int startFrame, int levels, CancellationToken ct = default)
    { EnsureStopped(); return Engine.GetStackTraceAsync(threadId, startFrame, levels, ct); }
    public Task<IReadOnlyList<Scope>> GetScopesAsync(int frameId, CancellationToken ct = default)
    { EnsureStopped(); return Engine.GetScopesAsync(frameId, ct); }
    public Task<IReadOnlyList<Variable>> GetVariablesAsync(int variablesReference, CancellationToken ct = default)
    { EnsureStopped(); return Engine.GetVariablesAsync(variablesReference, ct); }
    public Task<EvaluateResult> EvaluateAsync(string expression, int? frameId, string? context, CancellationToken ct = default)
    { EnsureStopped(); return Engine.EvaluateAsync(expression, frameId, context, ct); }
    public Task<ExceptionInfo?> GetExceptionInfoAsync(int threadId, CancellationToken ct = default)
    { EnsureStopped(); return Engine.GetExceptionInfoAsync(threadId, ct); }

    /// <summary>Restart the debuggee in place, keeping the same session id and
    /// breakpoint set. Terminates the current engine, swaps in <paramref name="newEngine"/>
    /// (a DAP engine is single-use — the netcoredbg handshake cannot be replayed),
    /// relaunches with <paramref name="req"/>, then re-applies all breakpoints.</summary>
    public async Task RestartAsync(LaunchRequest req, IDebugEngine newEngine, CancellationToken ct = default)
    {
        var old = Engine;
        try { await old.TerminateAsync(ct); } catch { /* may already be gone */ }
        Unsubscribe(old);
        try { await old.DisposeAsync(); } catch { /* best effort */ }

        Engine = newEngine;
        Subscribe(newEngine);

        // Invalidate any per-stop handles and abandon a pending waiter from the old run.
        lock (_gate) { StopId++; LastStop = null; TakeWaiter()?.TrySetResult(null); }

        await newEngine.LaunchAsync(req, ct);
        LaunchInfo = req;
        await ReapplyAllBreakpointsAsync(ct);
    }

    // Re-send the desired breakpoint set for every file to the current engine
    // (used after an engine swap on restart). Results remap onto the stable ids.
    async Task ReapplyAllBreakpointsAsync(CancellationToken ct)
    {
        await _bpLock.WaitAsync(ct);
        try
        {
            foreach (var path in _bpsByFile.Keys.ToList())
                await ResendAsync(path, ct);
            if (_fnBps.Count > 0)
                await ResendFunctionsAsync(ct);
        }
        finally { _bpLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (OwnedResource is not null)
        {
            try { await OwnedResource.DisposeAsync(); } catch { /* best effort */ }
        }
        await Engine.DisposeAsync();
    }
}
