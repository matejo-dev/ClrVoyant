using ClrVoyant.Core;

namespace ClrVoyant.Tests.Fakes;

/// <summary>
/// In-memory IDebugEngine test double. Lets tests drive stop/exit/fault events and
/// records control commands, so Session/SessionManager logic can be verified
/// deterministically without netcoredbg.
///
/// By default a resume answers immediately and the stop event fires synchronously
/// inside it (the "event already happened" path). To test a LATE event, set
/// <see cref="StopOnResume"/> to null, start the resume without awaiting it, await
/// <see cref="ResumeCalledAsync"/>, then call <see cref="RaiseStopped"/> /
/// <see cref="RaiseExited"/> / <see cref="RaiseFaulted"/> from the test (or from
/// <c>Task.Run</c>): no timers are involved. It honours the IDebugEngine contract
/// (see EngineContractTests): Exited is raised exactly once, also after TerminateAsync.
/// </summary>
public sealed class FakeDebugEngine : IDebugEngine
{
    readonly SemaphoreSlim _resumeCalls = new(0);
    int _exitRaised;

    public int Pid { get; set; } = 1234;
    public SessionState State { get; set; } = SessionState.Starting;

    public event Action<StopLocation>? Stopped;
    public event Action<int?>? Exited;
    public event Action<string>? Faulted;
    public event Action<DebugOutput>? Output;

