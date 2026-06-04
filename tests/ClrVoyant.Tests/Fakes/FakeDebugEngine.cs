using ClrVoyant.Core;

namespace ClrVoyant.Tests.Fakes;

/// <summary>
/// In-memory IDebugEngine test double. Lets tests drive stop/exit events and
/// records control commands, so Session/SessionManager logic can be verified
/// deterministically without netcoredbg.
/// </summary>
public sealed class FakeDebugEngine : IDebugEngine
{
    public int Pid { get; set; } = 1234;
    public SessionState State { get; set; } = SessionState.Starting;

    public event Action<StopLocation>? Stopped;
    public event Action<int>? Exited;

    // Recorded calls.
    public int ContinueCalls { get; private set; }
    public List<StepKind> Steps { get; } = new();
    public int PauseCalls { get; private set; }
    public int TerminateCalls { get; private set; }
    public List<string> ExceptionFilters { get; private set; } = new();
    public List<(string file, IReadOnlyList<BreakpointRequest> bps)> SetBreakpointCalls { get; } = new();

    // What ContinueAsync/StepAsync should do (default: stop at this location).
    public StopLocation? StopOnResume { get; set; } = new("F.cs", 10, "breakpoint", 1);
    public bool ExitOnResume { get; set; }

    // Canned stack frame used by Session.EnrichAsync.
    public StackFrame TopFrame { get; set; } = new(0, "Foo", "Enriched.cs", 42);

    public void RaiseStopped(StopLocation loc) { State = SessionState.Stopped; Stopped?.Invoke(loc); }
    public void RaiseExited(int code) { State = SessionState.Exited; Exited?.Invoke(code); }

    public Task LaunchAsync(LaunchRequest request, CancellationToken ct = default)
    {
        State = SessionState.Running;
        return Task.CompletedTask;
    }

    public Task AttachAsync(int pid, CancellationToken ct = default)
    {
        Pid = pid;
        State = SessionState.Running;
        return Task.CompletedTask;
    }

    public Task TerminateAsync(CancellationToken ct = default)
    {
        TerminateCalls++;
        State = SessionState.Exited;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Breakpoint>> SetBreakpointsAsync(string file, IReadOnlyList<BreakpointRequest> breakpoints, CancellationToken ct = default)
    {
        SetBreakpointCalls.Add((file, breakpoints));
        IReadOnlyList<Breakpoint> result = breakpoints
            .Select((b, i) => new Breakpoint(i + 1, true, file, b.Line))
            .ToList();
        return Task.FromResult(result);
    }

    public Task SetExceptionBreakpointsAsync(IReadOnlyList<string> filters, CancellationToken ct = default)
    {
        ExceptionFilters = filters.ToList();
        return Task.CompletedTask;
    }

    public Task ContinueAsync(int? threadId, CancellationToken ct = default)
    {
        ContinueCalls++;
        Resume();
        return Task.CompletedTask;
    }

    public Task StepAsync(StepKind kind, int threadId, CancellationToken ct = default)
    {
        Steps.Add(kind);
        Resume();
        return Task.CompletedTask;
    }

    public Task PauseAsync(int? threadId, CancellationToken ct = default)
    {
        PauseCalls++;
        return Task.CompletedTask;
    }

    // Simulate the engine resuming and then either stopping again or exiting.
    void Resume()
    {
        State = SessionState.Running;
        if (ExitOnResume) RaiseExited(0);
        else if (StopOnResume is not null) RaiseStopped(StopOnResume);
        // else: stays running (lets tests exercise the timeout path)
    }

    public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ThreadInfo>>(new[] { new ThreadInfo(1, "main") });

    public Task<IReadOnlyList<StackFrame>> GetStackTraceAsync(int threadId, int startFrame, int levels, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StackFrame>>(new[] { TopFrame });

    public Task<IReadOnlyList<Scope>> GetScopesAsync(int frameId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Scope>>(new[] { new Scope("Locals", 100) });

    public Task<IReadOnlyList<Variable>> GetVariablesAsync(int variablesReference, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Variable>>(new[] { new Variable("x", "1", "int", 0) });

    public Task<EvaluateResult> EvaluateAsync(string expression, int? frameId, string? context, CancellationToken ct = default)
        => Task.FromResult(new EvaluateResult("42", "int", 0));

    public Task<ExceptionInfo?> GetExceptionInfoAsync(int threadId, CancellationToken ct = default)
        => Task.FromResult<ExceptionInfo?>(new ExceptionInfo("System.Exception", "boom", null));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
