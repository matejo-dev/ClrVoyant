namespace ClrVoyant.Core;

/// <summary>Kind of single-step requested.</summary>
public enum StepKind { Over, Into, Out }

/// <summary>A breakpoint to install in a source file.</summary>
public sealed record BreakpointRequest(
    int Line,
    string? Condition = null,
    string? HitCondition = null,
    string? LogMessage = null);

/// <summary>An installed breakpoint as reported by the engine.</summary>
public sealed record Breakpoint(
    int Id,
    bool Verified,
    string File,
    int Line);

/// <summary>A breakpoint on a method by name (no source line / file needed): the
/// debugger binds it from the PDB. <paramref name="FunctionName"/> is matched by
/// netcoredbg as <c>Method</c>, <c>Type.Method</c>, or <c>Namespace.Type.Method</c>.</summary>
public sealed record FunctionBreakpointRequest(
    string FunctionName,
    string? Condition = null,
    string? HitCondition = null);

/// <summary>An installed function breakpoint. Once bound, <paramref name="File"/> and
/// <paramref name="Line"/> are the source location it resolved to (when symbols map
/// one), or null when debugging without source.</summary>
public sealed record FunctionBreakpoint(
    int Id,
    bool Verified,
    string FunctionName,
    string? File,
    int? Line);

/// <summary>Result of a resume operation (continue / step): the resulting state
/// and, when stopped, where. When the process exited, <paramref name="ExitCode"/> is
/// its exit code (null if the debugger did not report one). <paramref name="Message"/>
/// explains a Faulted outcome (the debugger connection was lost).</summary>
public sealed record StopOutcome(SessionState State, StopLocation? Stop, int? ExitCode = null, string? Message = null);

/// <summary>A chunk of debuggee output as reported by the debugger. <paramref name="Category"/>
/// is the DAP output category (typically stdout or stderr; console for the debugger's
/// own messages).</summary>
public sealed record DebugOutput(string Category, string Text);

/// <summary>The captured tail of a session's debuggee output (get_output).
/// <paramref name="Truncated"/> is true when earlier output is not included, either
/// dropped by the capture cap or cut by the requested limit.</summary>
public sealed record SessionOutput(string Text, bool Truncated, SessionState State, int? ExitCode);

/// <summary>A running OS process, for discovery before debug_attach (e.g. finding
/// the app process inside a shared-PID-namespace POD).</summary>
public sealed record ProcessInfo(int Pid, int ParentPid, string Name, bool IsDotNet, string? CommandLine = null);

/// <summary>A method discovered in an assembly's metadata, for the no-source case:
/// <paramref name="FullName"/> (Namespace.Type.Method) is ready to pass to
/// set_function_breakpoint.</summary>
public sealed record MethodSymbol(string FullName, string DeclaringType, string Method);

public sealed record ThreadInfo(int Id, string Name);

public sealed record StackFrame(int Id, string Function, string? File, int? Line);

public sealed record Scope(string Name, int VariablesReference);

public sealed record Variable(string Name, string Value, string Type, int VariablesReference);

public sealed record EvaluateResult(string Result, string Type, int VariablesReference);

public sealed record ExceptionInfo(string TypeName, string Description, string? Details);

/// <summary>A managed Task discovered on the heap (read with ClrMD).</summary>
public sealed record TaskInfo(
    long TaskId,
    string Address,
    string Status,
    string TypeName,
    string? AsyncMethod,
    string? Continuation);

/// <summary>A node in the async await/continuation graph.
/// <paramref name="Awaiting"/> are the addresses of Tasks this state machine is
/// parked on; <paramref name="ContinuationAddress"/> is the Task resumed when
/// this one completes.</summary>
public sealed record AsyncNode(
    string Address,
    string Status,
    string? AsyncMethod,
    string TypeName,
    IReadOnlyList<string> Awaiting,
    string? ContinuationAddress);
