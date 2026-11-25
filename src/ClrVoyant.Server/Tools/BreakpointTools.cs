using System.ComponentModel;
using ClrVoyant.Core;
using ClrVoyant.Inspection;
using ModelContextProtocol.Server;

namespace ClrVoyant.Server.Tools;

/// <summary>Breakpoints (source, function, exception) and the static method discovery that feeds
/// function breakpoints. All of them work while the target runs or is stopped.</summary>
[McpServerToolType]
public static class BreakpointTools
{
    [McpServerTool(Name = "set_breakpoint", Title = "Set a source breakpoint", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Set a source breakpoint. Returns its id and whether it was verified (bound). Can be set while running or stopped. Prefer 'content' (the source text of the line) over a raw 'line' number when you can: it survives line-number drift and miscounts. With 'content', 'line' becomes a hint used only to disambiguate when several lines match.")]
    public static Task<Breakpoint> SetBreakpoint(
        SessionManager sessions,
        [Description("Absolute path to the source file.")] string file,
        [Description("1-based line number. Required unless 'content' is given, in which case it is an optional disambiguation hint (0 = none).")] int line = 0,
        [Description("Source text of the target line to match (robust to line shifts). When set, the actual line is resolved by matching this text.")] string? content = null,
        [Description("Optional condition expression; breaks only when true.")] string? condition = null,
        [Description("Optional hit condition, e.g. '>=3'.")] string? hitCondition = null,
        [Description("Optional log message (logpoint); logs instead of breaking.")] string? logMessage = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).AddSourceBreakpointAsync(
            file, new BreakpointRequest(line, condition, hitCondition, logMessage), content, cancellationToken);

    [McpServerTool(Name = "set_function_breakpoint", Title = "Set a breakpoint on a method by name", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Set a breakpoint on a METHOD by name — no source file or line needed. Binds straight from the PDB, so it's the way to break in code you don't have the .cs for (a deployed build with PDBs + DLLs). Match by 'Method', 'Type.Method', or 'Namespace.Type.Method'; qualify it when the name is ambiguous. Returns its id, verified state, and the resolved file/line when symbols map one.")]
    public static Task<FunctionBreakpoint> SetFunctionBreakpoint(
        SessionManager sessions,
        [Description("Method to break on: 'Method', 'Type.Method', or 'Namespace.Type.Method'.")] string functionName,
        [Description("Optional condition expression; breaks only when true.")] string? condition = null,
        [Description("Optional hit condition, e.g. '>=3'.")] string? hitCondition = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).AddFunctionBreakpointAsync(new FunctionBreakpointRequest(functionName, condition, hitCondition), cancellationToken);

    [McpServerTool(Name = "list_function_breakpoints", Title = "List function breakpoints", ReadOnly = true, OpenWorld = false)]
    [Description("List all function (method-name) breakpoints in the session with their verified state and resolved file/line.")]
    public static Task<IReadOnlyList<FunctionBreakpoint>> ListFunctionBreakpoints(
        SessionManager sessions, string? sessionId = null, CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).ListFunctionBreakpointsAsync(cancellationToken);

    [McpServerTool(Name = "remove_breakpoint", Title = "Remove a breakpoint", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Remove a breakpoint by its id. Returns true if it existed.")]
    public static Task<bool> RemoveBreakpoint(
        SessionManager sessions,
        [Description("Breakpoint id returned by set_breakpoint.")] int bpId,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).RemoveBreakpointAsync(bpId, cancellationToken);

    [McpServerTool(Name = "list_breakpoints", Title = "List source breakpoints", ReadOnly = true, OpenWorld = false)]
    [Description("List all breakpoints in the session with their verified state and line.")]
    public static Task<IReadOnlyList<Breakpoint>> ListBreakpoints(
        SessionManager sessions, string? sessionId = null, CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).ListBreakpointsAsync(cancellationToken);

    [McpServerTool(Name = "clear_all_breakpoints", Title = "Remove every breakpoint", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Remove every breakpoint in the session — all source breakpoints across all files AND all function breakpoints — in one call. Returns how many were cleared.")]
    public static async Task<string> ClearAllBreakpoints(
        SessionManager sessions, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        int n = await sessions.Resolve(sessionId).ClearBreakpointsAsync(cancellationToken);
        return $"cleared {n} breakpoint(s)";
    }

    [McpServerTool(Name = "set_exception_breakpoints", Title = "Choose which exceptions break", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Configure which exceptions break into the debugger. Common filters: 'all', 'user-unhandled'. Pass an empty list to disable.")]
    public static async Task<string> SetExceptionBreakpoints(
        SessionManager sessions,
        [Description("Exception filter ids, e.g. ['user-unhandled'].")] string[] filters,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        await sessions.Resolve(sessionId).SetExceptionBreakpointsAsync(filters, cancellationToken);
        return "ok";
    }

    [McpServerTool(Name = "list_methods", Title = "List methods of an assembly", ReadOnly = true, OpenWorld = false)]
    [Description("Discover methods in a built assembly by reading its metadata statically — no running process or source needed. Use it for the no-source case: point at a deployed .dll, filter by type/method name, then pass a returned FullName ('Namespace.Type.Method') straight to set_function_breakpoint. Compiler-generated members are omitted. Returns a page { items, total, truncated, nextOffset }: at most 'max' methods; when truncated, repeat with offset=nextOffset, or narrow with the filters.")]
    public static Page<MethodSymbol> ListMethods(
        [Description("Full path to the assembly (.dll) to scan.")] string assemblyPath,
        [Description("Optional case-insensitive substring to filter the declaring type name.")] string? typeFilter = null,
        [Description("Optional case-insensitive substring to filter the method name.")] string? methodFilter = null,
        [Description("Maximum number of methods to return in this page (1-1000).")] int max = 200,
        [Description("Number of matching methods to skip (use the previous page's nextOffset).")] int offset = 0)
        => AssemblyMethodScanner.ListMethods(assemblyPath, typeFilter, methodFilter, max, offset);
}
