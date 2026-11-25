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

## Update (2026-11-25)

The decision about **debug targets** stands: .NET 8, 9 and 10 are supported. What
changed is the platform the tool itself runs on.

- The server, its libraries and the tests target **net10.0**, with `RollForward=Major`
  on the server so the installed tool still starts on a machine that only has a newer
  runtime. The SDK band is pinned by `global.json`. Running the tool therefore needs
  the .NET 10 runtime **including ASP.NET Core** (the HTTP transport uses it), or a
  newer one through roll-forward. Roll-forward onto a runtime newer than 10 is not
  validated.
- .NET 8 and 9 left Microsoft support on 2026-11-10. They stay supported as targets of
  the debuggee, not as runtimes for the tool.
- The sample debuggees are multi-targeted (`net8.0;net9.0;net10.0`) and the
  runtime-sensitive integration tests (the netcoredbg control loop, the ClrMD Task and
  async-graph inspection) run once per target runtime. CI installs all three runtimes
  and sets `CLRVOYANT_REQUIRE_TARGET_RUNTIMES=1`, so a missing runtime is a failure
  there; locally it is reported as a skipped test.
- **.NET 11 is not validated** as a debug target and is not claimed.
