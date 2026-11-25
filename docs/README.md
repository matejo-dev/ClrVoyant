# ClrVoyant documentation

ClrVoyant is a local MCP server that lets an agent deterministically debug .NET
8/9/10 apps — breakpoints, stepping, variables/call stack/threads, and full async
`Task` introspection — headless, on Windows or Linux, locally or inside a POD.

Start with the root [README](../README.md) for install/quickstart. This folder is
the deeper reference.

## Index

| Document | What it covers |
|---|---|
| [architecture.md](architecture.md) | The system as built: projects, the two-engine design, the request pipeline, wait-based model, session ownership, lazy engine resolution, transports. |
| [tools.md](tools.md) | Complete reference of the 35 MCP tools, grouped, with parameters, result shapes, errors and paging. |
| [comparison.md](comparison.md) | What you get / what you give up versus other MCP debuggers, including the .NET-specific ones; dated, with sources. |
| [remote-debugging-pod.md](remote-debugging-pod.md) | How Docker/Kubernetes debugging works (the two-link model, sidecar, end-to-end flow). |
| [security.md](security.md) | Threat model, controls and release verification: required reading before exposing HTTP. |
| [testing.md](testing.md) | The test layers, how to run the fast subset, what CI runs, coverage. |
| [archive/IMPLEMENTATION_PLAN.md](archive/IMPLEMENTATION_PLAN.md) | The pre-implementation plan. Superseded, kept for history; not a reference. |
| [adr/](adr/README.md) | Architecture Decision Records — the significant decisions and their trade-offs. |

## Related, outside `docs/`

- [`deploy/`](../deploy/README.md) — operational quickstart for the POD sidecar
  (Dockerfile, manifest, commands).
- [`spike/`](../spike/README.md) — historical de-risking experiments cited by the ADRs
  (not built, not maintained).
- [`CHANGELOG.md`](../CHANGELOG.md) and [`SECURITY.md`](../SECURITY.md) — release notes
  and how to report a vulnerability.
- [`scripts/`](../scripts) — the MCP driver and end-to-end smokes.

## Reading paths

- **New to the project?** root README → [architecture.md](architecture.md) →
  [tools.md](tools.md).
- **Why is it built this way?** [adr/](adr/README.md).
- **Deploying to a cluster?** [remote-debugging-pod.md](remote-debugging-pod.md) →
  [security.md](security.md) → [`deploy/`](../deploy/README.md).
- **Contributing/changing it?** [architecture.md](architecture.md) →
  [testing.md](testing.md).
