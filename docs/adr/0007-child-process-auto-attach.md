# ADR-0007: Tiered child-process auto-attach

- **Status:** Accepted
- **Date:** 2026-06-03

## Context

A debugged process often spawns .NET children (workers, test hosts). Catching them
ranges from "attach manually when you notice" to "suspend each child at startup so
no instruction runs before the debugger attaches". Each tier costs more.

## Decision

Ship in tiers:

- **Tier 1 — manual `debug_attach(pid)`.** Always available.
- **Tier 2 — auto-discovery (chosen, on by opt-in).** `ChildProcessWatcher` (a
  hosted background service) periodically enumerates processes, finds .NET children
  of already-debugged processes (by parent PID), and attaches them as new sessions.
  Opt-in via `set_auto_attach(true)`. No suspend-at-startup — a child's very first
  instants may run before attach (documented trade-off).
- **Tier 3 — suspend-at-startup (deferred).** `DOTNET_DefaultDiagnosticPortSuspend`
  so breakpoints can be set before a child runs. Not built; available if needed.

Process enumeration is cross-OS via `ProcessLister` (Windows WMI; Linux `/proc`),
shared with the `list_processes` tool.

## Consequences

- Manual attach covers the common case immediately; auto-discovery removes the
  "notice and attach" toil for child-heavy workloads.
- Tier 2's no-suspend gap is real but acceptable for most debugging; tier 3 remains
  the escape hatch for "must break from the first instruction".
- `evaluate`/attach into discovered processes inherits the same security weight as
  any attach.

## Alternatives considered

- **Always suspend-at-startup** — most thorough but most intrusive (sets a runtime
  env var on the whole process tree) and unnecessary for most sessions. Deferred to
  tier 3.
- **No auto-attach at all** — leaves child-heavy debugging tedious. Rejected;
  tier 2 is opt-in so it costs nothing when off.
