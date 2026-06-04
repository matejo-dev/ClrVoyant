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
    event Action<int>? Exited;

    // --- Lifecycle ---
    Task LaunchAsync(LaunchRequest request, CancellationToken ct = default);
    Task AttachAsync(int pid, CancellationToken ct = default);
    Task TerminateAsync(CancellationToken ct = default);

    // --- Breakpoints ---
    Task<IReadOnlyList<Breakpoint>> SetBreakpointsAsync(string file, IReadOnlyList<BreakpointRequest> breakpoints, CancellationToken ct = default);
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
