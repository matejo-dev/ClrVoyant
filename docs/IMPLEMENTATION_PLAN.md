# ClrVoyant — Implementation Plan

> **Historical.** This is the original phased plan (the project was built from it).
> For the architecture **as built** see [architecture.md](architecture.md); for the
> decisions and their rationale see the [ADRs](adr/README.md). All phases here are
> done, plus later work (cross-OS, HTTP/POD, `debug_test`, `list_processes`).

A local MCP server that lets an agent deterministically debug .NET applications:
launch, set breakpoints, step, inspect variables / call stack / threads, and
enumerate all async `Task`s — "everything a developer looks at while debugging".

Status: architecture validated by the spike under [`/spike`](../spike). This
document is the plan to turn that spike into a real server.

---

## 1. Scope and non-goals

**In scope**
- Target runtime: **.NET 8 / 9 / 10** (modern .NET only).
- Headless operation: no IDE required.
- Deterministic introspection over a request/response tool surface.
- Full async/Task introspection (the "Tasks window" equivalent).
- **Multiple concurrent debug sessions** — debug several processes at once, each
  with its own engine, breakpoints, and stop state.

**Out of scope (for now)**
- .NET Framework (dropped — neither netcoredbg nor a clean headless path covers it).
- Visible IDE automation (EnvDTE / VSIX / VS Code extension).
- Remote-machine / container debugging (possible later; design stays attach-friendly).
- **Suspend-at-startup auto-attach of child processes** — catching children before
  they run. Future dedicated phase; tiers documented in §8 (Phase 7). The
  multi-session core and manual `debug_attach` ship now.

---

## 2. Architecture

A single .NET process (the MCP server) orchestrates **two engines** against one
target process, behind **one IDE-neutral tool contract**. The agent never sees
which engine answered.

```
            ┌────────────────────────── MCP server (stdio) ──────────────────────────┐
            │                                                                          │
 agent ───► │  Tool layer (contract)  ──►  SessionManager  ──►  DapClient  ──► netcoredbg ──► target
 (Claude)   │                                     │                                      ▲ (.NET proc)
            │                                     └──────────►  ClrMdInspector ──────────┘ (snapshot at stop)
            └──────────────────────────────────────────────────────────────────────────┘
```

- **DapClient → netcoredbg** (Samsung, MIT, DAP): control + live introspection
  (breakpoints, step, threads, call stack, variables, evaluate). Event-driven:
  the `stopped` event is what makes the wait-based model deterministic.
- **ClrMdInspector** (`Microsoft.Diagnostics.Runtime`): when the target is
  stopped, snapshot it and enumerate `Task` objects with state and the
  async-state-machine linkage. Coexistence with netcoredbg is proven.

### The wait-based execution model (the core design decision)

MCP tools are synchronous request/response; debugging is asynchronous. We bridge
this by making execution tools **block until the next stop**:

- `continue` / `step_*` send the DAP request, then `await` the `stopped` event
  (or process `exited`) with a timeout, and return the resulting state
  (`file:line`, reason, threadId). The agent sees a clean loop:
  `continue → "stopped at Foo.cs:42" → inspect → continue`.
- Introspection tools are only valid while `stopped`; they fail fast with a clear
  message otherwise.

---

## 3. Tech stack

