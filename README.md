# ClrVoyant

[![CI](https://github.com/matejo-dev/ClrVoyant/actions/workflows/ci.yml/badge.svg)](https://github.com/matejo-dev/ClrVoyant/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/matejo-dev/ClrVoyant/blob/main/LICENSE)
![Debug targets: .NET 8/9/10](https://img.shields.io/badge/debug%20targets-.NET%208%20%7C%209%20%7C%2010-512BD4)

A local **MCP server that lets an agent (e.g. Claude) debug .NET applications**:
launch a program, set breakpoints, step, inspect variables / call stacks / threads, and
enumerate all in-flight async `Task`s, headless, with no IDE. You run it locally and
point your agent at it, then ask in natural language:

> "Launch project X. It hangs instead of completing. Set breakpoints, step through,
> read the variables and the in-flight async tasks, and tell me why."

The agent decides *where* to break and *what* to inspect (it reads your source with its
own tools); ClrVoyant gives it the debugger primitives.

> Links in this file are absolute because it is also the package readme on nuget.org,
> where repository-relative links do not resolve.

## Quickstart

**1. Install** (needs the [.NET 10 SDK](https://dotnet.microsoft.com/download), which
includes the runtimes the tool runs on):

```sh
dotnet tool install -g ClrVoyant
```

The tool bundles the debug engine for every supported platform, so it installs and runs
offline. It installs the command `clrvoyant` into the .NET tools folder
(`~/.dotnet/tools` or `%USERPROFILE%\.dotnet\tools`), which is normally on your `PATH`;
open a new terminal if the command is not found. `clrvoyant` is an MCP server, not an
interactive command: your client starts it.

**2. Register it in your MCP client.** Claude Code:

```sh
claude mcp add --transport stdio --scope user clrvoyant -- clrvoyant
```

or put this in a project's `.mcp.json` (Claude Code) or in Claude Desktop's
`claude_desktop_config.json`:

```json
{ "mcpServers": { "clrvoyant": { "command": "clrvoyant" } } }
```

Cursor (`.cursor/mcp.json`) documents `type` for stdio servers:

```json
{ "mcpServers": { "clrvoyant": { "type": "stdio", "command": "clrvoyant" } } }
```

VS Code uses `.vscode/mcp.json` with the key `servers`:

```json
{ "servers": { "clrvoyant": { "type": "stdio", "command": "clrvoyant" } } }
```

Where these formats come from, as read from each client's documentation on 2026-11-25:
Claude Code (`claude mcp add ... -- <command>`, `.mcp.json` with `mcpServers`; an entry
without `type` is stdio), VS Code (`.vscode/mcp.json`, top-level `servers`, `"type":
"stdio"`; "MCP: Add Server" in the Command Palette), Cursor (`.cursor/mcp.json` or
`~/.cursor/mcp.json`, `mcpServers`; `command` must be on `PATH` or a full path), Claude
Desktop (Settings, Developer, Edit Config; `%APPDATA%\Claude\claude_desktop_config.json`
on Windows, `~/Library/Application Support/Claude/claude_desktop_config.json` on macOS;
restart the app). Not verified: Windsurf (the install script below can write its
config), and whether a GUI client that does not inherit your shell's `PATH` finds
`clrvoyant`.

**3. Check it is connected.** In Claude Code run `claude mcp list` (it should say
`Connected`) or `/mcp` in a session. In other clients, look for `clrvoyant` and its
tools in the MCP/tools list; asking the agent to call `get_debug_instructions` is a
cheap end-to-end test.

**4. First prompt.** Build something with a PDB (`dotnet build -c Debug`) and ask:

> "Launch `<path>/MyApp/bin/Debug/net10.0/MyApp.dll` under the debugger. Put a
> breakpoint on the line `var total = items.Sum(x => x.Price);` in `Program.cs`, run to
> it, and show me the local variables."

To try it on the repository's own sample (clone this repository first):

```sh
dotnet build samples/SampleApp -c Debug -f net10.0
```

> "Launch `samples/SampleApp/bin/Debug/net10.0/SampleApp.dll` (use full paths). Set a
> breakpoint on the line `int offset = squared + 7;` in `samples/SampleApp/Program.cs`,
> continue until it stops, then list the in-flight Tasks and tell me which are stuck."

**If `clrvoyant` is not on `PATH`**: confirm it is installed (`dotnet tool list -g`),
open a new terminal, or add the .NET tools folder to `PATH`. Failing that, register it
by the full path of the executable in the .NET tools folder (`clrvoyant.exe` on
Windows, `clrvoyant` elsewhere) as `"command"`; in JSON, write Windows backslashes
doubled.

Install scripts that pack this repository, install the tool from the local package and
write the client entry in one step (the existing config is backed up first):

```pwsh
./scripts/install.ps1 -Client cursor     # claude-code | claude-desktop | cursor | windsurf | vscode
./scripts/install.ps1                     # default: just prints the snippet
```
```sh
./scripts/install.sh --client cursor      # bash equivalent (uses jq to merge)
```

## How it works

A single .NET process orchestrates **two engines** behind one IDE-neutral tool contract:

- **[netcoredbg](https://github.com/Samsung/netcoredbg)** (bundled, MIT) over the
  **Debug Adapter Protocol**: control and live introspection (breakpoints, step,
  threads, call stack, variables, evaluate).
- **[ClrMD](https://github.com/microsoft/clrmd)**: reads the heap at a stop to
  enumerate all `Task`s and reconstruct the async await/continuation graph (the "Tasks
  window" equivalent, which DAP cannot provide). On Windows via a process snapshot; on
  Linux via a passive read of the process while netcoredbg holds it.

See [docs/architecture.md](https://github.com/matejo-dev/ClrVoyant/blob/main/docs/architecture.md)
for the design and the [documentation index](https://github.com/matejo-dev/ClrVoyant/blob/main/docs/README.md)
for the rest.

## What you can do with it

- **Debug an app you launch**: break, step, read variables, call stack, threads; pass
  environment variables with `env`.
- **Attach to a running process**: `list_processes` (filters to .NET, shows command
  lines) then `debug_attach`.
- **Debug a unit test in one step**: `debug_test(project[, testName])` runs the test
  host suspended, attaches, and breaks in your test or production code.
- **Understand stuck `async` code**: every in-flight `Task` with its state, and the
  await/continuation graph (`list_tasks`, `get_async_graph`).
- **Debug several processes at once**: multi-session, `wait_for_any_stop` to learn which
  one stopped, exited or lost its debugger; optional child-process auto-attach.
- **Debug without source**: with only a build's DLLs and PDBs, `list_methods` finds method
  names and `set_function_breakpoint("Namespace.Type.Method")` binds from the PDB.
- **Debug inside a container or Kubernetes pod**: run it as an authenticated HTTP
  sidecar next to the target (PDBs required); only the agent is remote.
  ([remote debugging](https://github.com/matejo-dev/ClrVoyant/blob/main/docs/remote-debugging-pod.md))

## Requirements

- **To run the tool:** the **.NET 10 runtime including ASP.NET Core** (the HTTP transport
  uses it), or a newer runtime through roll-forward; the .NET 10 SDK has both. Installing
  with an older SDK has not been tried.
- **Debug targets:** .NET **8, 9 and 10** (the runtime of the target must be installed,
  as for running the app at all). .NET 8 and 9 left Microsoft support on 2026-11-10 but
  remain supported as targets. .NET 11 is not validated. .NET Framework is not supported.
- **Platforms:** `win-x64`, `linux-x64`, `linux-arm64`. The engine debugs locally, so the
  tool runs at the same architecture as its target. No `win-arm64` (netcoredbg ships no
  such build) and no macOS (not validated). Each architecture has its own CI leg, the
  arm64 one on real hardware
  ([ADR-0011](https://github.com/matejo-dev/ClrVoyant/blob/main/docs/adr/0011-supported-architectures.md)).
- **Linux distribution:** glibc 2.34+ and a GCC 12+ libstdc++ (`GLIBCXX_3.4.30`), the
  bundled engine's baseline, for example Debian 12 or Ubuntu 22.04 and newer. Not Debian 11
  or RHEL 9. CI starts the engine on the sidecar image's base and on Ubuntu 22.04.
- A **PDB** next to each assembly you want breakpoints and locals in (see
  [limitations](#known-limitations)).

## Configuration

All environment variables the server reads. Set them in the client's server entry
(`env`, or `claude mcp add --env NAME=value`) or in the container.

| Variable | Default | Meaning |
|---|---|---|
| `CLRVOYANT_TRANSPORT` | `stdio` | `http` selects the Streamable-HTTP transport; any other value (or unset) is stdio. |
| `CLRVOYANT_AUTH_TOKEN` | none | Bearer token for HTTP. **Required for HTTP**: the server refuses to start without it. Ignored on stdio. |
| `CLRVOYANT_HTTP_URL` | `http://127.0.0.1:3001` | Where HTTP listens. The default is loopback only; the container image sets `http://0.0.0.0:3001`. A warning is logged for any non-loopback address. |
| `CLRVOYANT_NETCOREDBG` | unset | Full path to a netcoredbg executable to use instead of the bundled one. If set, the file must exist, or starting a session fails (no fallback). |
| `CLRVOYANT_NO_FETCH` | unset | Anything but empty, `0` or `false` forbids the first-run download of netcoredbg (air-gapped use); only a complete cache or `CLRVOYANT_NETCOREDBG` then works. |
| `CLRVOYANT_CACHE_DIR` | `~/.clrvoyant` | Root of the download cache (`<dir>/netcoredbg/<version>`), used only when no bundled engine is found. A cache counts as complete only with its `netcoredbg-<version>.stamp` file next to the engine. |
| `CLRVOYANT_PACKAGING_TESTS` | unset | Test suite only: `1` enables the installed-tool packaging tests. |
| `CLRVOYANT_REQUIRE_TARGET_RUNTIMES` | unset | Test suite only: `1` makes a missing .NET 8/9/10 runtime a test failure instead of a skip. |

The engine is located when the **first session starts**, not at server start; a
problem shows up as the error of the first `debug_launch` / `debug_attach` /
`debug_test`.

## Transports

- **stdio** (default): the agent spawns the server locally; one client per process.
  Unauthenticated by design: the boundary is the OS process boundary.
- **HTTP**: `CLRVOYANT_TRANSPORT=http` and a `CLRVOYANT_AUTH_TOKEN`. It **fails closed**
  without a token and **listens on loopback by default**. Every request needs
  `Authorization: Bearer <token>` (the scheme is case-insensitive), is otherwise
  answered `401`, and refusals are audited with the remote address. It is stateless and
  plain HTTP: keep it inside a trusted network (`kubectl port-forward`) or put TLS in
  front. A debug server that can launch processes and `evaluate` code is a
  remote-code-execution surface: read
  [docs/security.md](https://github.com/matejo-dev/ClrVoyant/blob/main/docs/security.md)
  before exposing it. For Kubernetes see
  [deploy/](https://github.com/matejo-dev/ClrVoyant/blob/main/deploy/README.md).

## Tools

35 tools; the full reference (parameters, defaults, result shapes, errors, paging) is
[docs/tools.md](https://github.com/matejo-dev/ClrVoyant/blob/main/docs/tools.md). Have
the agent call `get_debug_instructions` first (the same playbook is also sent as the MCP
server instructions).

- **Onboarding:** `get_debug_instructions`
- **Sessions:** `debug_launch`, `debug_attach`, `debug_test`, `restart_debugging`,
  `debug_stop`, `debug_status`, `list_sessions`, `list_processes`, `get_output`,
  `set_auto_attach`
- **Breakpoints:** `set_breakpoint`, `set_function_breakpoint`, `list_breakpoints`,
  `list_function_breakpoints`, `remove_breakpoint`, `clear_all_breakpoints`,
  `set_exception_breakpoints`, `list_methods`
- **Execution** (wait-based: they block until the next stop, exit or timeout):
  `continue`, `step_over`, `step_into`, `step_out`, `pause`, `wait_for_any_stop`
- **Introspection** (target stopped): `get_threads`, `get_callstack`, `get_scopes`,
  `get_variables`, `evaluate` (**executes code in the debuggee**), `get_exception_info`
- **Async / Tasks** (ClrMD, target stopped): `list_tasks`, `get_task`,
  `get_async_graph`, `get_async_callstack`

Things to know: `debug_stop` kills only processes the server started and detaches from
ones it attached to (`terminate` overrides); a tool failure comes back as an error result
with an actionable message; `list_tasks`, `get_async_graph`, `get_variables` and
`list_methods` return pages (`items`, `total?`, `truncated`, `nextOffset?`).

## Debugging tests

Tests run in a `testhost` process. `debug_test(testProject, testName?)` runs
`dotnet test` with `VSTEST_HOST_DEBUG=1` (the host suspends and announces its PID),
attaches in one step, and you set breakpoints right after it returns. It fails at once,
with the build output, if `dotnet test` ends before a host appears. The `dotnet test`
process is ended with the session. Build the tests with a PDB (Debug is safest for full
locals).

## Alternatives and how to choose

Several MCP debuggers exist, with different trade-offs. This is a summary of what their
own pages said on **2026-11-25**; "not documented" means the page did not mention it. The
details, sources and caveats are in
[docs/comparison.md](https://github.com/matejo-dev/ClrVoyant/blob/main/docs/comparison.md).

| If you want... | Consider | Notes |
|---|---|---|
| .NET debugging with the `Task` state of a live, stopped process, headless, in a pod | **ClrVoyant** (this) | .NET 8/9/10 targets only; needs the .NET 10 runtime. |
| Other .NET MCP debuggers | netcoredbg-mcp, AspNetCoreDebuggerMcp, DebugMcpServer, DotnetDebugger.Mcp, debug-mcp, Csdbg.Mcp | Live debugging over DAP or ICorDebug. Of these, none documents a live Task listing; DebugMcpServer documents async state analysis on dump files. |
| Many languages through one DAP server, .NET included | [debugmcp/mcp-debugger](https://github.com/debugmcp/mcp-debugger) | Task/heap view not documented. |
| Debugging inside VS Code with a human watching | [microsoft/DebugMCP](https://github.com/microsoft/DebugMCP) | The extension needs VS Code; a standalone CLI that does not is also described. Task view not documented. |
| Python / Node / Java / browser, no IDE | [bastiencb/claude-mcp-debugger](https://github.com/bastiencb/claude-mcp-debugger) | .NET not documented. |

The Debug Adapter Protocol has no request for listing `Task`s or heap objects; ClrVoyant
gets them from ClrMD. The price is focus: .NET only, headless.

## Known limitations

- **One debugger per process.** ClrVoyant cannot attach to a process another debugger
  already owns, for example an app started with the Visual Studio or Rider debugger. A
  .NET process allows a single debugger (ICorDebug). Run the target without the IDE
  debugger (Ctrl+F5, `dotnet run`), or detach the IDE first. This is an OS/runtime
  constraint.
- **A PDB, not Debug-vs-Release, gates breakpoints and locals.** netcoredbg disables JIT
  optimization on module load, so an optimized assembly **with** a PDB still debugs
  (breakpoints bind, stepping works, locals are readable; the test suite verifies this).
  With **no PDB**, breakpoints cannot be placed at all, by line or by method name;
  `pause` into the target and use the ClrMD `Task`/heap view, which needs no PDB. The
  compiler can still drop dead locals or inline code, and those will not reappear.
- Child-process auto-attach (`set_auto_attach`) does not suspend children, so a child's
  very first instants may run before attach. Suspend-at-startup is not implemented.
- `evaluate` runs code in the target: treat it as dangerous.
- `pause` is best-effort: netcoredbg cannot always break into a freely running process,
  and it needs a thread known from an earlier stop.
- **The bundled netcoredbg differs by OS:** 3.2.0-1092 on Windows, 3.1.3-1062 on Linux (the
  3.2.0 Linux builds need glibc 2.38, so they do not start on Debian 12 or Ubuntu 22.04,
  where 3.1.3 does). 3.1.3 fails a whole `get_callstack` when a frame on the stack is
  managed but not IL: seen with WinForms on .NET 10, not observed on Linux so far. On a
  glibc 2.38+ host you can point `CLRVOYANT_NETCOREDBG` at a 3.2.0+ netcoredbg.

## Tests

```pwsh
dotnet test tests/ClrVoyant.Tests                                                  # everything runnable here
dotnet test tests/ClrVoyant.Tests --filter "Category!=Integration&Category!=Packaging"   # fast tests only
```

Layers: unit tests over a fake engine, a DAP client against a scripted adapter, the tool
surface through a real MCP client, and integration tests that drive real netcoredbg and
ClrMD against sample apps on each target runtime. See
[docs/testing.md](https://github.com/matejo-dev/ClrVoyant/blob/main/docs/testing.md).

## Layout

```
src/ClrVoyant.Core         models, IDebugEngine, Session, SessionManager
src/ClrVoyant.Dap          DAP client + DapEngine (netcoredbg)
src/ClrVoyant.Inspection   ClrMD TaskInspector (Tasks + async graph) + method discovery
src/ClrVoyant.Server       MCP host: tools, filters, stdio and HTTP transports
tools/                     scripts that fetch netcoredbg at build time (not committed)
samples/                   target apps for the tests (net8.0/net9.0/net10.0)
tests/ClrVoyant.Tests      the test suite
deploy/                    Dockerfile + Kubernetes sidecar manifest
docs/                      architecture, tool reference, security, ADRs
spike/                     historical experiments (not built, not maintained)
scripts/                   MCP stdio driver and end-to-end smoke scripts
```

## Releases

See the [changelog](https://github.com/matejo-dev/ClrVoyant/blob/main/CHANGELOG.md).
Release files (`.nupkg`, `.snupkg`, `SHA256SUMS`) and their provenance attestations are on
the GitHub Releases page; how to verify them is in
[docs/security.md](https://github.com/matejo-dev/ClrVoyant/blob/main/docs/security.md#supply-chain).
To report a vulnerability, see
[SECURITY.md](https://github.com/matejo-dev/ClrVoyant/blob/main/SECURITY.md).

## Acknowledgments

ClrVoyant stands on **[netcoredbg](https://github.com/Samsung/netcoredbg)** (Samsung,
MIT), **[ClrMD](https://github.com/microsoft/clrmd)** (Microsoft, MIT), the
**[Debug Adapter Protocol](https://microsoft.github.io/debug-adapter-protocol/)**
(Microsoft), and the **[Model Context Protocol](https://modelcontextprotocol.io)** and its
C# SDK. Prior art that shaped the design space:
[microsoft/DebugMCP](https://github.com/microsoft/DebugMCP) and
[debugmcp/mcp-debugger](https://github.com/debugmcp/mcp-debugger). Licenses:
[THIRD-PARTY-NOTICES.md](https://github.com/matejo-dev/ClrVoyant/blob/main/THIRD-PARTY-NOTICES.md).

Thanks to **[@robselleri70](https://github.com/robselleri70)** for the initial idea that
sparked this project.

## License

[MIT](https://github.com/matejo-dev/ClrVoyant/blob/main/LICENSE) © Matteo Panzacchi
