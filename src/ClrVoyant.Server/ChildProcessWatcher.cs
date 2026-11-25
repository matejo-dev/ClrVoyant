using ClrVoyant.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClrVoyant.Server;

/// <summary>
/// Child-process auto-attach: when it is enabled (<see cref="AutoAttachOptions"/>), periodically discovers .NET
/// processes spawned by an already-debugged process (matched by parent PID) and
/// attaches them as new sessions. No suspend-at-startup, so the very first
/// instants of a child's startup may run before we attach (documented tradeoff).
/// Newly attached children become tracked parents themselves, so grandchildren
/// are caught on subsequent passes.
///
/// A matching parent PID alone is NOT proof of parentage: an OS keeps recording the
/// creator's PID in a process even after the creator is gone, and PIDs are recycled, so
/// when a debuggee receives a recycled PID, every still-running process of the PID's
/// previous owner would look like its child. A child is therefore also required to have
/// been created no earlier than its parent. This matters beyond a wrong session: a child
/// of a launched session inherits ownership and is killed when that session stops.
/// </summary>
public sealed class ChildProcessWatcher : BackgroundService
{
    /// <summary>How many times an attach to one process is attempted before it is left alone.</summary>
    internal const int MaxAttachAttempts = 3;

    readonly SessionManager _sessions;
    readonly AutoAttachOptions _options;
    readonly ILogger<ChildProcessWatcher>? _log;
    // The process list and the .NET check are injectable so the matching rules can be
    // tested without real processes; production uses ProcessLister.
    readonly Func<IEnumerable<ProcessEntry>> _enumerate;
    readonly Func<int, bool> _isDotNet;
    // Failed attach attempts per process identity (pid + creation stamp: the pid alone may be
    // recycled). A process that cannot be attached (another debugger owns it, access denied)
    // would otherwise cost a netcoredbg spawn, possibly a full ready-timeout, on every scan.
    readonly Dictionary<(int pid, long start), int> _failures = new();
    // Poll the process list ~every 750ms: responsive enough to catch short-lived
    // children without busy-spinning on process enumeration.
    readonly TimeSpan _interval = TimeSpan.FromMilliseconds(750);

    public ChildProcessWatcher(SessionManager sessions, AutoAttachOptions options, ILogger<ChildProcessWatcher>? log = null)
        : this(sessions, options, log, null, null) { }

    internal ChildProcessWatcher(SessionManager sessions, AutoAttachOptions options, ILogger<ChildProcessWatcher>? log,
        Func<IEnumerable<ProcessEntry>>? enumerate, Func<int, bool>? isDotNet)
    {
        _sessions = sessions;
        _options = options;
        _log = log;
        _enumerate = enumerate ?? (() => ProcessLister.Enumerate());
        _isDotNet = isDotNet ?? ProcessLister.IsDotNet;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ScanOnceAsync(stoppingToken); }
            catch (Exception ex) { _log?.LogDebug(ex, "child-process scan failed"); }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One discovery pass: attach .NET children of debugged processes.
    /// Returns the count of newly attached sessions. No-op unless enabled. A child
    /// whose attach fails is tried again on the next passes, up to
    /// <see cref="MaxAttachAttempts"/> times.</summary>
    public async Task<int> ScanOnceAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled) return 0;

        // pid -> the session debugging it: a child is attached on behalf of its parent's session.
        // A session whose process is gone is not a parent: its pid may already belong to
        // someone else.
        var tracked = new Dictionary<int, string>();
        foreach (var s in _sessions.List())
            if (s.Pid > 0 && s.State is not (SessionState.Exited or SessionState.Faulted)) tracked[s.Pid] = s.SessionId;
        if (tracked.Count == 0) return 0;

        var table = _enumerate().ToList();
        var startOf = new Dictionary<int, long>();
        foreach (var p in table) startOf[p.Pid] = p.StartStamp;
        // Identities that are gone must not keep a failure budget (and a recycled pid must not inherit it).
        _failures.Keys.Where(k => !startOf.TryGetValue(k.pid, out long st) || st != k.start).ToList()
            .ForEach(k => _failures.Remove(k));

        int attached = 0;
        foreach (var child in table)
        {
            if (ct.IsCancellationRequested) break;
            if (!tracked.TryGetValue(child.ParentPid, out var parentSessionId)) continue; // not a child of a debugged process
            if (tracked.ContainsKey(child.Pid)) continue;                                  // already a session
            if (!IsGenuineChild(child, startOf.GetValueOrDefault(child.ParentPid, 0))) continue; // stale parent PID (see class summary)
            var identity = (child.Pid, child.StartStamp);
            if (_failures.GetValueOrDefault(identity) >= MaxAttachAttempts) continue;
            if (!_isDotNet(child.Pid)) continue;

            try { await _sessions.AttachChildAsync(child.Pid, parentSessionId, ct); attached++; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _failures[identity] = _failures.GetValueOrDefault(identity) + 1;
                _log?.LogDebug(ex, "auto-attach to {Pid} failed", child.Pid);
            }
        }
        return attached;
    }

    // The child was created no earlier than its parent. With either creation time unknown (0,
    // or the parent missing from the table) the parentage cannot be shown, so the process is
    // left alone: adopting a stranger costs more than missing a child (debug_attach still covers it).
    static bool IsGenuineChild(ProcessEntry child, long parentStart)
        => parentStart > 0 && child.StartStamp >= parentStart;
}
