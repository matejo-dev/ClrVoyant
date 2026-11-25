# Tool reference

The complete MCP tool surface: **35 tools**. The source of truth is the code under
[`src/ClrVoyant.Server/Tools/`](../src/ClrVoyant.Server/Tools/) (one class per
concern); the `[Description]` of each tool and parameter is what an agent actually
reads. This page groups the tools and spells out their semantics. The same playbook
the agent gets from `get_debug_instructions` is also sent as the MCP server
instructions in the `initialize` handshake.

## Conventions

- **Session-scoped tools** take an optional `sessionId`. Omitted, it targets the
  *active* session: the only one, or the most recently stopped. If several sessions
  exist and none is active, the call fails and lists the ids to choose from.
- **Execution tools** (`continue`, `step_*`) are wait-based: they block until the next
  stop, the process exit, a lost debugger connection, or the timeout, and return the
  new state ([ADR-0004](adr/0004-wait-based-execution-model.md)).
- **Introspection tools** need the target to be **stopped** and fail immediately
  otherwise (or, for a session whose debugger connection was lost, with a message
  saying so).
- **Results** are JSON text. Property names are camelCase, enums are words
  (`"Stopped"`, not a number), and `null` properties are omitted. A few tools return a
  plain string. No tool advertises an output schema.
- **Hints.** Every tool declares an MCP title and the read-only / destructive /
  idempotent hints, and `openWorld=false` (a test keeps new tools from being added
  without deciding them). The tables below mark read-only tools `RO`; for the others,
  `D` means destructive and `I` idempotent. A tool marked neither is non-destructive
  and non-idempotent. The hints are advisory for the client.
- **Cancellation.** Every asynchronous tool honours a client's cancellation of the
  call. For `continue`/`step_*`/`wait_for_any_stop` it releases the wait and leaves
  the session usable: the next stop is then reported as a spontaneous one (see
  `wait_for_any_stop`). Heap reads (`list_tasks` and friends) stop walking.

## Errors

A tool that fails returns an MCP error result (`isError: true`) whose only content is
the actionable message, for example `Session 's1' is 'Running', not stopped. This
operation requires a stopped target.` or a message naming the `sessionId` to pass.
This covers the exception kinds the server throws on purpose to explain a condition
(invalid operation, bad argument, unknown session, timeout, I/O, debugger-protocol
errors). Anything else is a probable bug: the message is prefixed with the exception
type (`Unexpected NullReferenceException: ...`) and the stack trace goes to the
server log only. Client cancellation and JSON-RPC protocol errors (such as invalid
parameters) are not turned into error results. See
[ADR-0012](adr/0012-tool-errors-as-results.md).

## Paging

`list_tasks`, `get_async_graph`, `get_variables` and `list_methods` return a **page**
instead of a bare array:

```json
{ "items": [ ... ], "total": 412, "truncated": true, "nextOffset": 100 }
```

| Field | Meaning |
|---|---|
| `items` | At most `max` entries starting at `offset`. |
| `total` | Number of matching entries, when it can be counted. Omitted for `get_variables` (the debugger does not report it). |
| `truncated` | `true` when more entries exist beyond this page. |
| `nextOffset` | The `offset` to pass for the next page; omitted on the last page. `truncated` is exactly "`nextOffset` is present". |

Parameters: `max` (1-1000; default 100, except `list_methods`: 200) and `offset`
(0 or greater, default 0). Values out of range are an error result. To narrow a
result instead of paging, use the filters (`status`, `typeFilter`, `methodFilter`).
See [ADR-0013](adr/0013-bounded-paged-tool-results.md).

## Onboarding

| Tool | Parameters | Returns |
|---|---|---|
| `get_debug_instructions` `RO` | none | The playbook as text: typical loop, tool order, gotchas. |

## Sessions and lifecycle