| Concern | Choice |
|---|---|
| Language / runtime | C# / .NET (server targets net8.0; can read net8/9/10 targets) |
| MCP framework | `ModelContextProtocol` (official C# SDK), **stdio** transport |
| Debug engine | `netcoredbg` (bundled under `tools/`), DAP via `--interpreter=vscode` |
| Heap inspection | `Microsoft.Diagnostics.Runtime` (ClrMD) 3.x |
| JSON | `System.Text.Json` |

netcoredbg is bundled with the server (per-RID) rather than required on PATH.

---

## 4. Tool contract (the durable artifact)

Names are semantic and engine-neutral. All tools return structured JSON. Tools
that require a stop return a uniform error when the target is running/exited.

**Multi-session:** every tool below accepts an optional `sessionId`. If omitted,
it targets the *active* session (the only one, or the most-recently-stopped). The
server supports many concurrent sessions, each with its own engine and state.

### Session / lifecycle
- `debug_launch(program, args?, cwd?, stopAtEntry?)` — launch a built `.dll` (or
  resolve from a project; build is the agent's responsibility). Returns
  `{ sessionId, pid, state }`.
- `debug_attach(pid)` — attach to an already-running process; returns a new
  `sessionId`. Manual attach is the Phase 1 path for secondary/child processes.
- `debug_stop(sessionId?)` — terminate the debuggee and end that session.
- `debug_status(sessionId?)` — `{ state: starting|running|stopped|exited, pid, stop?: {file,line,reason,threadId} }`.
- `list_sessions()` — `[{ sessionId, pid, state, stop? }]` for all sessions.
- `wait_for_any_stop(timeoutMs?)` — block until *any* session hits a stop;
  returns `{ sessionId, file, line, reason, threadId }`. Essential when several
  processes run asynchronously and the agent must learn which one broke.

### Breakpoints
- `set_breakpoint(file, line, condition?, hitCondition?, logMessage?)` → `{ bpId, verified, line }`.
- `remove_breakpoint(bpId)`
- `list_breakpoints()`
- `set_exception_breakpoints(filters)` — e.g. `["user-unhandled"]`, `["all"]`.

### Execution control (wait-based; block until next stop or timeout)
- `continue(timeoutMs?)` → `{ state, file?, line?, reason?, threadId? }`
- `step_over(threadId?, timeoutMs?)`
- `step_into(threadId?, timeoutMs?)`
- `step_out(threadId?, timeoutMs?)`
- `pause()` — break into a running target.

### Introspection (valid only when stopped)
- `get_threads()` → `[{ id, name, state }]`
- `get_callstack(threadId?, startFrame?, levels?)` → async-aware frames
  `[{ frameId, function, file, line }]`
- `get_scopes(frameId)` → `[{ name, variablesReference }]`
- `get_variables(variablesReference)` → `[{ name, value, type, variablesReference }]`;
  pass a child's `variablesReference` to expand a complex object (this expansion was
  folded into `get_variables` as built — there is no separate `get_variable` tool).
- `evaluate(expression, frameId?, context?)` — **⚠️ DANGEROUS: evaluating an
  expression executes code in the debuggee (property getters, method calls) and
  can mutate state or cause side effects.** This warning is in the tool
  description the agent reads.
- `get_exception_info(threadId)` — exception type, message, stack when stopped on
  an exception.

### Async / Tasks (ClrMD-backed snapshot at the current stop)
- `list_tasks(filter?)` → `[{ taskId, address, status, asyncMethod?, awaitingOn? }]`
  — the full Tasks-window equivalent.
- `get_task(address)` → details + continuation chain.
- `get_async_callstack(threadId?)` — logical await stack for the stopped thread.
- `get_async_graph()` — reconstructed await/continuation tree. *(Long pole, Phase 5.)*

---

## 5. Internal components

- **`ClrVoyant.Core`** — models, the tool-contract DTOs, the `IDebugEngine`
  abstraction (so the engine is swappable and testable), `Session` (one debugged
  process), and `SessionManager` (owns the dictionary of concurrent sessions,
  resolves the active session, enforces state preconditions, and fans in stop
  events for `wait_for_any_stop`).
- **`ClrVoyant.Dap`** — hardened `DapClient` (grown from the spike): framing,
  request/response correlation, event pump, `stopped`/`exited`/`breakpoint`
  events, reconnect/teardown.
- **`ClrVoyant.Inspection`** — `ClrMdInspector`: snapshot lifecycle, Task
  enumeration, status decoding, async-state-machine walking, graph reconstruction.
- **`ClrVoyant.Server`** — MCP host: registers tools (`[McpServerTool]`), stdio
  transport, wires tools to `SessionManager`.
- **`tools/netcoredbg`** — bundled engine.

### Two identity spaces (a real subtlety)
DAP uses `variablesReference`/`frameId` handles that are **invalidated on every
continue**. ClrMD uses heap addresses from a snapshot. These are different
identity spaces over the same objects. Rule: DAP handles are valid only within
the current stop; ClrMD addresses are valid only within the snapshot taken at
that stop. Each handle is tagged with `(sessionId, stopId)`; `SessionManager`
rejects stale or cross-session handles with a clear error.

---

## 6. Async-graph reconstruction (the long pole)

Confirmed feasible by the spike: each `Task`'s `m_continuationObject` points at
the `AsyncStateMachineBox<...>` whose generic argument names the source async
method (e.g. `<AwaitForever>d__2`). Reconstruction plan:
1. Enumerate all `Task`s + their `m_stateFlags` (status) and `m_continuationObject`.
2. For `AsyncStateMachineBox<T>`, read the boxed state machine fields to find what
   it is awaiting (the `TaskAwaiter`/`m_task` it is parked on).
3. Build edges `awaiter → awaited` to form the await tree; annotate each node with
   status and source method.
4. Handle fan-out continuation lists (`List<object>` / `ContinuationWrapper`).

This is parsing work over known runtime internals, not a feasibility risk. It is
the most expensive single feature and is isolated in Phase 5.

---

## 7. Project structure

```
ClrVoyant/
  docs/IMPLEMENTATION_PLAN.md      ← this file
  spike/                            ← validated proof-of-concept (kept as reference)
  src/
    ClrVoyant.Core/
    ClrVoyant.Dap/
    ClrVoyant.Inspection/
    ClrVoyant.Server/
  tools/netcoredbg/                 ← bundled engine (per RID)
  tests/
    ClrVoyant.Tests/                 ← unit + integration (drive a sample target end-to-end)
  samples/SampleApp/                ← a richer target for tests/demos
  ClrVoyant.slnx
```

---

## 8. Phasing

- **Phase 0 — Scaffolding.** Solution, projects, bundle netcoredbg, MCP stdio host
  that lists tools. Exit: agent can connect and see the tool list.
- **Phase 1 — Multi-session lifecycle.** `IDebugEngine` + `DapEngine` (porting the
  spike's `DapClient`), `Session`, `SessionManager`; tools `debug_launch`,
  `debug_attach`, `debug_stop`, `debug_status`, `list_sessions`,
  `wait_for_any_stop`. Built multi-session from the start. Exit: launch several
  targets concurrently and cleanly terminate each.
- **Phase 2 — Breakpoints + wait-based control.** `set_breakpoint`, `continue`,
  `step_*`, `pause`. Exit: deterministic break/continue loop.
- **Phase 3 — Live introspection.** `get_threads`, `get_callstack`, `get_scopes`,
  `get_variables`, `evaluate`, exceptions. Exit: full DAP-side developer view.
- **Phase 4 — Tasks enumeration.** `list_tasks`, `get_task` via ClrMD snapshot at
  stop. Exit: full Task list with status.
- **Phase 5 — Async graph.** `get_async_callstack`, `get_async_graph`. Exit: the
  await tree.
- **Phase 6 — Robustness & packaging.** Concurrency guards, timeouts, lifecycle
  edge cases, README, distribution. Exit: usable by a real agent.
- **Phase 7 — Child-process attach.** Tier 1 (manual `debug_attach`) shipped in
  Phase 1. **Tier 2 (auto-discovery) DONE**: `ChildProcessWatcher` (a hosted
  service) polls Win32 processes, finds .NET children of debugged processes by
  parent PID, and attaches them as sessions; opt-in via `set_auto_attach`. Tier 3
  (suspend-at-startup via `DOTNET_DefaultDiagnosticPortSuspend` so breakpoints can
  be set before a child runs) remains available if "debug from start" is needed.

Each phase is covered by an integration test driving `samples/SampleApp`.

---

## 9. Risks & open questions

- **netcoredbg `evaluate` is weaker than vsdbg** on complex expressions/framework
  types. Mitigation: fall back to ClrMD reads for object state when `evaluate`
  fails; if it proves too weak overall, the documented fallback is a VS Code +
  vsdbg host (rejected for now). To be measured in Phase 3.
- **ClrMD vs .NET 10 targets.** Spike validated a net8 target; verify ClrMD reads
  net9/net10 targets (bump ClrMD version if needed). Early check in Phase 4.
- **Snapshot cost & staleness.** One PSS snapshot per stop, lazily on first Task
  query, discarded on continue. Watch latency on large heaps; consider caching
  within a single stop.
- **Multi-session bookkeeping.** Built in from Phase 1. Watch the wait model:
  `continue(sessionId)` waits for *that* session; `wait_for_any_stop` fans in all
  sessions. Stop events arriving for a non-awaited session are buffered on the
  `Session` so a later `debug_status` still reports them.
- **Stale-handle safety.** Enforced via `(sessionId, stopId)` tagging (§5).
- **Build responsibility.** `debug_launch` expects a built `.dll`; the agent
  builds via its own tooling. Revisit if a `build=true` convenience is wanted.

---

## 10. Immediate next step

Phase 0 + Phase 1: scaffold the solution, bundle netcoredbg, stand up the MCP
stdio host, and port the spike's `DapClient` into `ClrVoyant.Dap` behind
`debug_launch` / `debug_status` / `debug_stop`.
