using System.ComponentModel;
using System.Globalization;
using ClrVoyant.Core;
using ClrVoyant.Inspection;
using ModelContextProtocol.Server;

namespace ClrVoyant.Server.Tools;

/// <summary>
/// Phase 1 MCP tool surface: multi-session lifecycle. Every session-scoped tool
/// accepts an optional sessionId; when omitted it targets the active session
/// (the only one, or the most-recently-stopped).
/// </summary>
[McpServerToolType]
public static class DebugTools
{
    [McpServerTool(Name = "get_debug_instructions")]
    [Description("Read this FIRST when you are about to debug a .NET app. Returns a concise playbook: the typical loop, which tools to call in what order, and the gotchas that trip agents up. Costs nothing to call and prevents wrong tool sequencing.")]
    public static string GetDebugInstructions() => Instructions;

    const string Instructions = """
        ClrVoyant — how to debug a .NET app with these tools.

        SCOPE: .NET 8/9/10 only, x64/arm64, headless (no IDE). Build the target first; you
        debug the built .dll. You decide WHERE to break and WHAT to inspect by reading
        the source with your own tools — ClrVoyant gives you the debugger primitives.

        TYPICAL LOOP
        1. debug_launch(program=<path-to.dll>, args?, stopAtEntry?) — starts a session.
           (Or debug_attach(pid) for an already-running process; use list_processes
           to find its pid, e.g. the app process in a shared-PID-namespace POD.)
        2. set_breakpoint(file, content=<source text of the line>) — prefer 'content'
           over a raw line number; it survives miscounts and line drift. Use a 'line'
           hint only to disambiguate when the same text appears more than once.
           NO SOURCE? Use set_function_breakpoint("Namespace.Type.Method") — it binds
           from the PDB alone, so it's how you break in a deployed build you have the
           DLLs + PDBs for but not the .cs.
        3. continue() — BLOCKS until the next stop and returns the new location. So do
           step_over / step_into / step_out. No separate "wait" call after them.
        4. At a stop: get_callstack -> get_scopes(frameId) -> get_variables(ref). Walk
           variablesReference to expand nested objects. Prefer get_variables over
           evaluate for plain inspection — evaluate EXECUTES code in the target.
        5. For async: list_tasks / get_async_graph / get_async_callstack — the unique
           feature; see ALL in-flight Tasks and their await/continuation chains.
        6. restart_debugging() to rerun with the same breakpoints; clear_all_breakpoints
           to reset. debug_stop when done.

        MULTI-SESSION: every session-scoped tool takes an optional sessionId; omit it to
        target the active (most-recently-stopped) session. When several processes run
        asynchronously, use wait_for_any_stop() to learn which one broke, then pass its
        sessionId. set_auto_attach(true) auto-attaches .NET child processes.

        GOTCHAS
        - Introspection (threads/callstack/variables/tasks) requires the target STOPPED.
        - continue/step already block until the next stop; don't poll.
        - Breakpoints and locals need a PDB next to the .dll; Debug is the safest
          build, but an optimized (Release) build with a PDB also works.
        - evaluate() can mutate state (it runs getters/methods) — treat as dangerous.
        - To debug unit tests use debug_test(testProject, testName?) — it runs
          'dotnet test' suspended and attaches in one step. Set breakpoints right
          after it returns; execution resumes and hits them.
        """;

    [McpServerTool(Name = "debug_launch")]
    [Description("Launch a built .NET (.dll) under the debugger and start a new debug session. Build the project first; pass the path to the output assembly. Returns the new session's id, pid and state.")]
    public static Task<SessionInfo> DebugLaunch(
        SessionManager sessions,
        [Description("Full path to the built .dll to launch.")] string program,
        [Description("Command-line arguments for the program.")] string[]? args = null,
        [Description("Working directory; defaults to the program's folder.")] string? cwd = null,
        [Description("Break at program entry instead of running freely.")] bool stopAtEntry = false)
        => sessions.LaunchAsync(new LaunchRequest(program, args, cwd, stopAtEntry));

    [McpServerTool(Name = "debug_attach")]
    [Description("Attach the debugger to an already-running .NET process by PID, creating a new debug session. Use this for secondary/child processes you discover.")]
    public static Task<SessionInfo> DebugAttach(
        SessionManager sessions,
        [Description("OS process id to attach to.")] int pid)
        => sessions.AttachAsync(pid);

