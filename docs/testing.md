# Testing strategy

Layers — fast unit tests, real-engine integration tests (including an opt-in
packaging layer that exercises the installed tool), and end-to-end spikes/smokes —
plus the de-risking spikes that gate risky directions.

## 1. Unit tests (`tests/ClrVoyant.Tests`, xUnit)

Pure logic, no netcoredbg. The control engine is faked
(`Fakes/FakeDebugEngine.cs`, an in-memory `IDebugEngine`) so `Session` /
`SessionManager` behaviour is deterministic:

- Wait-based model: continue returns enriched stop / times out / reports exit;
  step records kind; spontaneous vs targeted stops.
- Breakpoint store: stable ids, full-set re-send, clear, restart re-applies.
- `BreakpointLocator` (content→line resolution), `ProcessLister` (lists self as
  .NET), `OwnedResource` disposal on stop, child-process auto-attach matching
  (`AutoAttachTests`).
- `HostFactory`: DI wiring, and the HTTP transport **fails closed** without
  `CLRVOYANT_AUTH_TOKEN` (the RCE guard).
- `AssemblyMethodScanner` (no-source discovery): finds a real method by full name,
  applies type/method filters, skips compiler-generated members, honours the cap.
- `NetcoredbgLocator`: env override, per-RID bundle preferred over flat, fail-closed
  when fetching is disabled.

## 2. Integration tests (real netcoredbg + ClrMD)

Marked `[Trait("Category","Integration")]`. They drive a **real** netcoredbg and
ClrMD against `samples/SampleApp`. `TestPaths` locates the repo artifacts
(bundled netcoredbg, built SampleApp, the `BREAKPOINT-TARGET` line).
`DebugSessionFixture` (an `IClassFixture`) launches SampleApp once, stops it at the
breakpoint, and shares that stop across the read-only inspection tests (threads,
call stack, scopes/variables/evaluate, `list_tasks`, async graph). Control-flow tests
add: a **function breakpoint** binding and hitting `Calc.Compute` with no source line
(the no-source path), and a rejected `debug_attach` surfacing netcoredbg's real error
(not a hang or a generic message).

### Packaging tests (opt-in: `CLRVOYANT_PACKAGING_TESTS=1`)
`InstalledToolTests` is the end-to-end check of the **shipped artifact**: it packs
the tool, installs it from a local folder feed, then drives a real debug loop
(`debug_launch` → `set_breakpoint` → `continue` → `get_callstack`) through the
installed `clrvoyant` over MCP/stdio with `CLRVOYANT_NO_FETCH=1` — proving the
bundled per-RID engine works **offline**. Skipped unless the env var is set; CI runs
it on every leg (Windows / linux-x64 / linux-arm64), so each RID's bundle is
validated on its own native platform.

Run everything:
```pwsh
dotnet test tests/ClrVoyant.Tests --settings coverlet.runsettings
```
Coverage is measured with coverlet (generate a report with `reportgenerator`; DTO
records and `Program.cs` are excluded). Treat coverage as a by-product — the suite
targets behaviours (the wait model, breakpoint store, Task decoding, real-engine
flows), not a percentage. Tests are not added to inflate the number; trivial
getters/passthroughs are intentionally left untested.

### Debugging the tests themselves
The test host suspends with `VSTEST_HOST_DEBUG`; `debug_test` (or the manual
recipe) attaches to it. Verified by debugging ClrVoyant's own suite.

## 3. End-to-end smokes (`scripts/`)

Drive the **real server** over its actual transport. `scripts/mcp-driver.ps1` is a
reusable MCP-stdio harness (`Handshake` / `Call` / `Stop-Driver`).

| Script | Proves |
|---|---|
| `smoke-new-tools.ps1` | content breakpoint, `restart_debugging`, `clear_all_breakpoints`, `get_debug_instructions` against SampleApp. |
| `smoke-debug-test.ps1` | `debug_test` runs a suspended test and attaches; breakpoint hits in the test host. |
| `smoke-http.ps1` | HTTP transport: fail-closed without a token, `401` without/with a wrong bearer, `initialize` accepted with the right bearer. |

## 4. Spikes (`spike/`)

De-risk before building. Kept in the repo as reproducible evidence.

- **Windows coexistence** (`spike/`): netcoredbg holds a process at a breakpoint
  while ClrMD snapshots it and enumerates Tasks — the core feasibility proof.
- **Linux coexistence + capstone** (`spike/linux/`, `run.ps1` in Docker):
  `CoexistDriver` + `ClrMdLinuxProbe` prove all four Linux heap-read mechanisms
  coexist with netcoredbg (`FINDINGS.md`); `ServerSmoke` then drives the **real
  published server** on Linux end-to-end through `list_tasks`.
- **ARM64** (`spike/linux/run-arm64.ps1`): the same Linux spike under
  `--platform linux/arm64`. On an x64 host this is QEMU emulation, which can't
  complete netcoredbg's ptrace path (inconclusive — see `FINDINGS.md`); the
  authoritative arm64 check is the `ubuntu-24.04-arm` CI leg on real hardware,
  which runs the full unit + integration suite (alongside the x64/Windows legs).

## Gotcha: orphaned debug processes

Do **not** pipe a smoke `.ps1` through `Select-Object -First N` — it disposes the
pipeline and kills the smoke's `pwsh` mid-run, leaking `netcoredbg`/`SampleApp`
processes. Those orphans then make integration tests time out ("Running, not
stopped"). If integration tests fail oddly:
```pwsh
Get-Process -Name netcoredbg,SampleApp -ErrorAction SilentlyContinue | Stop-Process -Force
```
and re-run. Let smokes run to completion instead of truncating their output.
