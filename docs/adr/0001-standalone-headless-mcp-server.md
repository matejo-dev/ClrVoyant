# ADR-0001: Standalone headless MCP server (not IDE automation)

- **Status:** Accepted
- **Date:** 2026-06-03

## Context

The goal is to let an agent (e.g. Claude) *deterministically* debug .NET apps —
set breakpoints, step, read variables/call stacks/threads, and enumerate all
in-flight async `Task`s ("everything a developer looks at"). The question was
which substrate to build that on.

## Decision

Build a **standalone C#/.NET MCP server** that runs **headless** (no IDE, no GUI),
exposing debugging as a request/response tool contract over MCP (stdio by
default). The agent drives it; there is no editor in the loop.

## Consequences

- Deterministic, scriptable, portable; nothing depends on a desktop session.
- No native visual "human-in-the-loop" surface: a human follows the debug through
  the agent's output, not an editor's panes. Accepted trade-off (see
  [0009](0009-http-transport-mandatory-auth.md)/[0010](0010-pod-remote-debug-topology.md)
  for the remote story).
- We own the engine orchestration (and its complexity) rather than inheriting an
  IDE's debug stack.

## Alternatives considered

- **Visual Studio + EnvDTE / VSIX** — flaky COM automation, requires a running VS,
  and exposes no programmatic async/Tasks view. Rejected.
- **VS Code extension host** — re-introduces a required GUI and *still* needs a
  .NET sidecar for Task enumeration; we'd carry the worst of both. Rejected.
  (This is essentially what Microsoft's DebugMCP is — broad language coverage but
  bounded by the VS Code Debug API, with no async-Tasks view.)
- **vsdbg standalone** — license-locked to Visual Studio / VS Code; cannot ship.
  Rejected.