    [McpServerTool(Name = "list_processes")]
    [Description("List running processes (pid, parent pid, name, isDotNet) so you can pick one to debug_attach. Defaults to .NET processes only. Useful in a shared-PID-namespace POD to find the app process to debug.")]
    public static IReadOnlyList<ProcessInfo> ListProcesses(
        [Description("List only .NET (Core) processes. Set false to list every process.")] bool dotnetOnly = true)
        => ProcessLister.List(dotnetOnly);

    [McpServerTool(Name = "list_methods")]
    [Description("Discover methods in a built assembly by reading its metadata statically — no running process or source needed. Use it for the no-source case: point at a deployed .dll, filter by type/method name, then pass a returned FullName ('Namespace.Type.Method') straight to set_function_breakpoint. Compiler-generated members are omitted.")]
    public static IReadOnlyList<MethodSymbol> ListMethods(
        [Description("Full path to the assembly (.dll) to scan.")] string assemblyPath,
        [Description("Optional case-insensitive substring to filter the declaring type name.")] string? typeFilter = null,
        [Description("Optional case-insensitive substring to filter the method name.")] string? methodFilter = null,
        [Description("Maximum number of methods to return.")] int max = 200)
        => AssemblyMethodScanner.ListMethods(assemblyPath, typeFilter, methodFilter, max);

    [McpServerTool(Name = "debug_test")]
    [Description("Debug a unit test in one step: runs 'dotnet test' on the project with the test host suspended, then attaches a session to it. Set breakpoints right after this returns — execution resumes and hits them. Build the test project with a PDB (Debug is safest for full locals). The 'dotnet test' process is killed when you debug_stop the session.")]
    public static Task<SessionInfo> DebugTest(
        TestDebugLauncher launcher,
        [Description("Path to the test project (.csproj) or its directory.")] string testProject,
        [Description("Optional test filter (passed to 'dotnet test --filter'), e.g. a test or class name. Omit to debug the whole project.")] string? testName = null,
        [Description("Build configuration; Debug is safest for full locals (what's actually required is a PDB).")] string configuration = "Debug",
        [Description("Max time to wait for the test host to come up, milliseconds.")] int timeoutMs = 120000)
        => launcher.LaunchAndAttachAsync(testProject, testName, configuration, TimeSpan.FromMilliseconds(timeoutMs));

    [McpServerTool(Name = "set_auto_attach")]
    [Description("Enable/disable automatic attaching to .NET child processes spawned by debugged processes (matched by parent PID). Off by default. When on, children appear as new sessions shortly after they start; use wait_for_any_stop and list_sessions to drive them. Note: a child's very first startup instants may run before attach.")]
    public static string SetAutoAttach(
        AutoAttachOptions options,
        [Description("True to enable child-process auto-discovery.")] bool enabled)
    {
        options.Enabled = enabled;
        return enabled ? "auto-attach enabled" : "auto-attach disabled";
    }

    [McpServerTool(Name = "debug_status")]
    [Description("Get the current state of a debug session (starting/running/stopped/exited) and, when stopped, the current location.")]
    public static SessionInfo DebugStatus(
        SessionManager sessions,
        [Description("Session id; omit for the active session.")] string? sessionId = null)
        => sessions.Resolve(sessionId).ToInfo();

    [McpServerTool(Name = "debug_stop")]
    [Description("Terminate the debuggee and end the session.")]
    public static async Task<string> DebugStop(
        SessionManager sessions,
        [Description("Session id; omit for the active session.")] string? sessionId = null)
    {
        await sessions.StopAsync(sessionId);
        return "stopped";
    }

    [McpServerTool(Name = "restart_debugging")]
    [Description("Restart a launched session in place: terminate the debuggee and relaunch it with the SAME program/args, keeping the same session id and all breakpoints. Only works for sessions started with debug_launch (not debug_attach). Returns the restarted session's info.")]
    public static Task<SessionInfo> RestartDebugging(
        SessionManager sessions,
        [Description("Session id; omit for the active session.")] string? sessionId = null)
        => sessions.RestartAsync(sessionId);

    [McpServerTool(Name = "list_sessions")]
    [Description("List all debug sessions with their pid, state and last stop.")]
    public static IReadOnlyList<SessionInfo> ListSessions(SessionManager sessions)
        => sessions.List();

    [McpServerTool(Name = "wait_for_any_stop")]
    [Description("Block until ANY session hits a stop (breakpoint, step, exception), then report which one. Essential when several processes run asynchronously.")]
    public static async Task<object> WaitForAnyStop(
        SessionManager sessions,
        [Description("Max time to wait, milliseconds.")] int timeoutMs = 30000)
    {
        var stop = await sessions.WaitForAnyStopAsync(TimeSpan.FromMilliseconds(timeoutMs));
        return stop is null ? new { timedOut = true } : stop;
    }

