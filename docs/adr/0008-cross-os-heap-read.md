# ADR-0008: Cross-OS heap read (Windows snapshot vs Linux passive)

- **Status:** Accepted
- **Date:** 2026-06-04

## Context

The Tasks feature reads the target's heap with ClrMD *while netcoredbg holds the
process stopped*. On Windows the spike used `DataTarget.CreateSnapshotAndAttach` —
a PSS process snapshot (a copy-on-write clone), a Windows-only API. To run inside a
Linux POD we needed an equivalent. Two Linux concerns: PSS does not exist, and
Linux nominally allows only **one ptrace tracer** per process — netcoredbg already
holds it.

## Decision

Open the ClrMD view per-OS (`TaskInspector.OpenDataTarget`):

- **Windows:** `DataTarget.CreateSnapshotAndAttach(pid)` — frozen snapshot.
- **Linux:** `DataTarget.AttachToProcess(pid, suspend: false)` — a **passive,
  read-only** read of `/proc/<pid>/mem`, taking **no** second ptrace stop.

A Linux spike (see `spike/linux/FINDINGS.md`) proved coexistence: while netcoredbg
held the process, the passive read (and `createdump`, and the diagnostic-IPC dump)
all enumerated Tasks correctly. netcoredbg debugs via ICorDebug, not an exclusive
ptrace lock, so it does not block ClrMD's reads.

## Consequences

- The differentiating Tasks feature works on both OSes; the production server was
  validated end-to-end on Linux (`spike/linux/ServerSmoke`).
- On Linux the read is live, not a frozen copy — but the target is held stopped by
  netcoredbg for the read's duration, so it is consistent. The per-`(pid, stopId)`
  cache still holds.
- Prefer the read-only path; the invasive `suspend:true` attach also works but can
  perturb the target and is not used in production.
- Needs `CAP_SYS_PTRACE` to read `/proc/<pid>/mem` of a process it doesn't own —
  see [0010](0010-pod-remote-debug-topology.md).

## Alternatives considered

- **createdump → LoadDump** (works) and **DiagnosticsClient.WriteDump → LoadDump**
  (works, no ptrace at all) — kept as proven fallbacks; heavier (write a full core)
  than a passive read. Not the default.
- **Detach netcoredbg, snapshot, reattach** — loses the "same stop" guarantee and
  perturbs control. Rejected.
