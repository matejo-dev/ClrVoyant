# ADR-0010: Remote/POD debugging topology (sidecar)

- **Status:** Accepted
- **Date:** 2026-06-04

## Context

We want an agent to debug a .NET app running in Kubernetes. There are two distinct
links: **agent ↔ server** (can be remote) and **server ↔ target process** (cannot:
netcoredbg and ClrMD read the process locally via ptrace / `/proc`, with no network
attach). So the server must be co-located with the app.

## Decision

Run ClrVoyant as a **sidecar container in the same POD** as the app:

- **`shareProcessNamespace: true`** so the sidecar sees the app's process in
  `/proc` (enables `list_processes` → `debug_attach`).
- **`CAP_SYS_PTRACE`** on the sidecar so netcoredbg can attach and ClrMD can read
  `/proc/<pid>/mem` (see [0008](0008-cross-os-heap-read.md)).
- The agent reaches the sidecar over **HTTP + bearer**
  ([0009](0009-http-transport-mandatory-auth.md)), kept **cluster-internal**
  (ClusterIP + `kubectl port-forward`, never a public Ingress).
- The **app image must ship PDBs** for source-level debugging.

Artifacts: `deploy/Dockerfile` (publishes the server, which bundles netcoredbg for
all supported RIDs under `tools/netcoredbg/<rid>/`; the image uses its own arch),
`deploy/clrvoyant-sidecar.yaml`, `deploy/README.md`. The full data flow is in
`docs/remote-debugging-pod.md`.

## Consequences

- HTTP makes only the *agent* remote; the engine stays local to the target —
  validated by the Linux spike and the end-to-end `ServerSmoke`.
- Requires elevated POD settings (`shareProcessNamespace`, `SYS_PTRACE`) that
  hardened clusters drop by default — a deliberate, operator-granted capability.
- This is a **break-glass / non-prod** capability: an authenticated caller gets
  code execution in the POD. Governance lives in `docs/security.md`.

## Alternatives considered

- **ClrVoyant outside the POD, attach across the boundary** — impossible: no network
  attach for netcoredbg/ClrMD. Rejected.
- **Same container (app + debugger in one image)** — simplest PID namespace but
  couples debugger to the app image. Documented as an option; sidecar is the default
  for decoupling.
- **Ephemeral debug container** (`kubectl debug`) — attractive (no standing
  sidecar) but the process/PID-namespace and capability story is the same; a future
  variant, not the initial decision.
