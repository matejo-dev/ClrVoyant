# Architecture Decision Records

Each ADR captures one significant, hard-to-reverse decision: the context, the
choice, its consequences, and the alternatives we rejected.

**Rule for changes.** Do not rewrite a decision. When a decision still stands but its
consequences changed (a default moved, a limit was added, a pending item was resolved),
append a dated `## Update (YYYY-MM-DD)` section to the ADR and leave the original text
as it was. When a decision is reversed, add a new ADR that supersedes the old one and
mark the old one's status accordingly.

The project was started on 2026-06-03; the first ADRs were written close to the
decisions themselves, not reconstructed long after.

| # | Title | Status | Date |
|---|---|---|---|
| [0001](0001-standalone-headless-mcp-server.md) | Standalone headless MCP server (not IDE automation) | Accepted | 2026-06-03 |
| [0002](0002-two-engines-one-contract.md) | Two engines (netcoredbg + ClrMD) behind one IDE-neutral contract | Accepted | 2026-06-03 |
| [0003](0003-modern-dotnet-only.md) | Modern .NET only (8/9/10); drop .NET Framework | Accepted (updated 2026-11-25) | 2026-06-03 |
| [0004](0004-wait-based-execution-model.md) | Wait-based execution model | Accepted (updated 2026-11-25) | 2026-06-03 |
| [0005](0005-multi-session-from-the-start.md) | Multi-session from the start | Accepted (updated 2026-11-25) | 2026-06-03 |
| [0006](0006-bundle-netcoredbg.md) | Bundle netcoredbg rather than depend on vsdbg/PATH | Accepted (updated 2026-06-05, 2026-11-25) | 2026-06-03 |
| [0007](0007-child-process-auto-attach.md) | Tiered child-process auto-attach | Accepted (updated 2026-11-25) | 2026-06-03 |
| [0008](0008-cross-os-heap-read.md) | Cross-OS heap read (Windows snapshot vs Linux passive) | Accepted | 2026-06-04 |
| [0009](0009-http-transport-mandatory-auth.md) | HTTP transport with mandatory bearer auth (fail-closed) | Accepted (updated 2026-11-25) | 2026-06-04 |
| [0010](0010-pod-remote-debug-topology.md) | Remote/POD debugging topology (sidecar) | Accepted (updated 2026-11-25) | 2026-06-04 |
| [0011](0011-supported-architectures.md) | Supported architectures (x64 + linux-arm64) | Accepted (updated 2026-11-25) | 2026-06-04 |
| [0012](0012-tool-errors-as-results.md) | Tool errors are returned as results, from one dispatch-boundary filter | Accepted | 2026-11-25 |
| [0013](0013-bounded-paged-tool-results.md) | Bounded, paged tool results | Accepted | 2026-11-25 |

## Format

We use a lightweight [MADR](https://adr.github.io/madr/)-style template: Status,
Context, Decision, Consequences, Alternatives considered. Keep each ADR short —
one decision, the reasoning, the trade-off.
