using ClrVoyant.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClrVoyant.Server;

/// <summary>
/// Phase 7, tier 2: when auto-attach is enabled, periodically discovers .NET
/// processes spawned by an already-debugged process (matched by parent PID) and
/// attaches them as new sessions. No suspend-at-startup, so the very first
/// instants of a child's startup may run before we attach (documented tradeoff).
/// Newly attached children become tracked parents themselves, so grandchildren
/// are caught on subsequent passes.
/// </summary>
public sealed class ChildProcessWatcher : BackgroundService
{
    readonly SessionManager _sessions;
    readonly AutoAttachOptions _options;
    readonly ILogger<ChildProcessWatcher>? _log;
    // Poll the process list ~every 750ms: responsive enough to catch short-lived
    // children without busy-spinning on process enumeration.
    readonly TimeSpan _interval = TimeSpan.FromMilliseconds(750);

    public ChildProcessWatcher(SessionManager sessions, AutoAttachOptions options, ILogger<ChildProcessWatcher>? log = null)
    {
        _sessions = sessions;
        _options = options;
        _log = log;
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
    /// Returns the count of newly attached sessions. No-op unless enabled.</summary>
    public async Task<int> ScanOnceAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled) return 0;

        var tracked = _sessions.List().Select(s => s.Pid).Where(p => p > 0).ToHashSet();
        if (tracked.Count == 0) return 0;

        int attached = 0;
        foreach (var (pid, parentPid) in ProcessLister.Enumerate())
        {
            if (ct.IsCancellationRequested) break;
            if (!tracked.Contains(parentPid)) continue;  // not a child of a debugged process
            if (tracked.Contains(pid)) continue;         // already a session
            if (!ProcessLister.IsDotNet(pid)) continue;

            try { await _sessions.AttachAsync(pid, ct); attached++; }
            catch (Exception ex) { _log?.LogDebug(ex, "auto-attach to {Pid} failed", pid); }
        }
        return attached;
    }
}
