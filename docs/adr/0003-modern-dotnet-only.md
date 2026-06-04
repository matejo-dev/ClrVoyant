# ADR-0003: Modern .NET only (8/9/10); drop .NET Framework

- **Status:** Accepted
- **Date:** 2026-06-03

## Context

Targets could be .NET Framework or modern .NET (Core). Supporting both widens the
engine matrix considerably.

## Decision

Support **modern .NET only — .NET 8 / 9 / 10**. .NET Framework is out of scope.

## Consequences

- One engine path: netcoredbg debugs modern .NET cleanly; ClrMD reads modern
  runtimes. No .NET Framework debugging stack to carry.
- Targets must be .NET 8+. .NET Framework apps are simply unsupported (clear error
  rather than degraded behaviour).
- Aligns with the project's "deep on .NET, not broad across languages" thesis.

## Alternatives considered

- **Also support .NET Framework** — neither netcoredbg nor a clean headless path
  covers it well; it would pull in a separate, legacy-only debug stack for shrinking
  value. Rejected.
