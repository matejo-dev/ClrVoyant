# ADR-0006: Bundle netcoredbg rather than depend on vsdbg/PATH

- **Status:** Accepted
- **Date:** 2026-06-03

## Context

The control engine needs a debug backend present at runtime. Options: require it on
`PATH`, depend on the user's IDE debugger (vsdbg), or ship our own.

## Decision

**Bundle netcoredbg** (Samsung, MIT) with the server. `NetcoredbgLocator` resolves
it in order:
1. `CLRVOYANT_NETCOREDBG` env var (explicit override), else
2. a per-RID bundle `<app>/tools/netcoredbg/<rid>/netcoredbg[.exe]` — how the .NET
   global tool ships, see below, else
3. a flat bundle `<app>/tools/netcoredbg/netcoredbg[.exe]` — dev build output, the
   container image, and the spike all bundle a single RID flat, else
4. a per-user cache, fetching the pinned engine on first run (a last-resort fallback).

**The published .NET tool bundles netcoredbg for EVERY supported RID** (`win-x64`,
`linux-x64`, `linux-arm64`) under `tools/netcoredbg/<rid>/` (single package, option
"C"). `dotnet pack` stages all three at publish time with built-in MSBuild tasks
(`DownloadFile`/`GetFileHash`/`Unzip` + `tar`), SHA-256 verified — no PowerShell or
bash, so it runs in the `dotnet/sdk` container image too. The tool therefore installs
and runs **offline** with no runtime download; the fetch (step 4) only covers a host
with no bundled match. See [ADR-0011](0011-supported-architectures.md) for the RID set.

The Linux container image downloads the build for its architecture into the same
location (see [0010](0010-pod-remote-debug-topology.md)).

## Consequences

- The server is self-contained and works in locked-down / air-gapped environments:
  no PATH setup, no IDE dependency, no github.com at the user's runtime, MIT-licensed
  and shippable (including into a container).
- The single package carries all three RIDs (~12–13 MB), so every user downloads
  platforms they won't run. Accepted in exchange for one install command, maximum
  feed compatibility, and zero install-time RID resolution (vs RID-specific tool
  packages, option "B", which would be leaner per-user but rely on a newer SDK
  feature and trickier feed behaviour).
- We track netcoredbg releases ourselves and carry its quirks (async-line
  breakpoint binding, `pause` on a running target — documented limitations).
- Per-OS binary: `netcoredbg.exe` on Windows, `netcoredbg` on Linux; the locator
  picks by RID.

## Update (2026-06-05)

Originally the package bundled only the pack host's RID and fell back to a github.com
fetch for every other host — which broke offline/corporate installs. Reworked to the
all-RID bundle above so the shipped tool is genuinely offline-capable. A packaging
test (`InstalledToolTests`) now packs, installs from a local feed, and drives a real
debug loop through the installed tool with `CLRVOYANT_NO_FETCH=1` to prove it.

## Alternatives considered

- **vsdbg** — strictly better `evaluate`, but license-locked to VS / VS Code;
  cannot redistribute. Rejected (kept as a documented fallback only if netcoredbg
  proves too weak).
- **Require netcoredbg on PATH** — fragile onboarding; defeats "self-contained".
  Rejected (still allowed via the env override).

## Update (2026-11-25)

- **Lazy resolution.** `NetcoredbgLocator` runs on the **first session start**, not at
  server start. The server comes up and answers `initialize` / `tools/list` even where
  the engine is missing, and a first-run download never happens inside the client's
  handshake window. A failure is the error of the first `debug_launch` /
  `debug_attach` / `debug_test`; only a success is remembered, so the next session
  start looks again.
- **`CLRVOYANT_NETCOREDBG` is strict.** When set, the file must exist; otherwise
  resolution fails with a message. It never falls through to another source.
- **The runtime download cache is trusted only with its stamp.** The cache directory
  (`<CLRVOYANT_CACHE_DIR or ~/.clrvoyant>/netcoredbg/<version>`) counts as complete only
  if it holds both the engine and `netcoredbg-<version>.stamp`, written last. A cache
  populated by hand, or by an older release that did not write the stamp, is
  **incomplete: it is refetched once, and under `CLRVOYANT_NO_FETCH` resolution fails**
  (the message names the files the cache must hold). This is deliberate (an interrupted
  extraction must never be trusted) but it is a behaviour change for air-gapped users
  with a hand-filled cache.
- **Known gap: a crashed fetch can leave a work directory behind.** The fetch extracts
  into a sibling `<version>.<guid>.tmp` directory and renames it into place. A normal
  failure deletes it; if the process is killed mid-fetch it stays, and nothing sweeps
  such directories. It is harmless to correctness (it is never trusted) but uses disk.
