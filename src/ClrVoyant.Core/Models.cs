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

/// <summary>Parameters to launch a debuggee. <paramref name="Env"/> is added to (and
/// overrides entries of) the environment the debuggee inherits from the server.</summary>
public sealed record LaunchRequest(
    string Program,
    string[]? Args = null,
    string? Cwd = null,
    bool StopAtEntry = false,
    IReadOnlyDictionary<string, string>? Env = null);

/// <summary>Public, serializable view of a session for tool responses.
/// <paramref name="ExitCode"/> is set once the debuggee has exited and the debugger
/// reported its exit code.</summary>
public sealed record SessionInfo(
    string SessionId,
    int Pid,
    SessionState State,
    StopLocation? Stop,
    int? ExitCode = null);

/// <summary>An event reported by a specific session through wait_for_any_stop: a stop
/// (<paramref name="State"/> Stopped, with <paramref name="Stop"/>), the process
/// exiting (Exited, with <paramref name="ExitCode"/> when known) or the debugger
/// connection being lost (Faulted, explained by <paramref name="Message"/>).</summary>
public sealed record SessionStop(
    string SessionId,
    StopLocation? Stop,
    SessionState State = SessionState.Stopped,
    int? ExitCode = null,
    string? Message = null);
