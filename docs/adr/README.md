# Architecture Decision Records

Each ADR captures one significant, hard-to-reverse decision: the context, the
choice, its consequences, and the alternatives we rejected. They are immutable —
when a decision changes we add a new ADR that supersedes the old one rather than
editing history.

The project is new (started 2026-06-03), so these were written close to the
decisions themselves, not reconstructed long after.

| # | Title | Status | Date |
|---|---|---|---|
| [0001](0001-standalone-headless-mcp-server.md) | Standalone headless MCP server (not IDE automation) | Accepted | 2026-06-03 |
| [0002](0002-two-engines-one-contract.md) | Two engines (netcoredbg + ClrMD) behind one IDE-neutral contract | Accepted | 2026-06-03 |
| [0003](0003-modern-dotnet-only.md) | Modern .NET only (8/9/10); drop .NET Framework | Accepted | 2026-06-03 |
| [0004](0004-wait-based-execution-model.md) | Wait-based execution model | Accepted | 2026-06-03 |
| [0005](0005-multi-session-from-the-start.md) | Multi-session from the start | Accepted | 2026-06-03 |
| [0006](0006-bundle-netcoredbg.md) | Bundle netcoredbg rather than depend on vsdbg/PATH | Accepted | 2026-06-03 |
| [0007](0007-child-process-auto-attach.md) | Tiered child-process auto-attach | Accepted | 2026-06-03 |
| [0008](0008-cross-os-heap-read.md) | Cross-OS heap read (Windows snapshot vs Linux passive) | Accepted | 2026-06-04 |
| [0009](0009-http-transport-mandatory-auth.md) | HTTP transport with mandatory bearer auth (fail-closed) | Accepted | 2026-06-04 |
| [0010](0010-pod-remote-debug-topology.md) | Remote/POD debugging topology (sidecar) | Accepted | 2026-06-04 |
| [0011](0011-supported-architectures.md) | Supported architectures (x64 + linux-arm64) | Accepted | 2026-06-04 |

## Format

We use a lightweight [MADR](https://adr.github.io/madr/)-style template: Status,
Context, Decision, Consequences, Alternatives considered. Keep each ADR short —
one decision, the reasoning, the trade-off.