    /// <summary>When set, Launch/Attach/Continue/Step throw it (a rejected command).</summary>
    /// <summary>Runs inside LaunchAsync before it fails or succeeds (e.g. to raise a stopAtEntry stop).</summary>
    public Action? OnLaunch { get; set; }
    public Exception? LaunchFailure { get; set; }
    public Exception? AttachFailure { get; set; }
    public Exception? ResumeFailure { get; set; }
    public Exception? TerminateFailure { get; set; }
    public Exception? DisposeFailure { get; set; }
    public Exception? StackTraceFailure { get; set; }
    /// <summary>When set, GetStackTraceAsync waits for it (lets a test hold an enrichment mid-flight).</summary>
    public Task? StackTraceGate { get; set; }
    readonly SemaphoreSlim _stackTraceCalls = new(0);
    /// <summary>Completes once a GetStackTraceAsync call has reached the engine (bounded).</summary>
    public async Task StackTraceCalledAsync()
    {
        if (!await _stackTraceCalls.WaitAsync(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The engine never received a stack trace request.");
    }
    /// <summary>Lines the engine reports as not (yet) verified.</summary>
    public HashSet<int> UnverifiedLines { get; } = new();
    public int DisposeCalls { get; private set; }

    /// <summary>Completes when a resume command (continue/step) has reached the engine,
    /// once per call. Bounded, so a test that never resumes fails instead of hanging.</summary>
    public async Task ResumeCalledAsync()
    {
        if (!await _resumeCalls.WaitAsync(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The engine never received a resume command.");
    }

    // Recorded calls.
    public int ContinueCalls { get; private set; }
    public List<StepKind> Steps { get; } = new();
    /// <summary>The thread each step / stack trace request named, in call order.</summary>
    public List<int> StepThreadIds { get; } = new();
    public List<int> StackTraceThreadIds { get; } = new();
    public int PauseCalls { get; private set; }
    public int TerminateCalls { get; private set; }
    /// <summary>The terminateDebuggee flag of the last TerminateAsync call (null if never called).</summary>
    public bool? LastTerminateDebuggee { get; private set; }
    public LaunchRequest? LastLaunch { get; private set; }
    public List<string> ExceptionFilters { get; private set; } = new();
    public List<(string file, IReadOnlyList<BreakpointRequest> bps)> SetBreakpointCalls { get; } = new();

    // What ContinueAsync/StepAsync should do (default: stop at this location).
    public StopLocation? StopOnResume { get; set; } = new("F.cs", 10, "breakpoint", 1);
    public bool ExitOnResume { get; set; }

    // Canned stack frame used by Session.EnrichAsync.
    public StackFrame TopFrame { get; set; } = new(0, "Foo", "Enriched.cs", 42);

    public void RaiseStopped(StopLocation loc) { State = SessionState.Stopped; Stopped?.Invoke(loc); }
    public void RaiseOutput(string category, string text) => Output?.Invoke(new DebugOutput(category, text));
    /// <summary>The debuggee ends. Raised once per engine, like the real one.</summary>
    public void RaiseExited(int? code)
    {
        State = SessionState.Exited;
        if (Interlocked.Exchange(ref _exitRaised, 1) == 0) Exited?.Invoke(code);
    }
    /// <summary>The debugger connection is lost.</summary>
    public void RaiseFaulted(string reason) { State = SessionState.Faulted; Faulted?.Invoke(reason); }

    public Task LaunchAsync(LaunchRequest request, CancellationToken ct = default)
    {
        LastLaunch = request;
        OnLaunch?.Invoke();
        if (LaunchFailure is not null) return Task.FromException(LaunchFailure);
        State = SessionState.Running;
        return Task.CompletedTask;
    }

    public Task AttachAsync(int pid, CancellationToken ct = default)
    {
        Pid = pid;
        if (AttachFailure is not null) return Task.FromException(AttachFailure);
        State = SessionState.Running;
        return Task.CompletedTask;
    }

    public Task TerminateAsync(bool terminateDebuggee, CancellationToken ct = default)
    {
        TerminateCalls++;
        LastTerminateDebuggee = terminateDebuggee;
        RaiseExited(null);
        return TerminateFailure is null ? Task.CompletedTask : Task.FromException(TerminateFailure);
    }

    public Task<IReadOnlyList<Breakpoint>> SetBreakpointsAsync(string file, IReadOnlyList<BreakpointRequest> breakpoints, CancellationToken ct = default)
    {
        SetBreakpointCalls.Add((file, breakpoints));
        IReadOnlyList<Breakpoint> result = breakpoints
            .Select((b, i) => new Breakpoint(i + 1, !UnverifiedLines.Contains(b.Line), file, b.Line))
            .ToList();
        return Task.FromResult(result);
    }

    public List<IReadOnlyList<FunctionBreakpointRequest>> SetFunctionBreakpointCalls { get; } = new();

    public Task<IReadOnlyList<FunctionBreakpoint>> SetFunctionBreakpointsAsync(IReadOnlyList<FunctionBreakpointRequest> breakpoints, CancellationToken ct = default)
    {
        SetFunctionBreakpointCalls.Add(breakpoints);
        IReadOnlyList<FunctionBreakpoint> result = breakpoints
            .Select((b, i) => new FunctionBreakpoint(i + 1, true, b.FunctionName, "F.cs", 10))
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
        return Resume();
    }

    public Task StepAsync(StepKind kind, int threadId, CancellationToken ct = default)
    {
        Steps.Add(kind);
        StepThreadIds.Add(threadId);
        return Resume();
    }

    public Task PauseAsync(int? threadId, CancellationToken ct = default)
    {
        PauseCalls++;
        return Task.CompletedTask;
    }

    // Simulate the engine resuming and then either stopping again or exiting.
    Task Resume()
    {
        _resumeCalls.Release();
        // A rejected command leaves the target where it was (like the real engine).
        if (ResumeFailure is not null) return Task.FromException(ResumeFailure);
        State = SessionState.Running;
        if (ExitOnResume) RaiseExited(0);
        else if (StopOnResume is not null) RaiseStopped(StopOnResume);
        // else: stays running until the test raises an event (late-stop / timeout paths)
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ThreadInfo>>(new[] { new ThreadInfo(1, "main") });

    public async Task<IReadOnlyList<StackFrame>> GetStackTraceAsync(int threadId, int startFrame, int levels, CancellationToken ct = default)
    {
        StackTraceThreadIds.Add(threadId);
        _stackTraceCalls.Release();
        if (StackTraceGate is { } gate) await gate;
        if (StackTraceFailure is not null) throw StackTraceFailure;
        return new[] { TopFrame };
    }

    public Task<IReadOnlyList<Scope>> GetScopesAsync(int frameId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Scope>>(new[] { new Scope("Locals", 100) });

    /// <summary>The children every variablesReference expands to.</summary>
    public List<Variable> Variables { get; } = new() { new Variable("x", "1", "int", 0) };
    /// <summary>The (start, count) of each variables request, as the engine received them.</summary>
    public List<(int start, int count)> VariablesRequests { get; } = new();

    // Honours start/count like a real DAP adapter (count 0 = all the rest).
    public Task<IReadOnlyList<Variable>> GetVariablesAsync(int variablesReference, int start, int count, CancellationToken ct = default)
    {
        VariablesRequests.Add((start, count));
        var window = Variables.Skip(start);
        return Task.FromResult<IReadOnlyList<Variable>>((count > 0 ? window.Take(count) : window).ToList());
    }

    public Task<EvaluateResult> EvaluateAsync(string expression, int? frameId, string? context, CancellationToken ct = default)
        => Task.FromResult(new EvaluateResult("42", "int", 0));

    public Task<ExceptionInfo?> GetExceptionInfoAsync(int threadId, CancellationToken ct = default)
        => Task.FromResult<ExceptionInfo?>(new ExceptionInfo("System.Exception", "boom", null));

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        return DisposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure);
    }
}
