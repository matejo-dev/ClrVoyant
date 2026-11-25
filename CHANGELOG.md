# Changelog

All notable changes to ClrVoyant are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The project is pre-1.0:
minor versions may contain breaking changes, and they are listed first.

To release: bump `<Version>` in `src/ClrVoyant.Server/ClrVoyant.Server.csproj`, merge,
and tag the merge commit with the same version (`v<version>`); the tag must equal the
project version or the release workflow fails.

## [Unreleased]

### Breaking

- **The tool needs the .NET 10 runtime, including ASP.NET Core**, or a newer one through
  roll-forward (it targeted .NET 8 before). Debug *targets* are unchanged: .NET 8, 9 and
  10. .NET 8 and 9 left Microsoft support on 2026-11-10; they remain supported as targets.
  .NET 11 is not validated.
- **`list_tasks`, `get_async_graph`, `list_methods` and `get_variables` return a page
  object** `{ items, total?, truncated, nextOffset? }` instead of a bare array, and take
  `max` and `offset`. `get_async_callstack` is unchanged. See
  [docs/tools.md](docs/tools.md#paging).
- **`wait_for_any_stop` has a new result shape.** It is `{ sessionId, state, stop?,
  exitCode?, message? }` (it was `{ sessionId, stop }`); `stop` is absent when the event
  is a process exit (`state: "Exited"`) or a lost debugger connection
  (`state: "Faulted"`). `{ "timedOut": true }` on timeout is unchanged. Clients that read
  `stop` without looking at `state` must be updated.
- **`debug_stop` no longer kills a process the session merely attached to.** By default
  it kills only debuggees this server started (`debug_launch`, the `debug_test` host, and
  auto-attached children of those) and detaches from attached ones, leaving them running.
  Pass `terminate: true` to kill an attached process, or `false` to detach from a launched
  one.
- **HTTP binds `127.0.0.1:3001` by default** (it bound all interfaces). To listen
  elsewhere set `CLRVOYANT_HTTP_URL`. The container image sets `http://0.0.0.0:3001`
  explicitly, so the sidecar is unaffected.
- **`CLRVOYANT_NETCOREDBG` pointing to a missing file is an error** (it used to be
  ignored and another engine used). Unset it, or point it at the executable.
- **A hand-populated or older-release download cache is refetched once.** The runtime
  download cache is now trusted only with its version stamp file; a cache without it is
  incomplete, and under `CLRVOYANT_NO_FETCH` resolution then fails. Air-gapped users with
  a pre-populated cache must provide the stamp (`netcoredbg-<version>.stamp`, next to the
  engine) or use `CLRVOYANT_NETCOREDBG`.

### Added

- Tool `get_output`: the captured stdout/stderr tail of the debuggee (64 KiB cap), with
  the session state and exit code. **35 tools** now.
- `debug_launch` accepts `env`, environment variables for the debuggee, kept across
  `restart_debugging`.
- `debug_stop` accepts `terminate`.
- `continue` and `step_*` results carry `exitCode` and `message`; `debug_status` and
  `list_sessions` carry `exitCode` once the process has exited.
- `list_processes` includes each process's `commandLine`.
- Every tool has an MCP title and read-only / destructive / idempotent hints; all tools
  honour client cancellation.
- The playbook is also sent as the MCP server instructions in the `initialize` handshake
  (`get_debug_instructions` remains for clients that do not show them).
- Packaging: the NuGet package ships `LICENSE` and `THIRD-PARTY-NOTICES.md`, and a symbol
  package (`.snupkg`) is produced.
- Releases: `SHA256SUMS`, a build-provenance attestation and a GitHub Release for each
  tag; publishing to NuGet.org uses Trusted Publishing. See
  [docs/security.md](docs/security.md#supply-chain).
- `SECURITY.md` (how to report a vulnerability), CodeQL, Dependabot, and coverage and test
  results published from CI. Install scripts back up the client configuration before
  changing it; smoke scripts take `-ServerPath` and `-Port`.

### Changed

- Tool failures reach the client as error results carrying the actionable message,
  instead of the generic "An error occurred invoking '<tool>'" the SDK substituted.
  Unexpected exceptions are prefixed with their type; stack traces stay in the server log.
  ([ADR-0012](docs/adr/0012-tool-errors-as-results.md))
- `continue` and `step_*`: a second concurrent resume on a session is rejected; a client
  cancelling the call is no longer reported as a timeout; a lost debugger connection ends
  a waiting call promptly with state `Faulted`.
- `wait_for_any_stop` no longer returns events that are no longer true (the session was
  resumed, stopped again, restarted or ended since).
- A DAP request that gets no answer fails after 30 seconds, and every request fails at
  once, with netcoredbg's exit code and stderr tail, when netcoredbg dies, instead of
  hanging.
- A failed `debug_launch` / `debug_attach` leaves no session behind (and a failed attach
  never kills the process it tried to attach to). A failed `restart_debugging` leaves
  the session `Exited` with its breakpoints.
- `debug_test` fails as soon as `dotnet test` exits, with its exit code and the end of its
  output, instead of waiting for the timeout.
- Auto-attach adopts a process only if it was created after its parent, and gives up on a
  process after 3 failed attaches. An auto-attached child inherits ownership from its
  parent session (see `debug_stop` above).
- netcoredbg is located on the first session start, not when the server starts, so the
  server answers `initialize` even where the engine is missing; the error appears on the
  first `debug_launch`/`debug_attach`/`debug_test`.
- HTTP is explicitly stateless (the MCP SDK 2.x default, now set in code); the `Bearer`
  scheme is accepted in any casing. Dependency: `ModelContextProtocol` 2.4.0.
- The container image and sidecar manifest: .NET 10 images; the manifest no longer
  contains a placeholder token Secret (create it with `kubectl create secret`), pins an
  explicit image version, sets `allowPrivilegeEscalation: false`, the `RuntimeDefault`
  seccomp profile and resource limits. A non-root / drop-all-capabilities profile is
  present only as a commented, untested block.
- The sample debuggees are built for net8.0, net9.0 and net10.0 and the runtime-sensitive
  integration tests run once per target runtime.
- `THIRD-PARTY-NOTICES.md` corrected: the `ModelContextProtocol*` packages are
  Apache-2.0 (not MIT), transitive packages are listed, and netcoredbg's copyright line
  carries its year.

### Fixed

- **Linux: the server could crash with SIGSEGV some time after `list_tasks`, `get_task` or
  `get_async_graph`.** ClrMD unloaded the runtime's data-access library (`libmscordaccore.so`)
  after each heap read, while threads that had called into it still carried a thread-exit
  destructor pointing inside it; the process died when one of those threads later retired.
  The library now stays loaded for the life of the process, one per target runtime version.
- A debuggee that received a recycled PID no longer causes unrelated processes to be
  attached, and killed on `debug_stop`.
- A rejected `continue`/`step_*` no longer leaves a waiter armed that swallowed the next
  stop.
- Task-inspection snapshots are dropped when the debuggee ends, so a recycled PID cannot
  be served a stale heap snapshot.
- Breakpoint paths are compared case-insensitively on Windows only.
- The runtime netcoredbg download is populated atomically, so an interrupted run or a
  concurrent server cannot leave a half-written engine that is trusted afterwards.

### Security

- HTTP listens on loopback by default (see Breaking).
- Failed HTTP authentications are written to the audit log with the remote address, and
  every audited call over HTTP carries `remote=<address>`; the credential presented is
  never logged.
- The bearer scheme match follows RFC 7235 (case-insensitive); the token comparison
  remains constant-time.
- `debug_stop` cannot kill a process the session only attached to unless asked to.
- Documented: `debug_launch` `env` lets an authenticated caller set variables such as
  `DOTNET_STARTUP_HOOKS` in the new process, and `list_processes` returns command lines,
  which can contain secrets. Neither adds a capability an authenticated caller did not
  already have (launching processes, evaluating code), but both are part of what the token
  grants. See [docs/security.md](docs/security.md).
- Known gap: if the process is killed while it downloads netcoredbg for the first time, a
  work directory (`<version>.<guid>.tmp`) can be left in the cache directory; nothing
  removes it. It is never trusted, but it occupies disk space.

## [0.2.1] - 2026-11-25

- Fixed `get_callstack` failing whole when a frame on the stack is managed but not IL
  (seen with WinForms on a .NET 10 desktop runtime) by bumping the bundled netcoredbg on
  Windows (GitHub issue #1); Linux keeps the netcoredbg version that runs on its glibc
  baseline.
- Packaging bundles every RID's engine with any SDK, and the produced package is verified
  to contain the engine for `win-x64`, `linux-x64` and `linux-arm64`.
- CI starts the bundled Linux engine on the oldest supported distributions.
- Build-time engine caches are version-aware (a stamp file next to the engine).

## [0.2.0] - 2026-06-05

- Offline installs: the single tool package bundles netcoredbg for `win-x64`,
  `linux-x64` and `linux-arm64`; the runtime download is only a fallback.
- Real errors from a rejected launch or attach (for example a process already owned by
  another debugger) instead of a hang or a generic message.
- Function breakpoints (`set_function_breakpoint`, `list_function_breakpoints`) and static
  method discovery (`list_methods`) for debugging without source.

## [0.1.0] - 2026-06-04

First release.

[Unreleased]: https://github.com/matejo-dev/ClrVoyant/compare/v0.2.1...HEAD
[0.2.1]: https://github.com/matejo-dev/ClrVoyant/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/matejo-dev/ClrVoyant/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/matejo-dev/ClrVoyant/releases/tag/v0.1.0
