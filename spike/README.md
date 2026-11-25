# spike/

Historical de-risking experiments, kept as evidence for decisions recorded in the ADRs.

- They are **not part of the product**, are **not built by `ClrVoyant.slnx`**, and are
  **not maintained**. They may not build or run against the current dependency versions
  or SDK; nothing in CI exercises them.
- Do not treat them as examples of how the server works. The server's behaviour is
  described in [docs/architecture.md](../docs/architecture.md) and tested by
  `tests/ClrVoyant.Tests`.
- The spikes themselves are left as they were when the findings were produced.

## What is here and which ADR cites it

| Path | Question it answered | Cited by |
|---|---|---|
| `ClrMdProbe/`, `DapDriver/`, `TaskZoo/` (Windows) | Can ClrMD snapshot a process and enumerate its `Task`s while netcoredbg holds it at a breakpoint? (`TaskZoo` is the target; `DapDriver` drives netcoredbg; `ClrMdProbe` reads the heap.) | [ADR-0002](../docs/adr/0002-two-engines-one-contract.md), [ADR-0008](../docs/adr/0008-cross-os-heap-read.md) (Windows side) |
| `linux/ClrMdLinuxProbe/`, `linux/CoexistDriver/`, `linux/run.sh`, `linux/run.ps1`, `linux/FINDINGS.md` | On Linux, which ClrMD read mechanisms coexist with netcoredbg holding the process? | [ADR-0008](../docs/adr/0008-cross-os-heap-read.md), [ADR-0010](../docs/adr/0010-pod-remote-debug-topology.md), [ADR-0002](../docs/adr/0002-two-engines-one-contract.md) |
| `linux/ServerSmoke/`, `linux/run-server-smoke.sh` | Does the real published server drive a Linux debuggee end to end, through `list_tasks`? | [ADR-0008](../docs/adr/0008-cross-os-heap-read.md), [ADR-0010](../docs/adr/0010-pod-remote-debug-topology.md) |
| `linux/run-arm64.ps1` | The same Linux spike under `--platform linux/arm64` (QEMU emulation on an x64 host). Inconclusive by design; real hardware in CI is the authority. | [ADR-0011](../docs/adr/0011-supported-architectures.md) |

## netcoredbg versions

The findings and the scripts in this folder do **not** use the same netcoredbg version:

- `linux/FINDINGS.md` records that its results were produced with **netcoredbg
  3.1.3-1** (linux-amd64), on an x64 container, on 2026-06-04.
- `linux/run.sh` and `linux/run-server-smoke.sh` now pin **netcoredbg 3.2.0-1092**. The
  scripts were bumped with the product's pin; the findings were **not** re-run with that
  version. Whether the conclusions hold for it is therefore not established by the
  spikes. The product's own test suite, which runs against its pinned engine on every
  CI leg, is the current evidence.
- The Windows spike does not record which netcoredbg version it used.

Other details of `FINDINGS.md` are also frozen at the time it was written: it refers to a
breakpoint at "`Program.cs:37`" of `samples/SampleApp`, a line that has since moved (the
tests find the line marked `BREAKPOINT-TARGET`), and its container was the .NET 8 SDK
image. The Linux scripts still start `mcr.microsoft.com/dotnet/sdk:8.0`, while the
repository pins a .NET 10 SDK (`global.json`) and the server targets net10.0, so
`linux/run-server-smoke.sh` in particular is expected not to work as it stands (not
tried).
