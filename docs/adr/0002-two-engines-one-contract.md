# ADR-0002: Two engines (netcoredbg + ClrMD) behind one IDE-neutral contract

- **Status:** Accepted
- **Date:** 2026-06-03

## Context

No single mechanism gives both interactive control *and* a full async/Task view.
Debug Adapter Protocol (DAP) engines give breakpoints/step/variables but cannot
enumerate all `Task`s on the heap. ClrMD can walk the heap and reconstruct the
async graph but does not drive execution.

## Decision

Orchestrate **two engines** against one target, behind **one IDE-neutral tool
contract** so the agent never sees which engine answered:

1. **netcoredbg** (Samsung, MIT) over **DAP** — control + live introspection
   (breakpoints, step, threads, call stack, variables, evaluate).
2. **ClrMD** (`Microsoft.Diagnostics.Runtime`) — read the heap at a stop to
   enumerate `Task`s and reconstruct the await/continuation graph (the "Tasks
   window" equivalent DAP cannot provide).

Coexistence — ClrMD reading the heap while netcoredbg holds the process stopped —
was de-risked by a spike on Windows and (later) Linux.

Corollary principle: **one backend per concern, contract-first, no premature
multi-backend abstraction.** `IDebugEngine` abstracts the control engine for
testability, not to support multiple control engines we don't have.

## Consequences

- The differentiating feature (full async Task introspection) is possible and is
  what sets this apart from DAP-only tools.
- Two identity spaces over the same objects: DAP handles (`frameId`,
  `variablesReference`, invalidated on continue) vs ClrMD heap addresses (valid
  within a snapshot). Reconciled by tagging handles with `(sessionId, stopId)`.
- We depend on runtime internals (`Task.m_stateFlags`, async-state-machine box
  layout) for ClrMD parsing; resilient to Debug/Release layout differences but
  coupled to the runtime.

## Alternatives considered

- **DAP-only** — simplest, but no Tasks view: gives up the whole differentiator.
- **ClrMD-only** — no live control (can't drive break/step). Insufficient.