    // ---------------- Phase 2: breakpoints + execution control ----------------

    [McpServerTool(Name = "set_breakpoint")]
    [Description("Set a source breakpoint. Returns its id and whether it was verified (bound). Can be set while running or stopped. Prefer 'content' (the source text of the line) over a raw 'line' number when you can: it survives line-number drift and miscounts. With 'content', 'line' becomes a hint used only to disambiguate when several lines match.")]
    public static Task<Breakpoint> SetBreakpoint(
        SessionManager sessions,
        [Description("Absolute path to the source file.")] string file,
        [Description("1-based line number. Required unless 'content' is given, in which case it is an optional disambiguation hint (0 = none).")] int line = 0,
        [Description("Source text of the target line to match (robust to line shifts). When set, the actual line is resolved by matching this text.")] string? content = null,
        [Description("Optional condition expression; breaks only when true.")] string? condition = null,
        [Description("Optional hit condition, e.g. '>=3'.")] string? hitCondition = null,
        [Description("Optional log message (logpoint); logs instead of breaking.")] string? logMessage = null,
        string? sessionId = null)
    {
        int targetLine = line;
        if (content is not null)
        {
            string path = Path.GetFullPath(file);
            var lines = File.ReadAllLines(path);
            targetLine = BreakpointLocator.ResolveLine(lines, content, line > 0 ? line : null);
        }
        else if (line < 1)
        {
            throw new ArgumentException("Provide either a 1-based 'line' or the 'content' of the line to match.");
        }
        return sessions.Resolve(sessionId).AddBreakpointAsync(file, new BreakpointRequest(targetLine, condition, hitCondition, logMessage));
    }

    [McpServerTool(Name = "set_function_breakpoint")]
    [Description("Set a breakpoint on a METHOD by name — no source file or line needed. Binds straight from the PDB, so it's the way to break in code you don't have the .cs for (a deployed build with PDBs + DLLs). Match by 'Method', 'Type.Method', or 'Namespace.Type.Method'; qualify it when the name is ambiguous. Returns its id, verified state, and the resolved file/line when symbols map one.")]
    public static Task<FunctionBreakpoint> SetFunctionBreakpoint(
        SessionManager sessions,
        [Description("Method to break on: 'Method', 'Type.Method', or 'Namespace.Type.Method'.")] string functionName,
        [Description("Optional condition expression; breaks only when true.")] string? condition = null,
        [Description("Optional hit condition, e.g. '>=3'.")] string? hitCondition = null,
        string? sessionId = null)
        => sessions.Resolve(sessionId).AddFunctionBreakpointAsync(new FunctionBreakpointRequest(functionName, condition, hitCondition));

    [McpServerTool(Name = "list_function_breakpoints")]
    [Description("List all function (method-name) breakpoints in the session with their verified state and resolved file/line.")]
    public static Task<IReadOnlyList<FunctionBreakpoint>> ListFunctionBreakpoints(SessionManager sessions, string? sessionId = null)
        => sessions.Resolve(sessionId).ListFunctionBreakpointsAsync();

    [McpServerTool(Name = "remove_breakpoint")]
    [Description("Remove a breakpoint by its id. Returns true if it existed.")]
    public static Task<bool> RemoveBreakpoint(
        SessionManager sessions,
        [Description("Breakpoint id returned by set_breakpoint.")] int bpId,
        string? sessionId = null)
        => sessions.Resolve(sessionId).RemoveBreakpointAsync(bpId);

    [McpServerTool(Name = "list_breakpoints")]
    [Description("List all breakpoints in the session with their verified state and line.")]
    public static Task<IReadOnlyList<Breakpoint>> ListBreakpoints(SessionManager sessions, string? sessionId = null)
        => sessions.Resolve(sessionId).ListBreakpointsAsync();

    [McpServerTool(Name = "clear_all_breakpoints")]
    [Description("Remove every breakpoint in the session — all source breakpoints across all files AND all function breakpoints — in one call. Returns how many were cleared.")]
    public static async Task<string> ClearAllBreakpoints(SessionManager sessions, string? sessionId = null)
    {
        int n = await sessions.Resolve(sessionId).ClearBreakpointsAsync();
        return $"cleared {n} breakpoint(s)";
    }

