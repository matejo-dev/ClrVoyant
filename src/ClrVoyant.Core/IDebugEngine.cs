namespace ClrVoyant.Core;

/// <summary>
/// Abstraction over a debug backend for a single target process. The concrete
/// implementation (DapEngine over netcoredbg) lives in ClrVoyant.Dap; keeping the
/// contract here makes the engine swappable and the session logic testable.
///
/// Execution-control methods (Continue/Step/Pause) only SEND the command; the
/// "wait until the next stop" coordination is owned by <see cref="Session"/>,
/// which observes <see cref="Stopped"/>/<see cref="Exited"/>. Introspection
/// methods require the target to be stopped.
/// </summary>
public interface IDebugEngine : IAsyncDisposable
{
    int Pid { get; }
    SessionState State { get; }

    event Action<StopLocation>? Stopped;
    /// <summary>Raised exactly once when the debuggee ends (it exited, or
    /// <see cref="TerminateAsync"/> ended it), with its exit code when the debugger
    /// reported one.</summary>
    event Action<int?>? Exited;
    /// <summary>Raised at most once when the debugger itself is lost (the adapter died or
    /// its connection closed) before the debuggee's end was reported; the argument says
    /// why. <see cref="State"/> is Faulted from then on. Not raised for an end this
    /// engine caused through <see cref="TerminateAsync"/> or disposal.</summary>
    event Action<string>? Faulted;
    /// <summary>Debuggee (and debugger) output as it arrives.</summary>
    event Action<DebugOutput>? Output;

    // --- Lifecycle ---
    Task LaunchAsync(LaunchRequest request, CancellationToken ct = default);
    Task AttachAsync(int pid, CancellationToken ct = default);
    /// <summary>End the session. With <paramref name="terminateDebuggee"/> the debuggee
    /// is killed; otherwise the debugger detaches and leaves it running. Bounded: it
    /// returns promptly even if the debugger is dead or unresponsive.</summary>
    Task TerminateAsync(bool terminateDebuggee, CancellationToken ct = default);

    // --- Breakpoints ---
    Task<IReadOnlyList<Breakpoint>> SetBreakpointsAsync(string file, IReadOnlyList<BreakpointRequest> breakpoints, CancellationToken ct = default);
    Task<IReadOnlyList<FunctionBreakpoint>> SetFunctionBreakpointsAsync(IReadOnlyList<FunctionBreakpointRequest> breakpoints, CancellationToken ct = default);
    Task SetExceptionBreakpointsAsync(IReadOnlyList<string> filters, CancellationToken ct = default);

    // --- Execution control (send only) ---
    Task ContinueAsync(int? threadId, CancellationToken ct = default);
    Task StepAsync(StepKind kind, int threadId, CancellationToken ct = default);
    Task PauseAsync(int? threadId, CancellationToken ct = default);

    // --- Introspection (target must be stopped) ---
    Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<StackFrame>> GetStackTraceAsync(int threadId, int startFrame, int levels, CancellationToken ct = default);
    Task<IReadOnlyList<Scope>> GetScopesAsync(int frameId, CancellationToken ct = default);
    Task<IReadOnlyList<Variable>> GetVariablesAsync(int variablesReference, CancellationToken ct = default);
    Task<EvaluateResult> EvaluateAsync(string expression, int? frameId, string? context, CancellationToken ct = default);
    Task<ExceptionInfo?> GetExceptionInfoAsync(int threadId, CancellationToken ct = default);
}
