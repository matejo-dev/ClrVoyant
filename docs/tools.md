# Tool reference

The complete MCP tool surface — **34 tools**. The source of truth is the
`[Description]` on each tool in
[`DebugTools.cs`](../src/ClrVoyant.Server/Tools/DebugTools.cs) (what the agent
actually reads); this page groups and explains them.

**Conventions**
- Every *session-scoped* tool takes an optional `sessionId`; omit it to target the
  active session (the only one, or the most-recently-stopped).
- *Execution* tools are **wait-based**: they block until the next stop / exit /
  timeout and return the new state ([ADR-0004](adr/0004-wait-based-execution-model.md)).
- *Introspection* tools require the target **stopped**; otherwise they fail fast.

## Onboarding

| Tool | Purpose |
|---|---|
| `get_debug_instructions()` | Returns a short playbook (typical loop, tool ordering, gotchas). Call it first; costs nothing. |

## Sessions & lifecycle

| Tool | Signature | Notes |
|---|---|---|
| `debug_launch` | `(program, args?, cwd?, stopAtEntry?)` | Launch a built `.dll`. Returns `{ sessionId, pid, state }`. |
| `debug_attach` | `(pid)` | Attach to a running process; new session. |
| `list_processes` | `(dotnetOnly=true)` | List processes `{ pid, parentPid, name, isDotNet }` to find a target to attach (e.g. the app in a shared-PID-namespace POD). |
| `debug_test` | `(testProject, testName?, configuration=Debug, timeoutMs?)` | Run `dotnet test` with the host suspended (`VSTEST_HOST_DEBUG`) and attach in one step. The driver process is killed on `debug_stop`. |
| `restart_debugging` | `(sessionId?)` | Relaunch a *launched* session in place: same id, same args, breakpoints preserved. Not valid for attached sessions. |
| `set_auto_attach` | `(enabled)` | Toggle auto-attach of .NET child processes (by parent PID). Off by default; no suspend-at-startup. |
| `debug_status` | `(sessionId?)` | `{ state, pid, stop? }`. |
| `debug_stop` | `(sessionId?)` | Terminate the debuggee and end the session. |
| `list_sessions` | `()` | All sessions with pid/state/last-stop. |
| `wait_for_any_stop` | `(timeoutMs=30000)` | Block until *any* session breaks; reports which. Key when several processes run asynchronously. |

## Breakpoints & execution

| Tool | Signature | Notes |
|---|---|---|
| `set_breakpoint` | `(file, line?, content?, condition?, hitCondition?, logMessage?)` | Prefer `content` (the source text of the line) over a raw `line`: it survives line drift; `line` then only disambiguates duplicate matches. |
| `set_function_breakpoint` | `(functionName, condition?, hitCondition?)` | Break on a **method by name** (`Method` / `Type.Method` / `Namespace.Type.Method`) — no source line. Binds from the PDB, so it's the **no-source** path (deployed DLLs + PDBs). Returns the resolved file/line when symbols map one. |
| `list_function_breakpoints` | `()` | All function breakpoints with verified state and resolved file/line. |
| `list_methods` | `(assemblyPath, typeFilter?, methodFilter?, max=200)` | Discover method names in a built `.dll` by reading metadata statically (no running process). Each `FullName` is ready to pass to `set_function_breakpoint`. Compiler-generated members are omitted. |
| `remove_breakpoint` | `(bpId)` | Returns true if it existed (source or function breakpoint). |
| `clear_all_breakpoints` | `()` | Remove every breakpoint — all source breakpoints and all function breakpoints. |
| `list_breakpoints` | `()` | All *source* breakpoints with verified state and line (function breakpoints: `list_function_breakpoints`). |
| `set_exception_breakpoints` | `(filters)` | e.g. `["user-unhandled"]`, `["all"]`; empty to disable. |
| `continue` | `(threadId?, timeoutMs=30000)` | Resume, block until next stop. |
| `step_over` / `step_into` / `step_out` | `(threadId?, timeoutMs=30000)` | Step, block until stopped. |
| `pause` | `(threadId?)` | Break into a running target. Best-effort: netcoredbg cannot always pause a freely-running process. |

## Introspection (when stopped)

| Tool | Signature | Notes |
|---|---|---|
| `get_threads` | `()` | Threads of the stopped target. |
| `get_callstack` | `(threadId?, startFrame=0, levels=20)` | Async-aware frames; use `frameId` for scopes. |
| `get_scopes` | `(frameId)` | `{ name, variablesReference }` per scope. |
| `get_variables` | `(variablesReference)` | Expand a scope or a parent variable; walk references for nested objects. |
| `evaluate` | `(expression, frameId?, context?)` | ⚠️ **Executes code in the debuggee** (getters, methods) — can mutate state. Prefer `get_variables` for plain inspection. See [security.md](security.md). |
| `get_exception_info` | `(threadId?)` | Type/message/details when stopped on an exception; null otherwise. |

## Async / Tasks (ClrMD, when stopped)

The differentiating capability — the "Tasks window" DAP cannot provide.

| Tool | Signature | Notes |
|---|---|---|
| `list_tasks` | `(status?)` | All managed `Task`s with status and async method. Optional status filter (e.g. `WaitingForActivation`, `Faulted`). |
| `get_task` | `(address)` | Inspect one Task by heap address (hex `0x…` or decimal). |
| `get_async_graph` | `()` | The await/continuation graph: for each async Task, what it awaits and what it continues into. |
| `get_async_callstack` | `(address)` | Follow continuations from a Task to the logical async call stack. |

## Typical agent loop

1. `debug_launch` (or `list_processes` → `debug_attach`; or `debug_test`).
2. `set_breakpoint(file, content=…)` at the suspect line.
3. `continue` → returns `stopped at File.cs:NN`.
4. `get_callstack` → `get_scopes` → `get_variables` to read state.
5. `list_tasks` / `get_async_graph` to see pending/stuck async work.
6. `step_*` / `continue` to narrow it down → diagnose. `debug_stop` when done.
