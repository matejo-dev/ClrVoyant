namespace ClrVoyant.Server.Tools;

/// <summary>
/// The debugging playbook, in one place. It is served twice: as the MCP server instructions
/// (sent in the <c>initialize</c> handshake, which clients surface on their own) and by the
/// <c>get_debug_instructions</c> tool, as a fallback for clients that ignore instructions.
/// </summary>
public static class DebugInstructions
{
    public const string Text = """
        ClrVoyant — how to debug a .NET app with these tools.

        SCOPE: .NET 8/9/10 only, x64/arm64, headless (no IDE). Build the target first; you
        debug the built .dll. You decide WHERE to break and WHAT to inspect by reading
        the source with your own tools — ClrVoyant gives you the debugger primitives.

        TYPICAL LOOP
        1. debug_launch(program=<path-to.dll>, args?, env?, stopAtEntry?) — starts a session.
           env sets environment variables for the debuggee (ASPNETCORE_ENVIRONMENT, ...).
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
        5. For async: list_tasks / get_async_graph / get_async_callstack — they read
           the heap at a stop and list every in-flight Task with its state, and the
           await/continuation chains between them.
        6. restart_debugging() to rerun with the same breakpoints (and env); clear_all_breakpoints
           to reset. debug_stop when done: it kills a process you launched (and children auto-attached from it), but only
           detaches from one you attached to (the process keeps running).
        7. The debuggee's stdout/stderr and exit code: get_output() returns the captured
           tail; the exit code is also in the result of continue/step when the process
           exits, and in debug_status. Read these first when debugging a crash.

        MULTI-SESSION: every session-scoped tool takes an optional sessionId; omit it to
        target the active (most-recently-stopped) session. When several processes run
        asynchronously, use wait_for_any_stop() to learn which one broke, then pass its
        sessionId. set_auto_attach(true) auto-attaches .NET child processes.

        GOTCHAS
        - Introspection (threads/callstack/variables/tasks) requires the target STOPPED.
        - continue/step already block until the next stop; don't poll.
        - Big results are paged: list_tasks, get_async_graph, get_variables and list_methods
          return { items, total, truncated, nextOffset } with at most 'max' items. When
          truncated is true, call again with offset=nextOffset for the next page; narrow
          with filters (list_tasks status, list_methods typeFilter/methodFilter) first.
          nextOffset is absent on the last page; total is absent for get_variables (the
          debugger does not report it).
        - A failed call returns an error result whose text says what to do (for example which
          sessionId to pass, or that the target must be stopped): read it, then fix the call.
        - Breakpoints and locals need a PDB next to the .dll; Debug is the safest
          build, but an optimized (Release) build with a PDB also works.
        - evaluate() can mutate state (it runs getters/methods) — treat as dangerous.
        - To debug unit tests use debug_test(testProject, testName?) — it runs
          'dotnet test' suspended and attaches in one step. Set breakpoints right
          after it returns; execution resumes and hits them.
        """;
}