| Tool | Parameters (default) | Returns / semantics |
|---|---|---|
| `debug_launch` | `program` (path to a built `.dll`), `args?` (string[]), `cwd?` (default: the program's folder), `stopAtEntry` (false), `env?` (name to value map) | `SessionInfo`. `env` is added to, and overrides, the environment inherited from the server, and is kept across `restart_debugging`. A name that is empty or contains `=` is rejected. A failed launch leaves no session behind. |
| `debug_attach` | `pid` | `SessionInfo`. Attaches to a running .NET process. A failed attach leaves no session and never kills the process it tried to attach to. |
| `list_processes` `RO` | `dotnetOnly` (true) | Array of `{ pid, parentPid, name, isDotNet, commandLine? }`. `commandLine` is cut at 1000 characters (`...` marks the cut) and absent when the OS does not let the server read it. **Command lines can contain secrets** passed as arguments. On Linux `name` comes from `/proc` and is at most 15 characters. |
| `debug_test` | `testProject` (a `.csproj` or its directory), `testName?` (passed to `dotnet test --filter`), `configuration` ("Debug"), `timeoutMs` (120000) | `SessionInfo` of the attached test host. Runs `dotnet test` with `VSTEST_HOST_DEBUG=1`, reads the host's PID, attaches. Fails at once, with the exit code and the end of the output, if `dotnet test` exits first (build error, no matching test); fails with a timeout message if no PID appears in time. The `dotnet test` process tree is ended with the session. |
| `restart_debugging` `D` | `sessionId?` | `SessionInfo`. Terminates the debuggee and relaunches it with the same program, arguments and `env`; keeps the session id and all breakpoints. Only for sessions created by `debug_launch`. If the relaunch fails, the session is left `Exited` with its breakpoints, and the error says so. |
| `set_auto_attach` `I` | `enabled` | `"auto-attach enabled"` or `"auto-attach disabled"`. Off by default. See [Auto-attach](#auto-attach). |
| `debug_status` `RO` | `sessionId?` | `SessionInfo`. |
| `list_sessions` `RO` | none | Array of `SessionInfo`. |
| `debug_stop` `D` | `sessionId?`, `terminate?` (bool) | `"stopped"`. See [Ownership](#ownership-what-debug_stop-ends). |
| `get_output` `RO` | `sessionId?`, `maxChars` (10000, at least 1) | `{ text, truncated, state, exitCode? }`: the most recent stdout/stderr of the debuggee (a bounded capture of 64 KiB; the oldest part is dropped). `truncated` is true when earlier output is not included, whether dropped by the cap or cut by `maxChars`. Works in any state. For an attached process, output is captured only if the debugger relays it. |

`SessionInfo` is `{ sessionId, pid, state, stop?, exitCode? }`. `state` is one of
`Starting`, `Running`, `Stopped`, `Exited`, `Faulted`. `stop` is
`{ file?, line?, reason?, threadId? }` and is present while stopped. `exitCode` is
present once the debuggee has exited and the debugger reported it.

### Ownership: what `debug_stop` ends

By default `debug_stop` **kills** the debuggee only if this server started it:

- a process started by `debug_launch`;
- the test host behind `debug_test` (the `dotnet test` run is always ended with the session);
- a child process auto-attached from a session of one of the above (ownership is inherited, also by grandchildren).

A process the session merely **attached** to with `debug_attach`, or an auto-attached
child of such a session, is **detached** and keeps running. `terminate` overrides the
default either way: `true` kills even an attached process; `false` detaches and
leaves it running (for a `debug_test` session the `dotnet test` run still ends, and a
launched process whose console output goes through the debugger may fail on its next
write). The session is removed first and always released, even if the debugger fails
to end cleanly; if the active session ends and exactly one remains, that one becomes
active.

### Auto-attach

With `set_auto_attach(true)`, a background scan (about every 750 ms) attaches .NET
processes that are children of a debugged process, as new sessions. A process is
adopted only if its parent PID matches a live debugged process **and it was created
after that parent** (a recycled PID would otherwise make unrelated processes look
like children); when the creation time is unknown the process is left alone
(`debug_attach` still works). An attach that fails is retried at most 3 times, then
the process is left alone. No suspend-at-startup: a child's first instants may run
before it is attached.

## Breakpoints

All of these work while the target runs or is stopped.

| Tool | Parameters (default) | Returns / semantics |
|---|---|---|
| `set_breakpoint` | `file` (absolute path), `line` (0), `content?`, `condition?`, `hitCondition?`, `logMessage?`, `sessionId?` | `{ id, verified, file, line }`. Prefer `content` (the source text of the line): the line is found by matching it in the file, and `line` becomes an optional hint that only picks between several matches. Without `content`, `line` (1-based) is required. `logMessage` makes a logpoint. |
| `set_function_breakpoint` | `functionName` (`Method`, `Type.Method` or `Namespace.Type.Method`), `condition?`, `hitCondition?`, `sessionId?` | `{ id, verified, functionName, file?, line? }`. Binds from the PDB without source: the no-source path. `file`/`line` are present when symbols map one. |
| `list_breakpoints` `RO` | `sessionId?` | Array of source breakpoints `{ id, verified, file, line }`. |
| `list_function_breakpoints` `RO` | `sessionId?` | Array of function breakpoints. |
| `remove_breakpoint` `I` | `bpId`, `sessionId?` | `true` if it existed (source or function breakpoint), else `false`. |
| `clear_all_breakpoints` `I` | `sessionId?` | `"cleared N breakpoint(s)"` (source and function breakpoints). |
| `set_exception_breakpoints` `I` | `filters` (string[]), `sessionId?` | `"ok"`. Filters such as `all` or `user-unhandled`; an empty list disables them. |
| `list_methods` `RO` | `assemblyPath`, `typeFilter?`, `methodFilter?`, `max` (200), `offset` (0) | A [page](#paging) of `{ fullName, declaringType, method }`, read statically from the assembly metadata (no process needed). Filters are case-insensitive substrings. Compiler-generated members are omitted. Pass a `fullName` to `set_function_breakpoint`. |

Breakpoint ids are assigned by the server, are shared by source and function
breakpoints, and stay stable across `restart_debugging`.

## Execution

| Tool | Parameters (default) | Returns / semantics |
|---|---|---|
| `continue` | `threadId?`, `timeoutMs` (30000), `sessionId?` | `StopOutcome`. |
| `step_over`, `step_into`, `step_out` | `threadId?`, `timeoutMs` (30000), `sessionId?` | `StopOutcome`. The thread defaults to the one of the last stop; with no stop yet, `threadId` is required. |
| `pause` `I` | `threadId?`, `sessionId?` | `"pausing"`. Best-effort: netcoredbg cannot always break into a freely running process. |
| `wait_for_any_stop` | `timeoutMs` (30000) | See below. |

`StopOutcome` is `{ state, stop?, exitCode?, message? }`:

| `state` | Meaning |
|---|---|
| `Stopped` | `stop` holds file, line, reason and thread id. |
| `Running` | The timeout passed; the target keeps running. A timeout is a result, not an error. |
| `Exited` | The process ended; `exitCode` when the debugger reported one. |
| `Faulted` | The debugger connection was lost (adapter died). `message` says so; the target's state is unknown. End the session with `debug_stop` and start a new one. |

Only **one resume may be in flight per session**: a second `continue`/`step_*` while
one is waiting is rejected with an error result (queueing it would silently resume the
target again at the first one's stop). A resume on an `Exited` or `Faulted` session is
also an error. A resume the client cancels is neither a stop nor a timeout; the next
real stop is reported as spontaneous.

### `wait_for_any_stop`

Blocks until **any** session has an event nobody was waiting for, then reports it:

- a stop (breakpoint, step, exception, `stopAtEntry`, a `pause`), `state: "Stopped"`;
- the process exiting, `state: "Exited"`, with `exitCode` when known;
- the debugger connection being lost, `state: "Faulted"`, with `message`.

The result is `{ sessionId, state, stop?, exitCode?, message? }`, or
`{ "timedOut": true }` when the timeout passes first. A session that stops
spontaneously becomes the active one. Events that are no longer true when read are
**skipped** (the session was resumed, stopped again, restarted or ended since), so a
result always describes the current state of that session. A stop consumed by a
`continue`/`step_*` that was waiting is never queued here. The tool is not read-only:
a reported event is consumed, so asking again does not return it again.

## Introspection (target stopped)

| Tool | Parameters (default) | Returns / semantics |
|---|---|---|
| `get_threads` `RO` | `sessionId?` | Array of `{ id, name }`. |
| `get_callstack` `RO` | `threadId?`, `startFrame` (0), `levels` (20), `sessionId?` | Array of `{ id, function, file?, line? }`; use `id` as `frameId`. |
| `get_scopes` `RO` | `frameId`, `sessionId?` | Array of `{ name, variablesReference }`. |
| `get_variables` `RO` | `variablesReference`, `max` (100), `offset` (0), `sessionId?` | A [page](#paging) of `{ name, value, type, variablesReference }`; walk `variablesReference` to expand nested objects. The page is requested from the debugger itself, so a huge collection costs one bounded request; `total` is absent. |
| `evaluate` `D` | `expression`, `frameId?` (top frame), `context?` (`repl` when omitted; also `watch`, `hover`), `sessionId?` | `{ result, type, variablesReference }`. **Executes code in the debuggee** (getters, method calls) and can change state. Prefer `get_variables` for plain inspection. See [security.md](security.md). |
| `get_exception_info` `RO` | `threadId?`, `sessionId?` | `{ typeName, description, details? }` when stopped on an exception; `null` otherwise. |

## Async and Tasks (ClrMD, target stopped)

Read from the target's heap at the current stop. Addresses are valid for that stop
only; the heap is read once per stop and shared by these tools. The walks run off the
request thread and stop when the call is cancelled. A process without a .NET runtime
fails with an actionable message.

| Tool | Parameters (default) | Returns / semantics |
|---|---|---|
| `list_tasks` `RO` | `status?` (case-insensitive, e.g. `WaitingForActivation`, `Faulted`), `max` (100), `offset` (0), `sessionId?` | A [page](#paging) of `{ taskId, address, status, typeName, asyncMethod?, continuation? }`. `total` counts every match of the filter. |
| `get_task` `RO` | `address` (hex `0x...` or decimal), `sessionId?` | One task as above, or `null` if the address is not a Task. A malformed address is an error result. |
| `get_async_graph` `RO` | `max` (100), `offset` (0), `sessionId?` | A [page](#paging) of `{ address, status, asyncMethod?, typeName, awaiting[], continuationAddress? }`: for each async Task, the Tasks it is parked on and the Task it continues into. |
| `get_async_callstack` `RO` | `address`, `sessionId?` | Array of the same nodes: the continuation chain starting at the given Task. Not paged. |

## Typical loop

1. `debug_launch` (or `list_processes`, then `debug_attach`; or `debug_test`).
2. `set_breakpoint(file, content=...)` at the suspect line.
3. `continue` returns the stop location.
4. `get_callstack`, `get_scopes`, `get_variables` to read state.
5. `list_tasks` / `get_async_graph` to see pending or stuck async work.
6. `step_*` / `continue` to narrow it down; `get_output` for what the process printed;
   `debug_stop` when done.
