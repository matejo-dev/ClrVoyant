namespace ClrVoyant.Core;

/// <summary>Lifecycle state of a single debugged process.</summary>
public enum SessionState
{
    Starting,
    Running,
    Stopped,
    Exited,
    Faulted,
}

/// <summary>Where execution is currently stopped (breakpoint, step, exception).</summary>
public sealed record StopLocation(
    string? File,
    int? Line,
    string? Reason,
    int? ThreadId);

/// <summary>Parameters to launch a debuggee.</summary>
public sealed record LaunchRequest(
    string Program,
    string[]? Args = null,
    string? Cwd = null,
    bool StopAtEntry = false);

/// <summary>Public, serializable view of a session for tool responses.</summary>
public sealed record SessionInfo(
    string SessionId,
    int Pid,
    SessionState State,
    StopLocation? Stop);

/// <summary>A stop reported by a specific session (used by wait_for_any_stop).</summary>
public sealed record SessionStop(
    string SessionId,
    StopLocation Stop);