    [McpServerTool(Name = "set_exception_breakpoints")]
    [Description("Configure which exceptions break into the debugger. Common filters: 'all', 'user-unhandled'. Pass an empty list to disable.")]
    public static async Task<string> SetExceptionBreakpoints(
        SessionManager sessions,
        [Description("Exception filter ids, e.g. ['user-unhandled'].")] string[] filters,
        string? sessionId = null)
    {
        await sessions.Resolve(sessionId).SetExceptionBreakpointsAsync(filters);
        return "ok";
    }

    [McpServerTool(Name = "continue")]
    [Description("Resume execution and BLOCK until the next stop (breakpoint/exception), the process exits, or the timeout. Returns the resulting state and location.")]
    public static Task<StopOutcome> Continue(
        SessionManager sessions,
        [Description("Thread to resume; omit for the last stopped thread.")] int? threadId = null,
        [Description("Max time to wait for the next stop, milliseconds.")] int timeoutMs = 30000,
        string? sessionId = null)
        => sessions.Resolve(sessionId).ContinueAsync(threadId, TimeSpan.FromMilliseconds(timeoutMs));

    [McpServerTool(Name = "step_over")]
    [Description("Step over the current line (run called methods without stepping in) and block until stopped. Returns the new location.")]
    public static Task<StopOutcome> StepOver(SessionManager sessions, int? threadId = null, int timeoutMs = 30000, string? sessionId = null)
        => Step(sessions, StepKind.Over, threadId, timeoutMs, sessionId);

    [McpServerTool(Name = "step_into")]
    [Description("Step into the call on the current line and block until stopped. Returns the new location.")]
    public static Task<StopOutcome> StepInto(SessionManager sessions, int? threadId = null, int timeoutMs = 30000, string? sessionId = null)
        => Step(sessions, StepKind.Into, threadId, timeoutMs, sessionId);

    [McpServerTool(Name = "step_out")]
    [Description("Step out of the current method and block until stopped. Returns the new location.")]
    public static Task<StopOutcome> StepOut(SessionManager sessions, int? threadId = null, int timeoutMs = 30000, string? sessionId = null)
        => Step(sessions, StepKind.Out, threadId, timeoutMs, sessionId);

    static Task<StopOutcome> Step(SessionManager sessions, StepKind kind, int? threadId, int timeoutMs, string? sessionId)
    {
        var s = sessions.Resolve(sessionId);
        int tid = threadId ?? s.LastStop?.ThreadId
            ?? throw new InvalidOperationException("No current thread; pass threadId.");
        return s.StepAsync(kind, tid, TimeSpan.FromMilliseconds(timeoutMs));
    }

    [McpServerTool(Name = "pause")]
    [Description("Break into a running target at its current point of execution.")]
    public static async Task<string> Pause(SessionManager sessions, int? threadId = null, string? sessionId = null)
    {
        await sessions.Resolve(sessionId).PauseAsync(threadId);
        return "pausing";
    }

    // ---------------- Phase 3: introspection ----------------

    [McpServerTool(Name = "get_threads")]
    [Description("List the threads of a stopped target.")]
    public static Task<IReadOnlyList<ThreadInfo>> GetThreads(SessionManager sessions, string? sessionId = null)
        => sessions.Resolve(sessionId).GetThreadsAsync();

    [McpServerTool(Name = "get_callstack")]
    [Description("Get the call stack of a thread in a stopped target (async-aware). Use the returned frameId for get_scopes/evaluate.")]
    public static Task<IReadOnlyList<StackFrame>> GetCallstack(
        SessionManager sessions,
        [Description("Thread id; omit for the last stopped thread.")] int? threadId = null,
        int startFrame = 0,
        int levels = 20,
        string? sessionId = null)
    {
        var s = sessions.Resolve(sessionId);
        int tid = threadId ?? s.LastStop?.ThreadId
            ?? throw new InvalidOperationException("No current thread; pass threadId.");
        return s.GetStackTraceAsync(tid, startFrame, levels);
    }

    [McpServerTool(Name = "get_scopes")]
    [Description("Get the variable scopes (Locals, Arguments, ...) for a stack frame. Each returns a variablesReference for get_variables.")]
    public static Task<IReadOnlyList<Scope>> GetScopes(
        SessionManager sessions,
        [Description("frameId from get_callstack.")] int frameId,
        string? sessionId = null)
        => sessions.Resolve(sessionId).GetScopesAsync(frameId);

    [McpServerTool(Name = "get_variables")]
    [Description("Expand a variablesReference (from get_scopes or a parent variable) into its child variables. Walk references to inspect nested objects.")]
    public static Task<IReadOnlyList<Variable>> GetVariables(
        SessionManager sessions,
        [Description("variablesReference from a scope or a parent variable.")] int variablesReference,
        string? sessionId = null)
        => sessions.Resolve(sessionId).GetVariablesAsync(variablesReference);

