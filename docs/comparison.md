# ClrVoyant vs other MCP debuggers

A "what you get / what you give up" comparison to help you pick a tool. The goal is
**fit**, not ranking: ClrVoyant is deliberately narrow (.NET only, headless).

## How this was checked

On **2026-11-25** the project pages listed below (GitHub READMEs and NuGet package
pages) were read. Nothing was installed or run; every statement is what that page says
about itself, and the pages change. **"Not documented"** means the page text read does not
mention the capability, which is not the same as the capability being absent. Counts
("27 tools") are the page's own. Competitor information is of the date above: check the
project before deciding.

## The shapes of tools

1. **Multi-language DAP front ends.** One MCP server over several debug adapters
   (Debug Adapter Protocol, DAP). Broad language coverage; capabilities are those the
   adapter exposes.
2. **IDE-hosted.** An MCP server inside an editor, reusing its debug stack. Visual, with
   a human in the loop.
3. **.NET-specific servers.** Several exist (next section). Most drive netcoredbg or
   another .NET debugger over DAP.
4. **.NET dump and diagnostics servers.** Analyse a dump file or a process's runtime data
   instead of (or besides) live debugging.

ClrVoyant is in the third group. It adds ClrMD heap reads next to netcoredbg, to expose
the Tasks of a live process stopped under the debugger.

## .NET-specific MCP debuggers

