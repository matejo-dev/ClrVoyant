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
    readonly Channel<SessionStop> _stops = Channel.CreateUnbounded<SessionStop>();
    int _counter;
    volatile string? _activeId;

    public SessionManager(Func<IDebugEngine> engineFactory) => _engineFactory = engineFactory;

    public async Task<SessionInfo> LaunchAsync(LaunchRequest req, CancellationToken ct = default)
    {
        var session = CreateSession();
        session.LaunchInfo = req; // remember it so the session can be restarted
        await session.Engine.LaunchAsync(req, ct);
        return session.ToInfo();
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

    public async Task<SessionInfo> AttachAsync(int pid, CancellationToken ct = default)
    {
        var session = CreateSession();
        await session.Engine.AttachAsync(pid, ct);
        return session.ToInfo();
    }

    Session CreateSession()
    {
        var id = $"s{Interlocked.Increment(ref _counter)}";
        var session = new Session(id, _engineFactory());
        // Only stops that no resume operation was waiting for are fanned in here
        // (e.g. a breakpoint hit while idle, or stopAtEntry). Targeted stops from
        // continue/step are consumed by ResumeAndWaitAsync.
        session.SpontaneousStop += (s, loc) =>
        {
            _activeId = s.Id;
            _stops.Writer.TryWrite(new SessionStop(s.Id, loc));
        };
        _sessions[id] = session;
        _activeId = id;
        return session;
    }

    public Session Resolve(string? sessionId)
    {
        var id = sessionId ?? _activeId
            ?? throw new InvalidOperationException("No active debug session.");
        if (!_sessions.TryGetValue(id, out var s))
            throw new KeyNotFoundException($"Unknown sessionId '{id}'.");
        return s;
    }

    public IReadOnlyList<SessionInfo> List()
        => _sessions.Values.Select(s => s.ToInfo()).ToList();

    public async Task StopAsync(string? sessionId, CancellationToken ct = default)
    {
        var s = Resolve(sessionId);
        _sessions.TryRemove(s.Id, out _);
        if (_activeId == s.Id) _activeId = _sessions.Keys.FirstOrDefault();
        try { await s.Engine.TerminateAsync(ct); }
        finally { await s.DisposeAsync(); }
    }

    public async Task<SessionStop?> WaitForAnyStopAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        SessionStop stop;
        try { stop = await _stops.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) { return null; }

        // Enrich with file/line (the raw stop event carries only reason+thread).
        if (_sessions.TryGetValue(stop.SessionId, out var s))
            stop = stop with { Stop = await s.EnrichAsync(stop.Stop, ct) };
        return stop;
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
