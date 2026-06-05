# ClrVoyant documentation

ClrVoyant is a local MCP server that lets an agent deterministically debug .NET
8/9/10 apps — breakpoints, stepping, variables/call stack/threads, and full async
`Task` introspection — headless, on Windows or Linux, locally or inside a POD.

Start with the root [README](../README.md) for install/quickstart. This folder is
the deeper reference.

## Index

| Document | What it covers |
|---|---|
| [architecture.md](architecture.md) | The system as built: projects, the two-engine design, wait-based model, cross-OS heap read, transports. |
| [tools.md](tools.md) | Complete reference of the 34 MCP tools, grouped, with parameters and semantics. |
| [comparison.md](comparison.md) | Honest "what you get / what you give up" vs other MCP debuggers. |
| [remote-debugging-pod.md](remote-debugging-pod.md) | How Docker/Kubernetes debugging works (the two-link model, sidecar, end-to-end flow). |
| [security.md](security.md) | Threat model and controls — required reading before exposing HTTP. |
| [testing.md](testing.md) | Unit / integration / smoke layers and the de-risking spikes. |
| [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) | The original phased plan (historical; architecture.md is the current state). |
| [adr/](adr/README.md) | Architecture Decision Records — the significant decisions and their trade-offs. |

## Related, outside `docs/`

- [`deploy/`](../deploy/README.md) — operational quickstart for the POD sidecar
  (Dockerfile, manifest, commands).
- [`spike/`](../spike) — the reproducible de-risking proofs (Windows + `spike/linux`).
- [`scripts/`](../scripts) — the MCP driver and end-to-end smokes.

## Reading paths

- **New to the project?** root README → [architecture.md](architecture.md) →
  [tools.md](tools.md).
- **Why is it built this way?** [adr/](adr/README.md).
- **Deploying to a cluster?** [remote-debugging-pod.md](remote-debugging-pod.md) →
  [security.md](security.md) → [`deploy/`](../deploy/README.md).
- **Contributing/changing it?** [architecture.md](architecture.md) →
  [testing.md](testing.md).
