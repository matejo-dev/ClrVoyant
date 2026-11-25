namespace ClrVoyant.Core;

/// <summary>
/// One debugged process: an id, its engine, and the wait/stop coordination.
///
/// Event routing: a resume operation (continue/step) arms a "targeted waiter";
/// the next stop, exit or loss of the debugger connection completes that waiter and
/// is NOT treated as spontaneous. The same events with no targeted waiter (a
/// breakpoint hit while the agent was doing something else, stopAtEntry, a process
/// dying while nobody waits) raise <see cref="SpontaneousStop"/>, which the
/// SessionManager feeds into wait_for_any_stop. Only one resume operation may be in
/// flight per session. <see cref="LastStop"/> is updated on every stop so
/// debug_status always reflects the current location.
/// </summary>
public sealed class Session : IAsyncDisposable
{
    readonly object _gate = new();
    TaskCompletionSource<StopOutcome>? _targetedWaiter;

    // Server-side breakpoint store. DAP setBreakpoints replaces ALL breakpoints
    // for a source, so we keep the desired set per file and assign our own stable
    // ids (engine ids change on each re-send). All access is serialized by _bpLock.
    readonly SemaphoreSlim _bpLock = new(1, 1);
    // Keys compare the way the file system compares paths, so two spellings of one file are
    // one entry. netcoredbg itself keys sources by the exact spelling it is sent (verified on
    // Windows: clearing a file under another casing leaves its breakpoints installed), so the
    // engine must always be given the FIRST spelling seen (_bpPaths), or a re-send under a
    // different casing would stack a second, independent set on top of the first.
    static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    readonly Dictionary<string, List<(int id, BreakpointRequest req)>> _bpsByFile = new(PathComparer);
    readonly Dictionary<string, string> _bpPaths = new(PathComparer);
    readonly Dictionary<int, (bool verified, int line, string file)> _bpResult = new();
    // Function breakpoints are global (DAP setFunctionBreakpoints replaces the whole
    // set), kept in their own list but sharing _bpCounter so ids are unique across
    // both kinds and _bpLock so all breakpoint mutations are serialized together.
    readonly List<(int id, FunctionBreakpointRequest req)> _fnBps = new();
    readonly Dictionary<int, FunctionBreakpoint> _fnResult = new();
    int _bpCounter;

    // Debuggee output is kept as a bounded tail (oldest dropped) so a chatty or runaway
    // process cannot grow the server without limit; get_output reads it.
    const int OutputCapacityChars = 64 * 1024;
    readonly BoundedTextTail _output = new(OutputCapacityChars);
    int? _exitCode;
    string? _faultMessage;

    public string Id { get; }
    public IDebugEngine Engine { get; private set; }

    /// <summary>The request used to launch this session, or null if it was created
    /// by attaching to an existing process. Only launched sessions can be restarted.</summary>
    public LaunchRequest? LaunchInfo { get; internal set; }

    /// <summary>True when this server owns the debuggee's lifetime: it launched it
    /// (debug_launch) or started the process that hosts it (debug_test). An attached
    /// session leaves the process it merely attached to running when it ends.</summary>
    public bool OwnsDebuggee => LaunchInfo is not null || OwnedResource is not null || OwnedByParent;

    /// <summary>True for a process auto-attached as a child of a session whose debuggee
    /// this server owns: it belongs to a process tree the server started, so it ends with
    /// its session. Propagates to grandchildren through <see cref="OwnsDebuggee"/>.</summary>
    internal bool OwnedByParent { get; set; }

    /// <summary>An external resource whose lifetime is tied to this session and is
    /// disposed when the session ends (e.g. the <c>dotnet test</c> driver process
    /// behind a test-debug session). Optional; null for ordinary sessions.</summary>
    public IAsyncDisposable? OwnedResource { get; set; }

    /// <summary>Bumped on every stop; invalidates per-stop handles from prior stops.</summary>
    public long StopId { get; private set; }
    public StopLocation? LastStop { get; private set; }

    /// <summary>Raised for a stop, exit or lost debugger connection that no resume
    /// operation was waiting for. The <c>long</c> is <see cref="StopId"/> at that moment;
    /// <see cref="IsCurrent"/> uses it to tell whether the event is still true when it is
    /// read later.</summary>
    public event Action<Session, SessionStop, long>? SpontaneousStop;

