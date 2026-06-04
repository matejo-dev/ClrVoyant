# Linux coexistence spike — findings

**Question (the risk the Windows spike never covered):** the Windows spike proved
netcoredbg + ClrMD coexistence using `DataTarget.CreateSnapshotAndAttach` — a
**Windows-only** API (PSS process snapshot). On Linux that API does not exist, and
Linux nominally allows only **one ptrace tracer per process**. So: while netcoredbg
holds a .NET process stopped, can ClrMD still read the heap and enumerate Tasks?

**Answer: YES — via every mechanism tried.**

## Setup
- Container: `mcr.microsoft.com/dotnet/sdk:8.0` (Linux x64), run with
  `--cap-add=SYS_PTRACE --security-opt seccomp=unconfined`.
- Engine: `netcoredbg` 3.1.3-1 (linux-amd64, fetched from Samsung releases).
- Target: `samples/SampleApp` — parks async Tasks (`AwaitForever` →
  WaitingForActivation, a Faulted task) then loops in `Compute()`.
- Driver: `CoexistDriver` stops at entry, binds a breakpoint on the **synchronous,
  looped** line `Program.cs:37`, continues, and at the breakpoint stop (netcoredbg
  owning the process) runs `ClrMdLinuxProbe` against the same PID.

## Result — all PASS, 9 Tasks each (4 RanToCompletion, 3 WaitingForActivation, 2 Faulted)

| Mechanism | Works while netcoredbg holds the process? | Notes |
|---|---|---|
| `AttachToProcess(suspend:false)` — passive `/proc/pid/mem` read | ✅ | Closest analog to the Windows snapshot: read-only, no second ptrace stop. **Preferred.** |
| `createdump -f core <pid>` → `LoadDump` | ✅ | Runtime's own tool writes an ELF core; target stays alive. Safe. |
| `DiagnosticsClient.WriteDump` (diagnostic IPC) → `LoadDump` | ✅ | Runtime self-dumps over its IPC socket; no ptrace at all. |
| `AttachToProcess(suspend:true)` — ClrMD's own ptrace stop | ✅ | Even the invasive path coexisted. Most disruptive — see caveats. |

netcoredbg control plane on Linux also works: breakpoint bound and hit, call stack
read (`Compute | MoveNext | ...`).

## Why coexistence works (vs. the feared single-tracer conflict)
netcoredbg debugs via the runtime's **ICorDebug** services, not a raw gdb-style
global ptrace lock, so it does not exclude ClrMD's reads. Passive reads and core
dumps never contend for ptrace at all.

## Caveats / not yet de-risked
1. **ptrace policy is a separate deployment item.** We ran with `SYS_PTRACE` +
   `seccomp=unconfined` on purpose, to isolate the coexistence question. In a
   hardened cluster, `CAP_SYS_PTRACE` (and `shareProcessNamespace` for a sidecar)
   are often dropped — still required for this to work in a POD.
2. **Prefer a non-invasive mechanism in production** (passive read or createdump).
   The invasive `suspend:true` ClrMD attach ran last and the target exited 1 on
   teardown; the read-only paths are the safe choice.
3. **ClrMD reported `CLR 0.0`** for the version string but walked the heap and read
   `Task.m_stateFlags` correctly — cosmetic version-detection issue to revisit.
4. Breakpoints on **async** state-machine lines were unreliable on this netcoredbg
   build; a **synchronous looped** line bound and hit reliably. `pause` on a freely
   running target timed out (matches the known netcoredbg limitation). Neither
   affects the coexistence conclusion.

## Implication for the POD/remote-debug direction
The core differentiator (ClrMD Task enumeration) is viable on Linux while
netcoredbg drives the process. The remaining work for "debug a POD" is therefore
**not** about the engine coexistence (de-risked here) but about: a Linux build of
the server + bundled linux-amd64 netcoredbg, the ptrace/PID-namespace deployment
story, and auth on the HTTP transport.

## Reproduce
```pwsh
pwsh spike/linux/run.ps1   # builds + runs everything in Docker (host arch)
```

## ARM64 (2026-06-04) — QEMU run is INCONCLUSIVE; CI on real hardware is the gate
Ran the same spike under `--platform linux/arm64` on an x64 host (QEMU emulation)
via `spike/linux/run-arm64.ps1`. Confirmed on emulated arm64: the build, the pinned
`netcoredbg-linux-arm64` fetch + SHA, netcoredbg launch, DAP `initialize`, and the
target running. But `configurationDone` then failed and QEMU aborted with
`uncaught target signal 6 (Aborted)` in netcoredbg's ptrace path — **before** the
breakpoint/heap-read step. This is a known QEMU user-mode limitation for
ptrace-heavy debuggers, **not** an arm64 verdict. Authoritative arm64 validation is
the `ubuntu-24.04-arm` CI leg (real hardware) running the full suite — see
[ADR-0011](../../docs/adr/0011-supported-architectures.md).
```pwsh
pwsh spike/linux/run-arm64.ps1   # arm64 under QEMU; expect the signal-6 abort above
```
