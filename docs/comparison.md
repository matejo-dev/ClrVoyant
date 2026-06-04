# ClrVoyant vs other MCP debuggers

An honest "what you get / what you give up" comparison, so you can pick the right
tool. The goal is **fit**, not ranking — ClrVoyant is deliberately narrow.

The MCP-debugger space splits into three shapes:

1. **DAP multiplexers** — one MCP server fronting many language debuggers over the
   Debug Adapter Protocol (DAP). Broad language coverage; capabilities are the
   common denominator of DAP.
2. **IDE-hosted** — an MCP server inside an editor (VS Code), reusing the editor's
   debug stack. Visual, human-in-the-loop; tied to the editor.
3. **Single-runtime, deep** — focused on one runtime, going past DAP. **ClrVoyant**
   is this: .NET only, with async/`Task` introspection DAP cannot express.

## Feature matrix

| | ClrVoyant | mcp-debugger | microsoft/DebugMCP | claude-mcp-debugger |
|---|---|---|---|---|
| Shape | single-runtime, deep | DAP multiplexer | IDE-hosted (VS Code) | DAP multiplexer |
| Languages | .NET 8/9/10 only | Python, JS, Go, Rust, Java, .NET | 9 languages | Python, Node, Java, browser |
| Engine | netcoredbg (DAP) **+ ClrMD** | per-language DAP adapters | VS Code Debug API | DAP + CDP |
| Headless (no IDE) | ✅ | ✅ | ❌ (needs VS Code) | ✅ |
| Transports | stdio + HTTP | stdio + HTTP | HTTP | stdio |
| Multi-session | ✅ | ✅ | ❌ | partial (Node children) |
| **Async `Task` / heap view** | ✅ (the differentiator) | ❌ | ❌ | ❌ |
| Remote / in-POD | ✅ (sidecar, [docs](remote-debugging-pod.md)) | via container | ❌ | ❌ |
| Distribution | dotnet tool / container / source | npm / npx / docker | VS Code extension | install script |

## What you get / what you give up

### ClrVoyant (this project)
- **Get:** the only one that enumerates every in-flight `Task` and its
  await/continuation graph (via ClrMD) — what you actually need for a stuck `async`
  method; headless; HTTP/POD remote debugging; multi-session.
- **Give up:** breadth. **.NET 8+ only.** No visual IDE surface — a human follows
  the debug through the agent's output, not editor panes.

### [debugmcp/mcp-debugger](https://github.com/debugmcp/mcp-debugger)
- **Get:** one server for Python, JS, Rust, Go, Java, **and .NET** over DAP;
  mature (CI, coverage, OpenSSF badges); npm + Docker; stdio + HTTP; multi-session.
- **Give up:** DAP's ceiling — **no async/`Task`/heap view**. For .NET it gives the
  standard breakpoint/step/variables surface but not the Tasks window. .NET is one
  of many runtimes, not a deep focus.

### [microsoft/DebugMCP](https://github.com/microsoft/DebugMCP)
- **Get:** 9 languages; runs inside VS Code so a human sees the session live and
  can take over; line-content breakpoints.
- **Give up:** it needs VS Code running (not truly headless / CI-friendly), is
  bounded by the VS Code Debug API, and has no async-Tasks view.

### [bastiencb/claude-mcp-debugger](https://github.com/bastiencb/claude-mcp-debugger)
- **Get:** Python/Node/Java/**browser** debugging, no IDE; set-variable; CDP for
  the browser; tidy one-command install.
- **Give up:** no .NET; no async/heap inspection.

Other projects in the space: [KashunCheng/dap_mcp](https://github.com/KashunCheng/dap_mcp),
[Govinda-Fichtner/debugger-mcp](https://github.com/Govinda-Fichtner/debugger-mcp)
(Rust DAP bridge), [Digital-Defiance/mcp-debugger-server](https://github.com/Digital-Defiance/mcp-debugger-server)
(Node/TS via CDP, profiling), [mizchi/debugger-mcp](https://github.com/mizchi/debugger-mcp).

## The short version

If you debug **many languages**, use a DAP multiplexer (mcp-debugger) or, if you
want a human in the loop in VS Code, microsoft/DebugMCP. If you debug **.NET** and
care about **async/`Task` state** — especially headless or inside a POD — ClrVoyant
gives you the Tasks view the others structurally can't, in exchange for being
.NET-only.

*Sources: project READMEs as of June 2026 — links above. Details change; verify
against each project before deciding.*