    /// <summary>Raised with the process id when a process this session debugged is gone:
    /// it exited, the debugger connection was lost, a restart replaced it, or the session
    /// was disposed (an attached process may still be alive, but is no longer debugged).
    /// Lets owners of per-process caches drop them before the pid is reused.</summary>
    public event Action<int>? DebuggeeEnded;

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
        engine.Faulted += OnFaulted;
        engine.Output += OnOutput;
    }

    void Unsubscribe(IDebugEngine engine)
    {
        engine.Stopped -= OnStopped;
        engine.Exited -= OnExited;
        engine.Faulted -= OnFaulted;
        engine.Output -= OnOutput;
    }

    public SessionState State => Engine.State;
    public int Pid => Engine.Pid;
    /// <summary>Exit code of the debuggee once it has exited and the debugger reported it.</summary>
    public int? ExitCode { get { lock (_gate) return _exitCode; } }
    /// <summary>Why the debugger connection was lost, once the engine reported it.</summary>
    public string? FaultMessage { get { lock (_gate) return _faultMessage; } }
    public SessionInfo ToInfo() => new(Id, Pid, State, LastStop, ExitCode);

    /// <summary>The last <paramref name="maxChars"/> characters of the debuggee's captured output.</summary>
    public SessionOutput GetOutput(int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        // Read the flag first: output arriving in between can only make it more true.
        bool dropped = _output.Dropped;
        string text = _output.GetTail(maxChars);
        return new SessionOutput(text, dropped || _output.Length > text.Length, State, ExitCode);
    }

    void OnStopped(StopLocation loc)
    {
        long seq;
        TaskCompletionSource<StopOutcome>? waiter;
        lock (_gate) { seq = ++StopId; LastStop = loc; waiter = TakeWaiterLocked(); }
        Deliver(waiter, new StopOutcome(SessionState.Stopped, loc), new SessionStop(Id, loc), seq);
    }

    void OnOutput(DebugOutput o)
    {
        // The debugger's own chatter (module loads, etc.) is not the debuggee's output.
        if (o.Category is "stdout" or "stderr") _output.Append(o.Text);
    }

    void OnExited(int? code)
    {
        long seq;
        TaskCompletionSource<StopOutcome>? waiter;
        lock (_gate) { _exitCode = code; seq = StopId; waiter = TakeWaiterLocked(); }
        Deliver(waiter, new StopOutcome(SessionState.Exited, null, code),
            new SessionStop(Id, null, SessionState.Exited, code), seq);
        RaiseDebuggeeEnded(Pid);
    }

    void OnFaulted(string reason)
    {
        long seq;
        TaskCompletionSource<StopOutcome>? waiter;
        lock (_gate) { _faultMessage = reason; seq = StopId; waiter = TakeWaiterLocked(); }
        string lost = LostMessage(reason);
        Deliver(waiter, new StopOutcome(SessionState.Faulted, null, Message: lost),
            new SessionStop(Id, null, SessionState.Faulted, Message: lost), seq);
        RaiseDebuggeeEnded(Pid);
    }

    // A resume operation waiting for this event consumes it; otherwise it is spontaneous.
    void Deliver(TaskCompletionSource<StopOutcome>? waiter, StopOutcome outcome, SessionStop spontaneous, long seq)
    {
        if (waiter is not null) waiter.TrySetResult(outcome);
        else SpontaneousStop?.Invoke(this, spontaneous, seq);
    }

    void RaiseDebuggeeEnded(int pid)
    {
        if (pid > 0) DebuggeeEnded?.Invoke(pid);
    }

    static string LostMessage(string reason) =>
        $"The debugger connection was lost ({reason}); the target's state is unknown. End the session with debug_stop and start a new one.";

    /// <summary>Whether <paramref name="queued"/>, raised through <see cref="SpontaneousStop"/>
    /// when the stop sequence was <paramref name="stopId"/>, still describes this session: a
    /// stop only while the session is still stopped at that same stop (not resumed, not
    /// stopped again); an exit or a lost connection only while the session is still in that
    /// state of the same run (a restart starts a new run).</summary>
    public bool IsCurrent(SessionStop queued, long stopId)
    {
        lock (_gate) { if (StopId != stopId) return false; }
        return queued.State == State;
    }

    TaskCompletionSource<StopOutcome>? TakeWaiterLocked()
    {
        var w = _targetedWaiter;
        _targetedWaiter = null;
        return w;
    }

    // Disarm our waiter if it is still the armed one, so the next real event is not swallowed.
    void DisarmWaiter(TaskCompletionSource<StopOutcome> mine)
    {
        lock (_gate) { if (ReferenceEquals(_targetedWaiter, mine)) _targetedWaiter = null; }
    }

    /// <summary>Send a resume command and wait for the next stop, the process exiting, the
    /// debugger connection being lost, or the timeout. On timeout the target is left running.
    /// If the command is rejected, or <paramref name="ct"/> is cancelled, the waiter is disarmed
    /// so the next real stop is reported as spontaneous instead of being swallowed; cancellation
    /// surfaces as <see cref="OperationCanceledException"/>, never as a timeout. Only one resume
    /// may be in flight: a second one is rejected, because queuing it would silently resume the
    /// target again at the first one's stop.</summary>
    public async Task<StopOutcome> ResumeAndWaitAsync(Func<Task> sendCommand, TimeSpan timeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<StopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var state = State;
            if (state == SessionState.Faulted) throw new InvalidOperationException(FaultedMessage());
            if (state == SessionState.Exited)
                throw new InvalidOperationException(
                    $"Session '{Id}' has exited{(_exitCode is int c ? $" with code {c}" : "")}; there is nothing to resume. Use restart_debugging or debug_launch.");
            if (_targetedWaiter is not null)
                throw new InvalidOperationException(
                    $"Session '{Id}' already has a continue/step in progress. Wait for it to return (or call pause) before resuming again.");
            _targetedWaiter = tcs;
        }

        try { await sendCommand(); }
        catch { DisarmWaiter(tcs); throw; }

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(timeout);
        try
        {
            await tcs.Task.WaitAsync(window.Token);
        }
        catch (OperationCanceledException)
        {
            DisarmWaiter(tcs);
            // An event that won the race against the timeout or cancellation is not lost.
            if (!tcs.Task.IsCompletedSuccessfully)
            {
                ct.ThrowIfCancellationRequested();
                return new StopOutcome(State, null); // timed out: still running
            }
        }

        var outcome = tcs.Task.Result;
        return outcome.State == SessionState.Stopped
            ? outcome with { Stop = await EnrichAsync(outcome.Stop!, ct) }
            : outcome;
    }

    string FaultedMessage() =>
        $"Session '{Id}' lost its debugger connection ({FaultMessage ?? "the debugger ended"}); the target's state is unknown. End it with debug_stop and start a new session.";

    /// <summary>Fill file/line on a stop by reading the top stack frame.</summary>
    public async Task<StopLocation> EnrichAsync(StopLocation loc, CancellationToken ct = default)
    {
        if (loc.ThreadId is not int tid) return loc;
        long seq;
        lock (_gate) seq = StopId;
        try
        {
            var frames = await Engine.GetStackTraceAsync(tid, 0, 1, ct);
            if (frames.Count > 0)
            {
                var enriched = loc with { File = frames[0].File, Line = frames[0].Line };
                // Only the stop that was enriched may be recorded: the target may have moved on.
                lock (_gate) { if (StopId == seq) LastStop = enriched; }
                return enriched;
            }
        }
        catch { /* best effort */ }
        return loc;
    }

    public void EnsureStopped()
    {
        var state = State;
        if (state == SessionState.Stopped) return;
        throw new InvalidOperationException(state == SessionState.Faulted
            ? FaultedMessage()
            : $"Session '{Id}' is '{state}', not stopped. This operation requires a stopped target.");
    }

    // --- Breakpoints (allowed while running) ---

    /// <summary>Set a source breakpoint from what a caller can actually supply: a 1-based line,
    /// or the text of the target line. With <paramref name="content"/> the line is resolved by
    /// matching that text in the file (<see cref="BreakpointLocator"/>) and
    /// <c>req.Line</c> is only a hint (0 = none) that picks between several matches.</summary>
    /// <exception cref="ArgumentException">Neither a valid line nor content was given.</exception>
    public async Task<Breakpoint> AddSourceBreakpointAsync(string file, BreakpointRequest req, string? content, CancellationToken ct = default)
    {
        if (content is not null)
            req = req with { Line = await BreakpointLocator.ResolveInFileAsync(file, content, req.Line > 0 ? req.Line : null, ct) };
        else if (req.Line < 1)
            throw new ArgumentException("Provide either a 1-based 'line' or the 'content' of the line to match.");
        return await AddBreakpointAsync(file, req, ct);
    }

    public async Task<Breakpoint> AddBreakpointAsync(string file, BreakpointRequest req, CancellationToken ct = default)
    {
        string fullPath = Path.GetFullPath(file);
        await _bpLock.WaitAsync(ct);
        try
        {
            if (!_bpPaths.TryGetValue(fullPath, out var path)) _bpPaths[fullPath] = path = fullPath;
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
    public Task<StopOutcome> StepAsync(StepKind kind, int? threadId, TimeSpan timeout, CancellationToken ct = default)
    {
        int tid = ResolveThread(threadId);
        return ResumeAndWaitAsync(() => Engine.StepAsync(kind, tid, ct), timeout, ct);
    }
    public Task PauseAsync(int? threadId, CancellationToken ct = default)
        => Engine.PauseAsync(threadId, ct);

    // --- Introspection (target must be stopped) ---
    public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct = default)
    { EnsureStopped(); return Engine.GetThreadsAsync(ct); }
    public Task<IReadOnlyList<StackFrame>> GetStackTraceAsync(int? threadId, int startFrame, int levels, CancellationToken ct = default)
    { EnsureStopped(); return Engine.GetStackTraceAsync(ResolveThread(threadId), startFrame, levels, ct); }
    public Task<IReadOnlyList<Scope>> GetScopesAsync(int frameId, CancellationToken ct = default)
    { EnsureStopped(); return Engine.GetScopesAsync(frameId, ct); }

    /// <summary>One window of the children of a variablesReference. The engine is asked for the
    /// window itself (start/count) plus one lookahead item, never for everything, so a huge
    /// collection costs one bounded request; the lookahead is what tells whether more exist.</summary>
    public async Task<Page<Variable>> GetVariablesAsync(int variablesReference, int offset, int max, CancellationToken ct = default)
    {
        Paging.Validate(offset, max);
        EnsureStopped();
        var fetched = await Engine.GetVariablesAsync(variablesReference, offset, max + 1, ct);
        return Page<Variable>.FromLookahead(fetched, offset, max);
    }
    public Task<EvaluateResult> EvaluateAsync(string expression, int? frameId, string? context, CancellationToken ct = default)
    { EnsureStopped(); return Engine.EvaluateAsync(expression, frameId, context, ct); }
    public Task<ExceptionInfo?> GetExceptionInfoAsync(int? threadId, CancellationToken ct = default)
    { EnsureStopped(); return Engine.GetExceptionInfoAsync(ResolveThread(threadId), ct); }

    /// <summary>The thread an operation applies to: the one given, else the thread of the last stop.</summary>
    /// <exception cref="InvalidOperationException">No thread was given and the session has not stopped yet.</exception>
    public int ResolveThread(int? threadId)
        => threadId ?? LastStop?.ThreadId
            ?? throw new InvalidOperationException($"Session '{Id}' has no current thread (it has not stopped yet); pass threadId.");

    /// <summary>Restart the debuggee in place, keeping the same session id and
    /// breakpoint set. Terminates the current engine, swaps in <paramref name="newEngine"/>
    /// (a DAP engine is single-use — the netcoredbg handshake cannot be replayed),
    /// relaunches with <paramref name="req"/>, then re-applies all breakpoints.</summary>
    /// <remarks>If the relaunch fails, the new engine is torn down and the session is left
    /// Exited — no process, but its id, launch request and breakpoints kept — so the
    /// caller can fix the cause and restart again, or discard it with debug_stop. The
    /// exception says so.</remarks>
    public async Task RestartAsync(LaunchRequest req, IDebugEngine newEngine, CancellationToken ct = default)
    {
        var old = Engine;
        int oldPid = old.Pid;
        // Ending the old run on purpose is not an event anyone should hear: stop listening first.
        Unsubscribe(old);
        // Restart only exists for launched sessions, whose debuggee this server owns.
        try { await old.TerminateAsync(terminateDebuggee: true, ct); } catch { /* may already be gone */ }
        try { await old.DisposeAsync(); } catch { /* best effort */ }
        RaiseDebuggeeEnded(oldPid);

        Engine = newEngine;
        Subscribe(newEngine);

        // Invalidate any per-stop handles and abandon a pending waiter from the old run.
        lock (_gate)
        {
            StopId++; LastStop = null; _exitCode = null; _faultMessage = null;
            TakeWaiterLocked()?.TrySetResult(new StopOutcome(SessionState.Exited, null));
        }
        _output.Clear();

        try
        {
            await newEngine.LaunchAsync(req, ct);
            LaunchInfo = req;
            await ReapplyAllBreakpointsAsync(ct);
        }
        catch (Exception ex)
        {
            Unsubscribe(newEngine);
            int newPid = newEngine.Pid;
            try { await newEngine.TerminateAsync(terminateDebuggee: true, CancellationToken.None); } catch { /* best effort */ }
            try { await newEngine.DisposeAsync(); } catch { /* best effort */ }
            RaiseDebuggeeEnded(newPid);
            if (ex is OperationCanceledException) throw;
            throw new InvalidOperationException(
                $"Restart of session '{Id}' failed: {ex.Message} The previous process was terminated and the new one did not start, " +
                "so the session is now Exited; it keeps its breakpoints. Fix the cause and call restart_debugging again, or end it with debug_stop.", ex);
        }
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
        int pid = Pid;
        try { await Engine.DisposeAsync(); }
        finally { RaiseDebuggeeEnded(pid); }
    }
}
