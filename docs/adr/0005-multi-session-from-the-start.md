# ADR-0005: Multi-session from the start

- **Status:** Accepted
- **Date:** 2026-06-03

## Context

Real debugging often spans several processes that start asynchronously (a host and
its workers, a parent and its children). Retrofitting multi-process support onto a
single-session core is invasive.

## Decision

Build **multiple concurrent sessions from the start**. `SessionManager` owns a
dictionary of `Session`s, each with its own engine, breakpoints and stop state.
Every session-scoped tool takes an **optional `sessionId`**; when omitted it
targets the *active* session (the only one, or the most-recently-stopped).
`wait_for_any_stop` fans stop events from all sessions into one channel so the
agent can learn which process broke first.

## Consequences

- Debugging several targets at once is a core capability, not a bolt-on.
- The "active session" convenience keeps single-target use ergonomic (no sessionId
  needed) while multi-target use stays explicit.
- Per-stop handles must be scoped per session: handles are tagged
  `(sessionId, stopId)` and stale/cross-session handles are rejected.
- Slightly more bookkeeping in the wait model (targeted vs spontaneous stops, see
  [0004](0004-wait-based-execution-model.md)).

## Alternatives considered

- **Single session, add multi later** — simpler initially, but the wait model and
  handle identity would need reworking; the user explicitly debugs multiple
  async-starting processes. Rejected.
