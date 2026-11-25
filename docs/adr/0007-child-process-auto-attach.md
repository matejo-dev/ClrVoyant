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

## Update (2026-11-25)

- **Ownership is inherited.** A child auto-attached from a session whose debuggee this
  server owns (launched, or the host behind `debug_test`) is part of a process tree the
  server started: it is **terminated** when its session ends, and so are grandchildren.
  A child of an *attached* session is detached and left running. See `debug_stop` in
  [tools.md](../tools.md).
- **Creation-time check.** A matching parent PID is not proof of parentage: an OS keeps
  recording the creator's PID after the creator is gone, and PIDs are recycled. A
  process is adopted only if it was created no earlier than its parent; if either
  creation time is unknown it is left alone (`debug_attach` still covers it). Sessions
  whose process is gone no longer count as parents. This matters beyond a wrong session:
  an adopted stranger of a launched session would have been killed on `debug_stop`.
- **Retry limit.** An attach that fails is retried on later scans at most 3 times per
  process identity (PID plus creation stamp), then left alone, so a process that cannot
  be attached (another debugger owns it, access denied) does not cost a netcoredbg
  spawn on every scan.
