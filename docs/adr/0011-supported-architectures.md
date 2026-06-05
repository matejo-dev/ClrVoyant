# ADR-0011: Supported architectures (x64 + linux-arm64)

- **Status:** Accepted
- **Date:** 2026-06-04

## Context

The debug stack is native and architecture-coupled: ClrMD loads the target's
architecture-specific DAC and netcoredbg is a local ICorDebug debugger. So **the
tool must run at the same architecture as the process it debugs** — there is no
cross-architecture debugging. The first cut pinned everything to x64
(`PlatformTarget=x64`, an x64-only netcoredbg asset), which excludes the growing
arm64 Linux server fleet (AWS Graviton, Azure Ampere, arm64 Kubernetes nodes) —
exactly the POD/remote scenario we ship ([0010](0010-pod-remote-debug-topology.md)).

## Decision

Support three host/target architectures, each tool instance matching its target:

| Platform | Supported | netcoredbg asset |
|---|---|---|
| `win-x64` | ✅ | `netcoredbg-win64.zip` |
| `linux-x64` | ✅ | `netcoredbg-linux-amd64.tar.gz` |
| `linux-arm64` | ✅ | `netcoredbg-linux-arm64.tar.gz` |
| `win-arm64`, macOS | ❌ | no upstream build we depend on |

- Drop the `PlatformTarget=x64` pin: the server/tests run AnyCPU, i.e. at the host
  architecture, which is what must match the co-located target.
- Both fetch paths are architecture-aware off the **running/build process arch**:
  the runtime fetch (`NetcoredbgFetcher`) and the build-time fetch
  (`Directory.Build.props` + `tools/fetch-netcoredbg.sh`) select the asset and its
  pinned SHA-256 accordingly. SHAs for all three assets are pinned in
  `Directory.Build.props` (single source of truth) and baked into the assembly.

## Consequences

- arm64 Linux apps (incl. in-POD) are debuggable by an arm64 sidecar/tool, with the
  full Tasks/async view. Build the arm64 image with
  `docker buildx build --platform linux/arm64`.
- **Validation:** the authoritative check is a dedicated **arm64 CI leg**
  (`ubuntu-24.04-arm`, real hardware) running the full unit + integration suite on
  every push. The local QEMU spike (`spike/linux/run-arm64.ps1`) is **not**
  authoritative: it confirmed the arm64 build, asset fetch, netcoredbg launch and
  DAP handshake, but QEMU user-mode emulation aborts (signal 6) in netcoredbg's
  ptrace path before the breakpoint/heap-read step — a known emulation limitation,
  not an arm64 verdict. Treat `linux-arm64` as supported once the arm64 CI leg is
  green; until then it is "wired up, pending hardware validation".
- A single dotnet-tool package stays cross-platform AND offline: publish/pack bundle
  the engine for **all three RIDs** under `tools/netcoredbg/<rid>/` (staged with
  built-in MSBuild tasks at publish time), and `NetcoredbgLocator` picks the host's.
  The first-run fetch is only a last-resort fallback. See [ADR-0006](0006-bundle-netcoredbg.md).
- This also fixes the earlier cross-publish hazard: because every RID is staged
  regardless of the build host's architecture, `dotnet publish`/`pack` from an x64
  host no longer bundles the wrong (x64) engine for an arm64 target. The container
  image still builds inside the target-arch (buildx) image for the rest of its
  payload, but the engine bundle is correct either way.

## Alternatives considered

- **Stay x64-only.** Simplest, but excludes the arm64 cloud fleet that is the
  natural home of the remote/POD use case. Rejected.
- **AnyCPU with no arch-aware fetch.** Would launch on arm64 but then download the
  x64 engine and mismatch ClrMD — broken. Rejected; arch-awareness is required.
- **Support `win-arm64` / macOS.** No netcoredbg build we rely on; low demand for a
  .NET *server* debugger. Deferred until upstream + demand exist.
