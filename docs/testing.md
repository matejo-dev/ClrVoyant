# Testing

The suite lives in `tests/ClrVoyant.Tests` (xunit v2 on VSTest, because the product's
`debug_test` feature drives VSTest). It has four layers, from fast and in-process to
the shipped artefact, plus end-to-end smoke scripts and historical spikes.

The suite targets net10.0 and runs serially (`DisableTestParallelization`): integration
tests launch real processes and some tests mutate process-global environment variables.
A plain `dotnet test tests/ClrVoyant.Tests` works from a clean clone: the test project
builds the sample debuggees itself, for every target framework the tests debug.

## Running it

```pwsh
# Everything that can run on this machine
dotnet test tests/ClrVoyant.Tests

# With coverage, as CI does
dotnet test tests/ClrVoyant.Tests --settings coverlet.runsettings --collect:"XPlat Code Coverage"

# Only the fast tests: no real netcoredbg, no real debuggee, no packaging
dotnet test tests/ClrVoyant.Tests --filter "Category!=Integration&Category!=Packaging"

# Only the real-engine tests
dotnet test tests/ClrVoyant.Tests --filter "Category=Integration"
```

The two traits in use are `Category=Integration` (real netcoredbg and ClrMD against a
sample debuggee) and `Category=Packaging` (the installed tool). Tests without a trait
are the fast layers below. Packaging tests are skipped unless
`CLRVOYANT_PACKAGING_TESTS=1`.

## 1. Fast tests (no trait; no real debugger)

- **Session logic over a fake engine.** `Fakes/FakeDebugEngine.cs` is an in-memory
  `IDebugEngine` that can raise events later and from other threads. `SessionTests`,
  `SessionEventTests`, `SessionManagerTests`, `SessionManagerLifecycleTests` and
  `SessionOutputAndStopTests` cover the wait-based model (stop, timeout, exit, lost
  connection, cancellation, one resume at a time), the breakpoint store, session start
  as a transaction, ownership and `debug_stop`, stale-event skipping, output capture.
- **The DAP client and engine over a scripted adapter.** `Fakes/FakeAdapter.cs` plays
  the adapter on in-memory streams, so framing edge cases, timeouts, cancellation, the
  closed state and the engine's event mapping are deterministic and OS-independent
  (`DapClientTests`, `DapEngineTests`).
- **The MCP tool surface through a real MCP client.** `McpInMemory` connects an MCP
  client to a server built through the production registration over a fake engine, so
  the filters and tools run as in production: tool list, titles and hints, result
  shapes, error results, cancellation, audit lines (`McpToolSurfaceTests`,
  `ToolErrorMessagesTests`, `ToolExceptionTests`, `ToolCancellationTests`,
  `ToolResultShapeTests`, `TaskToolsTests`, `AuditLogTests`).
- **The HTTP wiring on an ephemeral port** (`HttpTransportTests`,
  `BearerAuthMiddlewareTests`, `HostFactoryTests`): `401` without a valid token, any
  casing of the scheme, handshake and calls with one, fail-closed start, audited
  refusals.
- **Components with their seams injected:** `NetcoredbgLocator` and `NetcoredbgFetcher`
  (resolution order, strict override, stamp-gated cache, `CLRVOYANT_NO_FETCH`, checksum
  failure), `ProcessLister`, `ChildProcessWatcher` (parent/creation-time rules, retry
  limit), `TestDebugLauncher` (against a small real child process instead of
  `dotnet test`), `BreakpointLocator`, `AssemblyMethodScanner`, `Page`/`PageBuilder`,
  `TaskInspector` status decoding.

## 2. Engine contract tests

`EngineContractTests` states what `Session` relies on from any `IDebugEngine` (a resume
is followed by a stop; `SetBreakpoints` answers one result per request, in order;
`Exited` is raised exactly once, with the exit code) and runs the same assertions
against the in-memory fake (`FakeEngineContractTests`, fast) and the real `DapEngine`
over netcoredbg (`DapEngineContractTests`, `Category=Integration`). The point is that
the fake cannot drift from what the real engine does.

## 3. Integration tests (`Category=Integration`)

They drive a **real** netcoredbg and ClrMD against `samples/SampleApp` (and
`ParentApp`). `TestPaths` locates the artefacts (the bundled engine, the built
samples, the line marked `BREAKPOINT-TARGET`). `DebugSessionFixture` launches
SampleApp once, stops it at the breakpoint and shares that stop across the read-only
inspection tests.

- **Per-runtime theories.** `[TargetRuntimeTheory, AllTargetRuntimes]` runs a test once
  per target framework (net8.0, net9.0, net10.0) for the runtime-sensitive behaviour:
  the netcoredbg control loop and the ClrMD Task and async-graph inspection. A row whose
  runtime is not installed is reported as skipped with the reason; with
  `CLRVOYANT_REQUIRE_TARGET_RUNTIMES=1` (set in CI, where all three are installed) it is
  a failure instead. Other integration tests run on net10.0.
- **Control and lifecycle:** function breakpoint binding with no source line, a rejected
  attach surfacing netcoredbg's real error, `env` reaching the debuggee and surviving a
  restart, exit code and captured output, `debug_stop` killing a launched process but
  only detaching an attached one (and `terminate` overriding it), several concurrent
  sessions, child auto-attach with `ParentApp` ending with the launched tree, the server
  being killed leaving no orphan processes (`ServerKilledTests`), eviction of heap
  snapshots when a process ends. `debug_test` is covered by the unit tests of
  `TestDebugLauncher` and by `scripts/smoke-debug-test.ps1`, not by an integration test.
