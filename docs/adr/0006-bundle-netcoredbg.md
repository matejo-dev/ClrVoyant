# ADR-0006: Bundle netcoredbg rather than depend on vsdbg/PATH

- **Status:** Accepted
- **Date:** 2026-06-03

## Context

The control engine needs a debug backend present at runtime. Options: require it on
`PATH`, depend on the user's IDE debugger (vsdbg), or ship our own.

## Decision

**Bundle netcoredbg** (Samsung, MIT) with the server. It is copied next to the
build output under `tools/netcoredbg/` and resolved by `NetcoredbgLocator`:
1. `CLRVOYANT_NETCOREDBG` env var (explicit override), else
2. the bundled copy `<app>/tools/netcoredbg/netcoredbg[.exe]`.

The Linux container image downloads the linux-amd64 build into the same location
(see [0010](0010-pod-remote-debug-topology.md)).

## Consequences

- The server is self-contained: no PATH setup, no IDE dependency, MIT-licensed and
  shippable (including into a container).
- We track netcoredbg releases ourselves and carry its quirks (async-line
  breakpoint binding, `pause` on a running target — documented limitations).
- Per-OS binary: `netcoredbg.exe` on Windows, `netcoredbg` on Linux; the locator
  picks by OS.

## Alternatives considered

- **vsdbg** — strictly better `evaluate`, but license-locked to VS / VS Code;
  cannot redistribute. Rejected (kept as a documented fallback only if netcoredbg
  proves too weak).
- **Require netcoredbg on PATH** — fragile onboarding; defeats "self-contained".
  Rejected (still allowed via the env override).
