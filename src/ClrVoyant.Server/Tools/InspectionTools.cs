using System.ComponentModel;
using ClrVoyant.Core;
using ModelContextProtocol.Server;

namespace ClrVoyant.Server.Tools;

/// <summary>Live introspection through the debugger (threads, stack, variables, evaluate). All of
/// it requires the target to be stopped; otherwise the call fails fast with a message saying so.</summary>
[McpServerToolType]
public static class InspectionTools
{
    [McpServerTool(Name = "get_threads", Title = "List threads", ReadOnly = true, OpenWorld = false)]
    [Description("List the threads of a stopped target.")]
    public static Task<IReadOnlyList<ThreadInfo>> GetThreads(
        SessionManager sessions, [Description("Session id; omit for the active session.")] string? sessionId = null, CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).GetThreadsAsync(cancellationToken);

    [McpServerTool(Name = "get_callstack", Title = "Get a thread's call stack", ReadOnly = true, OpenWorld = false)]
    [Description("Get the call stack of a thread in a stopped target (async-aware). Use the returned frameId for get_scopes/evaluate.")]
    public static Task<IReadOnlyList<StackFrame>> GetCallstack(
        SessionManager sessions,
        [Description("Thread id; omit for the last stopped thread.")] int? threadId = null,
        [Description("Index of the first frame to return (0 = innermost).")] int startFrame = 0,
        [Description("Maximum number of frames to return.")] int levels = 20,
        [Description("Session id; omit for the active session.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).GetStackTraceAsync(threadId, startFrame, levels, cancellationToken);

    [McpServerTool(Name = "get_scopes", Title = "Get a frame's variable scopes", ReadOnly = true, OpenWorld = false)]
    [Description("Get the variable scopes (Locals, Arguments, ...) for a stack frame. Each returns a variablesReference for get_variables.")]
    public static Task<IReadOnlyList<Scope>> GetScopes(
        SessionManager sessions,
        [Description("frameId from get_callstack.")] int frameId,
        [Description("Session id; omit for the active session.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).GetScopesAsync(frameId, cancellationToken);

    [McpServerTool(Name = "get_variables", Title = "Expand a variablesReference", ReadOnly = true, OpenWorld = false)]
    [Description("Expand a variablesReference (from get_scopes or a parent variable) into its child variables. Walk references to inspect nested objects. Returns a page { items, total, truncated, nextOffset }: at most 'max' children (a large collection is cut); when truncated, repeat with offset=nextOffset. total is omitted: the debugger does not report it.")]
    public static Task<Page<Variable>> GetVariables(
        SessionManager sessions,
        [Description("variablesReference from a scope or a parent variable.")] int variablesReference,
        [Description("Session id; omit for the active session.")] string? sessionId = null,
        [Description("Maximum number of children to return in this page (1-1000).")] int max = Paging.DefaultMax,
        [Description("Number of children to skip (use the previous page's nextOffset).")] int offset = 0,
        CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).GetVariablesAsync(variablesReference, offset, max, cancellationToken);

    [McpServerTool(Name = "evaluate", Title = "Evaluate an expression in the debuggee", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Evaluate an expression in the context of a stack frame. ⚠️ DANGEROUS: evaluating an expression EXECUTES CODE in the debuggee (property getters, method calls) and can mutate state or cause side effects. Prefer get_variables for plain inspection.")]
    public static Task<EvaluateResult> Evaluate(
        SessionManager sessions,
        [Description("The expression to evaluate.")] string expression,
        [Description("frameId for evaluation context; omit for the top frame.")] int? frameId = null,
        [Description("Evaluation context: 'repl', 'watch', or 'hover'.")] string? context = null,
        [Description("Session id; omit for the active session.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).EvaluateAsync(expression, frameId, context, cancellationToken);

    [McpServerTool(Name = "get_exception_info", Title = "Get the current exception", ReadOnly = true, OpenWorld = false)]
    [Description("When stopped on an exception, get its type, message and details. Returns null if not stopped on an exception.")]
    public static Task<ExceptionInfo?> GetExceptionInfo(
        SessionManager sessions,
        [Description("Thread id; omit for the last stopped thread.")] int? threadId = null,
        [Description("Session id; omit for the active session.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).GetExceptionInfoAsync(threadId, cancellationToken);
}
