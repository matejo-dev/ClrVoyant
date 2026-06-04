# ADR-0004: Wait-based execution model

- **Status:** Accepted
- **Date:** 2026-06-03

## Context

MCP tools are synchronous request/response. Debugging is asynchronous: you resume
the target and *later* it stops (breakpoint, step complete, exception) or exits.
Bridging these cleanly determines how ergonomic the agent loop is.

## Decision

Make execution tools **block until the next stop**. `continue` / `step_*` send the
DAP command, then `await` the next `stopped` event (or `exited`) with a timeout,
and return the resulting state (`file:line`, reason, `threadId`).

Mechanics (`Session`): a resume operation arms a *targeted waiter*; the next stop
completes that waiter and is **not** treated as spontaneous. A stop with no
targeted waiter (a breakpoint hit while the agent was idle, or `stopAtEntry`)
raises `SpontaneousStop`, which `SessionManager` fans into `wait_for_any_stop`.
On timeout the target is left running and the waiter is dropped so a later stop is
spontaneous.

Introspection tools are valid only while stopped and fail fast with a clear
message otherwise.

## Consequences

- The agent sees a clean loop: `continue → "stopped at Foo.cs:42" → inspect →
  continue`. No polling.
- Each tool call maps to one deterministic outcome (stopped / exited / timed-out).
- Timeouts are a first-class result, not an error: long-running continues return
  "still running" rather than hanging forever.
- `pause` on a freely-running target is inherently best-effort (netcoredbg cannot
  always pause a running process); documented as a limitation.

## Alternatives considered

- **Fire-and-forget + separate poll tool** — forces the agent to poll and
  correlate; worse ergonomics and more round-trips. Rejected.
- **Streaming/notifications for stops** — heavier protocol surface; the blocking
  model already gives deterministic results. Rejected for now;
  `wait_for_any_stop` covers the multi-session "which one broke?" case.