- **Symbols boundary** (`OptimizedBuildTests`, `NoPdbDebuggingTests`): a PDB, not
  Debug-vs-Release, gates debugging. An optimized build with a PDB binds line
  breakpoints and shows locals; with the PDB stripped neither function nor line
  breakpoints bind, while the ClrMD Task view still works after a `pause`.
- **Native frames** (`NativeFrameCallstackTests`, Windows only): breaks in a WinForms
  `OnLoad` reached from the native window procedure and asserts the stop has file/line
  and `get_callstack` spans the native transition (a regression test for GitHub issue
  #1). Skipped on Linux.

## 4. Packaging tests (`Category=Packaging`, opt-in)

`InstalledToolTests` packs the tool, installs it from a local folder feed, and drives a
real loop (`debug_launch`, `set_breakpoint`, `continue`, `get_callstack`) through the
installed `clrvoyant` over MCP/stdio with `CLRVOYANT_NO_FETCH=1`: the bundled per-RID
engine works **offline**. CI runs it on each of its three legs, so each RID's bundle is
validated on its own native platform.

Two guards sit outside the test suite. Every `dotnet pack` opens the produced `.nupkg` and
fails (deleting it) unless it bundles the engine for all three RIDs. And on the Linux legs
CI starts the bundled engine on the oldest supported bases (the `deploy/Dockerfile` runtime
image and Ubuntu 22.04): the runners' own glibc is newer, so a netcoredbg bump that raises
the Linux baseline would otherwise pass.

## What CI runs

[`ci.yml`](../.github/workflows/ci.yml), on pushes to `main` and on pull requests, in a
matrix of `ubuntu-latest` (linux-x64), `ubuntu-24.04-arm` (linux-arm64, real hardware)
and `windows-latest`:

- installs the .NET 10 SDK plus the 8.0 and 9.0 SDKs (for their runtimes);
- runs the full suite in Release with `CLRVOYANT_PACKAGING_TESTS=1` and
  `CLRVOYANT_REQUIRE_TARGET_RUNTIMES=1`, a 10-minute hang timeout and coverage
  collection (`coverlet.runsettings`, Cobertura) with TRX results;
- writes a coverage table to the job summary and uploads the TRX and Cobertura files as
  an artefact (14 days). **There is no coverage threshold:** coverage is published, not
  enforced;
- on the Linux legs, starts the bundled engine (`netcoredbg --version`) in containers of
  the oldest supported bases: the runtime image named in `deploy/Dockerfile` and Ubuntu
  22.04.

[`codeql.yml`](../.github/workflows/codeql.yml) runs CodeQL for C#, and Dependabot
updates the workflow actions, NuGet packages and the Docker base images
([`dependabot.yml`](../.github/dependabot.yml)). The release workflow runs the suite once
more (on Linux x64, with the packaging test) before packing; see
[the changelog](../CHANGELOG.md) and the header of `release.yml` for the procedure.

## Coverage

Coverage is measured with coverlet using [`coverlet.runsettings`](../coverlet.runsettings):
the four product assemblies are included; the test project and `SampleApp` are
excluded, as are `Program.cs` (the entry shim) and code carrying
`GeneratedCodeAttribute`, `ExcludeFromCodeCoverageAttribute` or
`CompilerGeneratedAttribute`; auto-properties are skipped. Nothing else is excluded.

A dated measurement, not a guarantee: on **2026-11-25**, on Windows, the suite had
437 tests and 95.2 % line / 85.2 % branch coverage. The two coverage figures were read
from the Cobertura reports of that day's local runs; the test count is as recorded by
the maintainer's run. Coverage is a by-product: tests are written for behaviour (the
wait model, the breakpoint store, Task decoding, real-engine flows), and the number is
reported, not targeted.

## End-to-end smokes (`scripts/`)

PowerShell scripts that drive the **built server** over its real transport.
`scripts/mcp-driver.ps1` is a reusable MCP-stdio harness (`Handshake`, `Call`,
`Stop-Driver`); `scripts/smoke-common.ps1` resolves the server. Each smoke takes
`-ServerPath` (the apphost or `ClrVoyant.Server.dll`), and the HTTP smoke a `-Port`;
otherwise they use the build output of the given `-Configuration`/`-Framework`.

| Script | Proves |
|---|---|
| `smoke-new-tools.ps1` | `get_debug_instructions`, a content breakpoint, `restart_debugging`, `clear_all_breakpoints` against SampleApp. |
| `smoke-debug-test.ps1` | `debug_test` runs a suspended test and attaches; a breakpoint hits in the test host. |
| `smoke-http.ps1` | HTTP transport: fail-closed without a token, `401` without or with a wrong bearer, `initialize` accepted with the right one. |

The smokes are manual aids, not part of CI.

## Spikes

`spike/` holds the historical de-risking experiments the ADRs cite. They are not built
by the solution and not maintained; see [spike/README.md](../spike/README.md).

## Gotcha: orphaned debug processes

Do **not** pipe a smoke `.ps1` through `Select-Object -First N`: it disposes the
pipeline and kills the smoke's `pwsh` mid-run, leaking `netcoredbg`/`SampleApp`
processes. Those orphans then make integration tests time out ("Running, not stopped").
If integration tests fail oddly:

```pwsh
Get-Process -Name netcoredbg,SampleApp -ErrorAction SilentlyContinue | Stop-Process -Force
```

and re-run. Let smokes run to completion instead of truncating their output.