    [McpServerTool(Name = "evaluate")]
    [Description("Evaluate an expression in the context of a stack frame. ⚠️ DANGEROUS: evaluating an expression EXECUTES CODE in the debuggee (property getters, method calls) and can mutate state or cause side effects. Prefer get_variables for plain inspection.")]
    public static Task<EvaluateResult> Evaluate(
        SessionManager sessions,
        [Description("The expression to evaluate.")] string expression,
        [Description("frameId for evaluation context; omit for the top frame.")] int? frameId = null,
        [Description("Evaluation context: 'repl', 'watch', or 'hover'.")] string? context = null,
        string? sessionId = null)
        => sessions.Resolve(sessionId).EvaluateAsync(expression, frameId, context);

    [McpServerTool(Name = "get_exception_info")]
    [Description("When stopped on an exception, get its type, message and details. Returns null if not stopped on an exception.")]
    public static Task<ExceptionInfo?> GetExceptionInfo(
        SessionManager sessions,
        [Description("Thread id; omit for the last stopped thread.")] int? threadId = null,
        string? sessionId = null)
    {
        var s = sessions.Resolve(sessionId);
        int tid = threadId ?? s.LastStop?.ThreadId
            ?? throw new InvalidOperationException("No current thread; pass threadId.");
        return s.GetExceptionInfoAsync(tid);
    }

    // ---------------- Phase 4: Tasks enumeration (ClrMD) ----------------

    [McpServerTool(Name = "list_tasks")]
    [Description("Enumerate ALL managed Tasks on the heap of a stopped target (the 'Tasks window' equivalent), with status and async method. Optionally filter by status (e.g. 'WaitingForActivation', 'Running', 'Faulted').")]
    public static Task<IReadOnlyList<TaskInfo>> ListTasks(
        SessionManager sessions,
        TaskInspector inspector,
        [Description("Optional status filter, case-insensitive.")] string? status = null,
        string? sessionId = null)
    {
        var s = sessions.Resolve(sessionId);
        s.EnsureStopped();
        int pid = s.Pid;
        long stopId = s.StopId;
        return Task.Run<IReadOnlyList<TaskInfo>>(() =>
        {
            var all = inspector.EnumerateTasks(pid, stopId);
            return status is null
                ? all
                : all.Where(t => t.Status.Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
        });
    }

    [McpServerTool(Name = "get_task")]
    [Description("Inspect a single Task by its heap address (as returned by list_tasks, e.g. '0x1c3...'). Returns null if not a Task.")]
    public static Task<TaskInfo?> GetTask(
        SessionManager sessions,
        TaskInspector inspector,
        [Description("Heap address, hex (0x...) or decimal.")] string address,
        string? sessionId = null)
    {
        var s = sessions.Resolve(sessionId);
        s.EnsureStopped();
        int pid = s.Pid;
        long stopId = s.StopId;
        return Task.Run(() => inspector.GetTask(pid, stopId, ParseAddress(address)));
    }

    // ---------------- Phase 5: async graph ----------------

    [McpServerTool(Name = "get_async_graph")]
    [Description("Reconstruct the async await/continuation graph of a stopped target: for each async Task, which Tasks it is awaiting and which Task it continues into. Use addresses to correlate with list_tasks/get_task.")]
    public static Task<IReadOnlyList<AsyncNode>> GetAsyncGraph(
        SessionManager sessions,
        TaskInspector inspector,
        string? sessionId = null)
    {
        var s = sessions.Resolve(sessionId);
        s.EnsureStopped();
        int pid = s.Pid;
        long stopId = s.StopId;
        return Task.Run(() => inspector.BuildAsyncGraph(pid, stopId));
    }

    [McpServerTool(Name = "get_async_callstack")]
    [Description("Follow continuation links from a starting Task address (from list_tasks) to produce the logical async call stack — the chain of async methods that resume one after another.")]
    public static Task<IReadOnlyList<AsyncNode>> GetAsyncCallstack(
        SessionManager sessions,
        TaskInspector inspector,
        [Description("Starting Task heap address (hex 0x... or decimal).")] string address,
        string? sessionId = null)
    {
        var s = sessions.Resolve(sessionId);
        s.EnsureStopped();
        int pid = s.Pid;
        long stopId = s.StopId;
        return Task.Run(() => inspector.FollowContinuations(pid, stopId, ParseAddress(address)));
    }

    static ulong ParseAddress(string address)
        => address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(address.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(address, CultureInfo.InvariantCulture);
}
