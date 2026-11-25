using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ClrVoyant.Core;

/// <summary>
/// Owns all concurrent debug sessions. Resolves the "active" session (for tools
/// called without an explicit sessionId), and fans stop events from every engine
/// into a single channel so <c>wait_for_any_stop</c> can report whichever process
/// breaks first.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    readonly Func<IDebugEngine> _engineFactory;
    readonly ConcurrentDictionary<string, Session> _sessions = new();
    readonly Channel<QueuedStop> _stops = Channel.CreateUnbounded<QueuedStop>();
    int _counter;
    volatile string? _activeId;

    public SessionManager(Func<IDebugEngine> engineFactory) => _engineFactory = engineFactory;

    /// <summary>An event waiting to be read by wait_for_any_stop, with the session's stop
    /// sequence when it happened (see <see cref="Session.IsCurrent"/>).</summary>
    sealed record QueuedStop(SessionStop Stop, long StopId);

    /// <summary>Raised with the process id when a debugged process is gone (see
    /// <see cref="Session.DebuggeeEnded"/>), for whichever session had it. Per-process
    /// caches in other layers hook this to drop their entry for the pid.</summary>
    public event Action<int>? DebuggeeEnded;

    public Task<SessionInfo> LaunchAsync(LaunchRequest req, CancellationToken ct = default)
    {
        ValidateEnv(req.Env);
        // A failed launch has already started a debuggee (or may have): end it.
        return StartAsync(s => s.LaunchInfo = req, s => s.Engine.LaunchAsync(req, ct), terminateOnFailure: true);
    }

    /// <summary>Restart a launched session in place (same id, same breakpoints).
    /// Manufactures a fresh engine for it. Attached sessions cannot be restarted.</summary>
    public async Task<SessionInfo> RestartAsync(string? sessionId, CancellationToken ct = default)
    {
        var s = Resolve(sessionId);
        var req = s.LaunchInfo
            ?? throw new InvalidOperationException(
                $"Session '{s.Id}' was attached, not launched, so it cannot be restarted. Use debug_attach instead.");
        await s.RestartAsync(req, _engineFactory(), ct);
        _activeId = s.Id;
        return s.ToInfo();
    }

    public Task<SessionInfo> AttachAsync(int pid, CancellationToken ct = default)
        => StartAsync(null, s => s.Engine.AttachAsync(pid, ct), terminateOnFailure: false);

    /// <summary>Attach to a process discovered as a child of session <paramref name="parentSessionId"/>
    /// (auto-attach). The child inherits the parent's ownership: if this server owns the
    /// parent's debuggee, the child is part of a process tree the server started and ends with
    /// its session; if the parent was merely attached, so is the child, and it is detached.</summary>
    public Task<SessionInfo> AttachChildAsync(int pid, string parentSessionId, CancellationToken ct = default)
    {
        bool owned = Resolve(parentSessionId).OwnsDebuggee;
        return StartAsync(s => s.OwnedByParent = owned, s => s.Engine.AttachAsync(pid, ct), terminateOnFailure: false);
    }

    // Creating a session is transactional: the session is registered (and active) while it
    // starts, so a failure — bad path, attach denied, timeout, cancellation — unregisters it,
    // restores the previously active session and ends its engine, leaving no ghost session and
    // no orphaned netcoredbg. A failed attach must never kill the process it tried to attach to.
    async Task<SessionInfo> StartAsync(Action<Session>? init, Func<Session, Task> start, bool terminateOnFailure)
    {
        string? previousActive = _activeId;
        var session = CreateSession();
        try
        {
            init?.Invoke(session);
            await start(session);
            return session.ToInfo();
        }
        catch
        {
            _sessions.TryRemove(session.Id, out _);
            if (_activeId == session.Id)
                _activeId = previousActive is not null && _sessions.ContainsKey(previousActive) ? previousActive : null;
            try { await EndAsync(session, terminateOnFailure, CancellationToken.None); } catch { /* the original failure is the one to report */ }
            throw;
        }
    }

    // Terminate (or detach) and dispose; the session is disposed even if the engine's end fails.
    static async Task EndAsync(Session s, bool terminateDebuggee, CancellationToken ct)
    {
        try { await s.Engine.TerminateAsync(terminateDebuggee, ct); }
        finally { await s.DisposeAsync(); }
    }

    Session CreateSession()
    {
        var id = $"s{Interlocked.Increment(ref _counter)}";
        var session = new Session(id, _engineFactory());
        // Only events that no resume operation was waiting for are fanned in here (a
        // breakpoint hit while idle, stopAtEntry, a process exiting unobserved). Events
        // consumed by continue/step never reach this. A session that is no longer
        // registered (stopped, or its start failed) has nothing to report.
        session.SpontaneousStop += (s, stop, stopId) =>
        {
            if (!_sessions.ContainsKey(s.Id)) return;
            if (stop.State == SessionState.Stopped) _activeId = s.Id;
            _stops.Writer.TryWrite(new QueuedStop(stop, stopId));
        };
        session.DebuggeeEnded += pid => DebuggeeEnded?.Invoke(pid);
        _sessions[id] = session;
        _activeId = id;
        return session;
    }

    public Session Resolve(string? sessionId)
    {
        var id = sessionId ?? _activeId;
        if (id is null)
        {
            var ids = _sessions.Keys.Order(StringComparer.Ordinal).ToList();
            throw new InvalidOperationException(ids.Count == 0
                ? "No active debug session."
                : $"No active debug session; {ids.Count} sessions exist ({string.Join(", ", ids)}): pass sessionId.");
        }
        if (!_sessions.TryGetValue(id, out var s))
            throw new KeyNotFoundException($"Unknown sessionId '{id}'.");
        return s;
    }

    public IReadOnlyList<SessionInfo> List()
        => _sessions.Values.Select(s => s.ToInfo()).ToList();

    // An environment variable name must be non-empty and contain no '=', or the debugger
    // would split it into a different name/value than the caller meant.
    static void ValidateEnv(IReadOnlyDictionary<string, string>? env)
    {
        if (env is null) return;
        foreach (var name in env.Keys)
            if (name.Length == 0 || name.Contains('='))
                throw new ArgumentException($"Invalid environment variable name '{name}': it must be non-empty and must not contain '='.");
    }

    /// <summary>End a session. By default the debuggee is killed only if this server
    /// owns it (<see cref="Session.OwnsDebuggee"/>: launched, or the host behind
    /// debug_test, or an auto-attached child of such a session); a process the session
    /// merely attached to is detached and left running. <paramref name="terminate"/>
    /// overrides that either way. The session is removed first and always disposed, even
    /// if ending the engine fails. If the active session ends, the only remaining one
    /// becomes active; with several left none is, so tools must be told which.</summary>
    public async Task StopAsync(string? sessionId, bool? terminate = null, CancellationToken ct = default)
    {
        var s = Resolve(sessionId);
        if (!_sessions.TryRemove(s.Id, out _))
            throw new KeyNotFoundException($"Session '{s.Id}' has already ended.");
        if (_activeId == s.Id)
        {
            var remaining = _sessions.Keys.ToList();
            _activeId = remaining.Count == 1 ? remaining[0] : null;
        }
        await EndAsync(s, terminate ?? s.OwnsDebuggee, ct);
    }

    /// <summary>Wait for the next stop, exit or lost connection of any session, or return
    /// null on timeout. Events that no longer describe their session (it resumed, stopped
    /// again, restarted or was removed since) are skipped. Cancelling <paramref name="ct"/>
    /// throws <see cref="OperationCanceledException"/>; it is not a timeout.</summary>
    public async Task<SessionStop?> WaitForAnyStopAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var queued = await _stops.Reader.ReadAsync(window.Token);
                var stop = queued.Stop;
                if (!_sessions.TryGetValue(stop.SessionId, out var s) || !s.IsCurrent(stop, queued.StopId))
                    continue;
                // Enrich with file/line (the raw stop event carries only reason+thread).
                return stop.State == SessionState.Stopped
                    ? stop with { Stop = await s.EnrichAsync(stop.Stop!, ct) }
                    : stop;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values)
        {
            try { await s.DisposeAsync(); } catch { /* best effort on shutdown */ }
        }
        _sessions.Clear();
    }
}
