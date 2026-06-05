# ClrVoyant

[![CI](https://github.com/matejo-dev/ClrVoyant/actions/workflows/ci.yml/badge.svg)](https://github.com/matejo-dev/ClrVoyant/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![.NET 8/9/10](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4)

A local **MCP server that lets an agent (e.g. Claude) debug .NET applications**:
launch a program, set breakpoints, step, inspect variables / call stacks /
threads, and enumerate all in-flight async `Task`s — everything a developer looks
at while debugging — deterministically, headless, with no IDE.

Style is the same as the Azure DevOps / Azure MCP servers: run it locally, point
your agent at it, and ask in natural language:

> "Launch project X, set a breakpoint where the bug is, run it, read the
> variables and the async tasks, and tell me why ABC happens."

The agent decides *where* to break and *what* to inspect (it reads your source
with its own tools); ClrVoyant gives it the deterministic debugger primitives.

## How it works

A single .NET process orchestrates **two engines** behind one IDE-neutral tool
contract:

- **[netcoredbg](https://github.com/Samsung/netcoredbg)** (bundled, MIT) over the
  **Debug Adapter Protocol** — control + live introspection (breakpoints, step,
  threads, call stack, variables, evaluate).
- **[ClrMD](https://github.com/microsoft/clrmd)** — reads the heap at a stop to
  enumerate all `Task`s and reconstruct the async await/continuation graph (the
  "Tasks window" equivalent, which DAP cannot provide). On Windows via a PSS
  snapshot; on Linux via a passive read of the (netcoredbg-held) process — both
  validated to coexist with netcoredbg holding the process.

See [docs/architecture.md](docs/architecture.md) for the design as built, and
[docs/](docs/README.md) for the full documentation (tool reference, POD/remote
debugging, security model, ADRs).

## What you can do with it

Concrete scenarios it delivers today (Windows or Linux, .NET 8/9/10):

- **Debug an app you launch** — start a program, break, step, read
  variables / call stack / threads. The base loop. ([tools](docs/tools.md))
- **Attach to a running process** — discover the PID (`list_processes`, filters
  to .NET) and attach; for long-running or externally-started processes.
- **Debug a unit test in one step** — `debug_test(project[, testName])` launches
  the test host suspended, attaches, and breaks in your test/production code.
- **Understand stuck `async` code** — enumerate every in-flight `Task` with its
  state and the await/continuation graph (`list_tasks`, `get_async_graph`). This
  is the "Tasks window" that DAP-only debuggers structurally can't provide.
- **Debug several processes at once** — multi-session, with `wait_for_any_stop`
  to orchestrate stops that arrive asynchronously; optional child-process
  auto-attach for parents that spawn workers.
- **Debug without source** — only have a deployed build's DLLs + PDBs, not the
  `.cs`? `list_methods` reads the assembly metadata to find method names, then
  `set_function_breakpoint("Namespace.Type.Method")` binds straight from the PDB —
  no source file needed. Attach/call stack/variables/Tasks all work the same.
- **Debug inside a container / Kubernetes POD** — run it as an authenticated
  HTTP sidecar co-located with the target (PDBs required); only the agent is
  remote, the engine stays local to the process.
  ([remote debugging](docs/remote-debugging-pod.md))

## Requirements

- **Supported architectures**: `win-x64`, `linux-x64`, `linux-arm64`. The engine
  debugs locally, so **the tool runs at the same architecture as the target** — an
  arm64 tool for an arm64 process. (No `win-arm64` or macOS: netcoredbg ships no
  such build.) Each architecture is exercised by its own CI leg, including an
  `arm64` runner on real hardware — see [ADR-0011](docs/adr/0011-supported-architectures.md).
- **.NET SDK 8 / 9 / 10** (the target apps you debug must be .NET 8+; .NET
  Framework is not supported)

## Install

The cleanest way is as a **.NET global tool** — you get a `clrvoyant` command on
your PATH, so client config never points at a build folder. The published package
**bundles netcoredbg for every supported RID** (pinned + SHA-256 verified at build
time) and the tool picks the one matching your host, so it installs and runs
**offline** with no runtime download — only a missing-bundle fallback fetches.

```pwsh
# From a published package (once it's on NuGet):
dotnet tool install -g ClrVoyant

# Or from source right now:
dotnet pack src/ClrVoyant.Server -c Release -o ./nupkg
dotnet tool install -g ClrVoyant --add-source ./nupkg
```

Then point your MCP client at the command (`.mcp.json` / Claude config; VS Code's
`mcp.json` uses the key `servers`):

```json
{
  "mcpServers": {
    "clrvoyant": { "command": "clrvoyant" }
  }
}
```

The install scripts do build + register in one step:

```pwsh
./scripts/install.ps1 -Client cursor     # claude-code | claude-desktop | cursor | windsurf | vscode
./scripts/install.ps1                     # default: just prints the snippet
```
```sh
./scripts/install.sh --client cursor      # bash equivalent (uses jq to merge)
```

Prefer not to install a tool? Build and point at the binary directly:

```pwsh
dotnet build ClrVoyant.slnx -c Release     # bundles netcoredbg next to the server
```

and set `"command"` to the built `ClrVoyant.Server` executable. To use your own
netcoredbg, set `CLRVOYANT_NETCOREDBG`; to forbid the first-run download (air-gapped),
set `CLRVOYANT_NO_FETCH=1` and pre-provide the engine.

For debugging a .NET app in **Kubernetes**, run ClrVoyant as a sidecar over HTTP —
see [deploy/](deploy/README.md) and [docs/remote-debugging-pod.md](docs/remote-debugging-pod.md).

Tip: have the agent call `get_debug_instructions` first — it returns a short
playbook of the tools and their ordering.

## Transports

- **stdio** (default) — the agent spawns the server locally; one client per process.
- **HTTP** — set `CLRVOYANT_TRANSPORT=http` for a Streamable-HTTP server a remote
  agent can reach (e.g. shipped inside a POD next to the app). HTTP **requires**
  `CLRVOYANT_AUTH_TOKEN` (a bearer token) and binds `CLRVOYANT_HTTP_URL`
  (default `http://0.0.0.0:3001`); it **fails closed** without a token, because a
  debug server that can launch processes and `evaluate` code is an RCE surface.

For debugging a .NET app running in **Kubernetes**, run ClrVoyant as a sidecar:
see [deploy/](deploy/) (Dockerfile + sidecar manifest + the security model).

## Tools

**Onboarding**
- `get_debug_instructions()` — a short playbook (typical loop, tool ordering,
  gotchas); call it first

**Sessions** (multi-session: every tool takes an optional `sessionId`; omit for
the active one)
- `debug_launch(program, args?, cwd?, stopAtEntry?)` — launch a built `.dll`
- `debug_attach(pid)` — attach to a running process (e.g. a child process)
- `list_processes(dotnetOnly?)` — list processes (pid, parent, name, isDotNet) to
  find a target to attach (e.g. the app process in a shared-PID-namespace POD)
- `debug_test(testProject, testName?, configuration?)` — run `dotnet test` with the
  host suspended and attach in one step (the `VSTEST_HOST_DEBUG` recipe, automated)
- `restart_debugging(sessionId?)` — relaunch a launched session in place: same
  id, same args, breakpoints preserved (not valid for attached sessions)
- `set_auto_attach(enabled)` — auto-attach .NET **child** processes of debugged
  processes (off by default; matched by parent PID, no suspend-at-startup)
- `debug_stop(sessionId?)`, `debug_status(sessionId?)`, `list_sessions()`
- `wait_for_any_stop(timeoutMs?)` — block until *any* session breaks (key for
  several processes running asynchronously)

**Breakpoints & execution** (wait-based: `continue`/`step_*` block until the next
stop and return the new location)
- `set_breakpoint(file, line?, content?, condition?, hitCondition?, logMessage?)` —
  prefer `content` (the source text of the line) over a raw `line`: it survives
  line-number drift; `line` then only disambiguates duplicate matches
- `set_function_breakpoint(functionName, condition?, hitCondition?)` — break by
  method name (`Method` / `Type.Method` / `Namespace.Type.Method`), no source line;
  binds from the PDB, so it's the **no-source** path. `list_function_breakpoints()`
- `list_methods(assemblyPath, typeFilter?, methodFilter?)` — discover method names
  in a built `.dll` (static metadata read, no process) to feed `set_function_breakpoint`
- `remove_breakpoint(bpId)`, `clear_all_breakpoints()`, `list_breakpoints()`,
  `set_exception_breakpoints(filters)`
- `continue(threadId?, timeoutMs?)`, `step_over` / `step_into` / `step_out`, `pause()`

**Introspection** (when stopped)
- `get_threads()`, `get_callstack(threadId?, ...)`
- `get_scopes(frameId)`, `get_variables(variablesReference)`
- `evaluate(expression, frameId?, context?)` — ⚠️ **executes code in the
  debuggee** (side effects); prefer `get_variables` for plain inspection
- `get_exception_info(threadId?)`

**Async / Tasks** (ClrMD, when stopped)
- `list_tasks(status?)` — all Tasks with status and async method
- `get_task(address)`
- `get_async_graph()` — await/continuation graph
- `get_async_callstack(address)` — logical async chain from a Task

## Typical agent loop

1. `debug_launch` the app → `set_breakpoint` at the suspect line
2. `continue` → returns `stopped at File.cs:NN`
3. `get_callstack` → `get_scopes` → `get_variables` to read state
4. `list_tasks` / `get_async_graph` to see what async work is pending/stuck
5. `step_over` / `continue` to narrow it down → diagnose

## Debugging tests

Tests run in a `testhost` process. Use `debug_test(testProject, testName?)` — it
runs `dotnet test` with `VSTEST_HOST_DEBUG=1` (so the host suspends and announces
its PID), parses that PID and attaches in one step; set breakpoints right after it
returns and execution resumes into them. The `dotnet test` driver is killed when
you `debug_stop` the session. Build the tests in **Debug** for symbols and locals.

Under the hood this automates the manual recipe (kept here for reference):
`$env:VSTEST_HOST_DEBUG=1; dotnet test <project> -c Debug` prints `Process Id: NNNN`
and waits; `debug_attach(NNNN)` releases it via `Debugger.IsAttached`.

## Alternatives & how to choose

There are several MCP debuggers. They make different trade-offs — this is about
**what you get and what you give up**, not "we're best". Pick by what you debug.

| If you want… | Consider | What you give up |
|---|---|---|
| **Deep .NET** debugging — async/`Task` state, headless, remote/POD | **ClrVoyant** (this) | breadth: it is **.NET 8+ only**, and headless (no visual IDE) |
| **Many languages** via one DAP server (Python, JS, Go, Rust, Java, .NET…) | [debugmcp/mcp-debugger](https://github.com/debugmcp/mcp-debugger) | no async/`Task`/heap view — DAP can't enumerate Tasks, so .NET async state is invisible |
| Debugging **inside VS Code** with a human watching | [microsoft/DebugMCP](https://github.com/microsoft/DebugMCP) | requires VS Code running (not truly headless); bounded by the VS Code Debug API; no async Tasks |
| **Python/Node/Java/browser**, no IDE | [bastiencb/claude-mcp-debugger](https://github.com/bastiencb/claude-mcp-debugger) | no .NET; no async/heap inspection |

The one thing ClrVoyant has that DAP-based tools structurally cannot: a **"Tasks
window"** — enumerate every in-flight `Task` and its await/continuation graph (via
ClrMD), the thing you actually need to debug a stuck `async` method. The price is
focus: it does **.NET only**, deeply, instead of many languages shallowly.

Fuller breakdown with sources: [docs/comparison.md](docs/comparison.md).

## Known limitations

- **One debugger per process.** ClrVoyant can't attach to a process another
  debugger already owns — e.g. an app started with the Visual Studio / Rider
  debugger (F5 / *Start Debugging*). A .NET process allows a single debugger
  (ICorDebug), so the second attach is refused. Run the target **without** the IDE
  debugger (*Start Without Debugging* / Ctrl+F5 / `dotnet run`), or detach the IDE
  first (VS: *Debug → Detach All*, which leaves the app running). This is an
  OS/runtime constraint, not a ClrVoyant limitation.
- **Optimized (Release) builds degrade line-level debugging.** With optimizations
  on, the JIT reorders and elides code, so line breakpoints may not bind where you
  expect and locals can read as unavailable — a universal debugger limitation, not
  specific to ClrVoyant. For reliable line breakpoints and locals, build the target
  **unoptimized** (Debug, or `<Optimize>false</Optimize>`) and ship its **PDBs**.
  Attach, call stacks, `evaluate`, and the async `Task`/heap view work regardless.
- Child-process auto-attach (`set_auto_attach`) discovers children by parent PID
  **without suspending them**, so a child's very first startup instants may run
  before the debugger attaches. Suspend-at-startup (tier 3) is not yet implemented.
- `evaluate` runs code in the target — treat as dangerous.
- `pause` requires a thread known from a prior stop (netcoredbg does not
  enumerate threads of a freely-running process).
- Targets must be **.NET 8+** (netcoredbg does not debug .NET Framework).

## Tests & coverage

```pwsh
dotnet test tests/ClrVoyant.Tests --settings coverlet.runsettings
```

Unit tests exercise the real logic — the wait-based execution model, the
breakpoint store, content-based breakpoint resolution, and Task status/async-method
decoding — with a fake engine; integration tests drive a **real** netcoredbg + ClrMD
against `SampleApp` (launch → breakpoint → inspect → enumerate Tasks). Line coverage
is high as a by-product, but the point is the behaviours above, not the number. See
[docs/testing.md](docs/testing.md).

## Layout

```
src/ClrVoyant.Core         models, IDebugEngine, Session, SessionManager
src/ClrVoyant.Dap          DAP client + DapEngine (netcoredbg)
src/ClrVoyant.Inspection   ClrMD TaskInspector (Tasks + async graph) + method discovery
src/ClrVoyant.Server       MCP stdio host + tools
tools/netcoredbg          bundled debug engine (fetched at build)
samples/SampleApp         a target app for tests
spike/                    the de-risking proof-of-concept
scripts/mcp-driver.ps1    stdio test harness
```

## Acknowledgments

ClrVoyant stands on:

- **[netcoredbg](https://github.com/Samsung/netcoredbg)** (Samsung, MIT) — the .NET
  debug engine, driven over DAP.
- **[ClrMD](https://github.com/microsoft/clrmd)** (Microsoft, MIT) — heap/Task
  introspection, the basis of the "Tasks window".
- The **[Debug Adapter Protocol](https://microsoft.github.io/debug-adapter-protocol/)**
  (Microsoft) and the **[Model Context Protocol](https://modelcontextprotocol.io)**
  (Anthropic) and its C# SDK.

Prior art that shaped the design space: [microsoft/DebugMCP](https://github.com/microsoft/DebugMCP)
and [debugmcp/mcp-debugger](https://github.com/debugmcp/mcp-debugger). See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for licenses.

## License

[MIT](LICENSE) © Matteo Panzacchi