| Project | Engine (per its page) | Live debugging | Async / `Task` features documented | Requires | License | Version listed (page date) |
|---|---|---|---|---|---|---|
| **ClrVoyant** (this) | netcoredbg over DAP, plus ClrMD for heap reads | launch, attach, `debug_test`, breakpoints, stepping, variables, evaluate | `list_tasks` (all Tasks of the stopped process, with status and async method), `get_async_graph`, `get_async_callstack` | .NET 10 runtime for the tool; targets .NET 8/9/10 | MIT | see [CHANGELOG](../CHANGELOG.md) |
| [netcoredbg-mcp](https://github.com/thebtf/netcoredbg-mcp) (PyPI) | netcoredbg, DAP, Windows UI Automation | launch, attach, breakpoints, stepping, variables | Not documented (135 tools listed, none for Tasks) | Python 3.10+, netcoredbg (setup wizard can fetch it); Windows for the UI-automation tools | MIT | v0.23.17 (GitHub) |
| [AspNetCoreDebuggerMcp](https://www.nuget.org/packages/AspNetCoreDebuggerMcp) | netcoredbg bundled (linux, win, osx), DAP | launch, attach, breakpoints, stepping, variables | `hang_analyze` pauses the process and classifies each thread's blocking pattern ("Monitor / Task / Semaphore / async"); no Task or async-graph listing documented | .NET 10 SDK; stdio mentioned | MIT | 1.4.0 (2026-09-08) |
| [DebugMcpServer](https://www.nuget.org/packages/DebugMcpServer) | DAP (netcoredbg for .NET; also Python, Node.js, C/C++); ClrMD for .NET **dump** analysis | launch, attach via DAP | `dotnet_dump_async_state` "Analyze async Tasks and state machines (deadlock diagnosis)", on a **loaded dump file**; no live-process Task enumeration documented | .NET 8 | MIT | 0.7.0 (2026-03-13) |
| [DotnetDebugger.Mcp](https://www.nuget.org/packages/DotnetDebugger.Mcp) | "clrdbg, driven over DAP" | launch, attach, breakpoints, stepping, variables; MAUI tooling | Not documented | .NET 10 SDK | MIT | 0.8.0 (2026-10-25) |
| [debug-mcp](https://www.nuget.org/packages/debug-mcp) | ICorDebug directly (no DAP) | launch, attach, breakpoints, tracepoints, inspection (34 tools) | Not documented | .NET 10 SDK; Linux (Windows and macOS "planned") | **AGPL-3.0** | 0.20.0 (2026-08-26) |
| [Csdbg.Mcp](https://www.nuget.org/packages/Csdbg.Mcp) | netcoredbg, started on demand | launch, attach, breakpoints, stepping, variables | Not documented | .NET 10 SDK | MIT | 0.2.2 (2026-07-27) |

Also seen in search results and **not assessed** here: `dotnet-diagnostics-mcp`,
`MemoryLens.Mcp`, [`tonyredondo/debugger-mcp-server`](https://github.com/tonyredondo/debugger-mcp-server)
and a ClrMD-based dump-analysis server by another author. They appeared to be dump or
diagnostics oriented; nothing is claimed about them.

What this table supports, and no more: among the pages read, ClrVoyant is the one whose
documentation describes enumerating the `Task`s of a live process that is stopped under
the debugger, and reconstructing its await/continuation graph. DebugMcpServer documents
async state-machine analysis for dump files, which answers a different situation (a
crash dump versus a process you can stop and step); AspNetCoreDebuggerMcp documents a
thread-blocking classification. Others do not document anything of the kind. Whether
such features exist undocumented, or are added later, this comparison cannot say.

## Multi-language and IDE-hosted tools

| | mcp-debugger | microsoft/DebugMCP | claude-mcp-debugger |
|---|---|---|---|
| Source | [debugmcp/mcp-debugger](https://github.com/debugmcp/mcp-debugger) | [microsoft/DebugMCP](https://github.com/microsoft/DebugMCP) | [bastiencb/claude-mcp-debugger](https://github.com/bastiencb/claude-mcp-debugger) |
| Shape | DAP front end | VS Code extension, plus a standalone CLI | DAP front end |
| Languages (per page) | Python, JS/TS, Ruby, Rust, Go, Java, .NET, C/C++, COBOL, Dart/Flutter (the page also says "ten" and "seven" languages in different places) | extension: Python, JS/TS, Java, C/C++, Go, Rust, PHP, Ruby, C#/.NET; CLI: any DAP adapter over stdio, with shorthands including `csharp` / `dotnet` | Python, Node.js, Java, browser JavaScript; .NET not mentioned |
| .NET | via netcoredbg; the container image excludes .NET and Go (run it with npm/npx on the host) | through the C# VS Code extension; or a DAP adapter you configure for the CLI | not mentioned |
| Needs an IDE | "No IDE required" | The VS Code extension needs VS Code (with language extensions); the standalone CLI "does not require VS Code" | "No IDE", headless |
| MCP transports | stdio, Streamable HTTP (loopback by default) | extension: Streamable HTTP on localhost:3001; CLI: not stated | stdio mentioned |
| Multi-session | yes ("every MCP client of it sees the same debug sessions") | not clearly documented | not documented |
| Async `Task` / heap view | not documented | not documented | not documented |
| Tools | 29 | 18 | 22 (the page also says 23) |
| License | MIT | MIT | MIT |

### What you get and what you give up

**ClrVoyant.** Get: the Tasks view of a stopped live process, no IDE, a sidecar image
for Kubernetes pods, multi-session with `wait_for_any_stop`, debugging without source
(`list_methods`, `set_function_breakpoint`), `debug_test`. Give up: breadth (.NET 8/9/10
only; Windows x64, Linux x64/arm64; no macOS, [ADR-0011](adr/0011-supported-architectures.md)),
a visual surface, and the reach of the .NET-specific projects above that target other
platforms or workflows (MAUI tooling, Windows UI automation, macOS).

**mcp-debugger.** Get: one server for many languages, including .NET, with stdio and
HTTP and multi-session. Give up: for .NET it is the generic DAP surface; no Task view is
documented, and the container image does not include .NET.

**microsoft/DebugMCP.** Get: a human watching and able to take over in VS Code, many
languages, and a standalone CLI that does not need VS Code. Give up: the extension needs VS Code running; no Task view is
documented.

**claude-mcp-debugger.** Get: Python, Node.js, Java and browser debugging without an
IDE. Give up: no .NET is documented.

Other multi-language projects, listed without having been checked on 2026-11-25: [KashunCheng/dap_mcp](https://github.com/KashunCheng/dap_mcp),
[Govinda-Fichtner/debugger-mcp](https://github.com/Govinda-Fichtner/debugger-mcp),
[Digital-Defiance/mcp-debugger-server](https://github.com/Digital-Defiance/mcp-debugger-server),
[mizchi/debugger-mcp](https://github.com/mizchi/debugger-mcp).

## The short version

Debugging **many languages**: a multi-language server (mcp-debugger, or microsoft/DebugMCP
if you want a human in VS Code). Debugging **.NET**: there are several options
(table above); choose by what you need. If you need to see **what every in-flight
`Task` is awaiting** in a process you can stop under a debugger, ClrVoyant is the one
whose documentation says it does that, in exchange for being .NET-only. If you are
analysing a **crash dump**, look at the dump-analysis servers instead.

The Debug Adapter Protocol has no request for listing the Tasks or objects on a heap, so
a DAP-only tool cannot offer this without a second mechanism; ClrVoyant's is ClrMD
([ADR-0002](adr/0002-two-engines-one-contract.md)).

*Sources (read 2026-11-25):* the GitHub pages linked above, and the NuGet package pages
for AspNetCoreDebuggerMcp, DebugMcpServer, DotnetDebugger.Mcp, debug-mcp and Csdbg.Mcp.
