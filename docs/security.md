# Security model

ClrVoyant is a debugger exposed as a service. That makes it powerful and dangerous:
a caller who can drive it can run arbitrary code in the debugged process, and start
processes of its choosing. This page states the threat model and the controls. To
report a vulnerability, see [SECURITY.md](../SECURITY.md).

## What an authorized caller can do

- **`evaluate`** executes expressions **in the debuggee**: property getters, method
  calls. It can mutate state or cause side effects. It is flagged destructive in its
  tool hints and dangerous in its description; prefer `get_variables` for plain
  inspection.
- **`debug_launch`** starts a new process (any .NET `.dll` the server's user can run),
  with arguments, a working directory and **environment variables of the caller's
  choosing** (`env`). Environment variables are an injection channel: for example
  `DOTNET_STARTUP_HOOKS` makes the runtime load an assembly into the new process, and
  on Linux `LD_PRELOAD` loads a native library. This adds no capability a caller who can
  already launch a process and `evaluate` code does not have, but it means that
  restricting *which program* may be launched is not a control: the caller shapes the
  process environment too. **`debug_test`** runs `dotnet test` on a project of the
  caller's choosing, which executes that project's code and build logic.
- **`debug_attach`** attaches to an existing process; **`set_auto_attach`** attaches
  .NET children of debugged processes.
- **`list_processes`** returns the **command line** of every process it lists (cut at
  1000 characters). Command lines routinely carry secrets (passwords, tokens, connection
  strings) passed as arguments; the server returns them as the OS reports them.
- Reading memory and the heap (`get_variables`, `evaluate`, `list_tasks`) can expose
  **secrets** held in the process (connection strings, tokens, personal data).
  `get_output` returns what the debuggee printed, which may include them too.
- **`debug_stop`** kills processes this server started, and with `terminate=true` any
  process the session was attached to.

Net: **access to ClrVoyant is equivalent to code execution and memory disclosure on the
target host or pod, as the user the server runs as.** Treat it accordingly.

## Trust boundaries by transport

### stdio (default)
The agent **spawns** the server as a local child over stdio. The boundary is the OS
process boundary: whoever can run the binary already has that user's privileges. stdio
is intentionally **unauthenticated**: adding auth there would protect nothing the OS
does not already gate ([ADR-0009](adr/0009-http-transport-mandatory-auth.md)).

### HTTP
HTTP removes the implicit local boundary, so it is **authenticated and fails closed**:

- `CLRVOYANT_AUTH_TOKEN` is **required**; the server refuses to start without it (no
  silent unauthenticated exposure).
- **It listens on loopback by default** (`http://127.0.0.1:3001`). Binding anything
  else is the operator's decision, through `CLRVOYANT_HTTP_URL`; the server logs a
  warning for every non-loopback address it listens on. The container image sets
  `http://0.0.0.0:3001` explicitly because a pod sidecar is reached through the pod
  network.
- Every request needs `Authorization: Bearer <token>`. The scheme is matched
  case-insensitively; the token is compared in constant time; a missing or invalid one
  is answered `401` with a `WWW-Authenticate: Bearer` challenge before MCP is reached.
- The server speaks plain HTTP. Keep the path to it inside a trusted network
  (`kubectl port-forward`, a private network), or terminate TLS in front of it: the
  token otherwise travels unencrypted.
- The MCP session mode is **stateless**: the server keeps no MCP session between
  requests (debug sessions are separate, held by the server and addressed by
  `sessionId`). The token is therefore the only thing that ties a caller to the server;
  there is no per-caller identity and no per-caller isolation: **every holder of the
  token sees and controls every debug session.**
- The token is the **only application-level gate**. Everything else is operational.

## Controls (defense in depth)

1. **Network exposure.** Keep HTTP **cluster-internal**: ClusterIP plus
   `kubectl port-forward`, or mTLS at an ingress. **Never** a public Service or Ingress.
   Add a `NetworkPolicy` restricting who can reach port 3001.
2. **Token hygiene.** A strong random secret (`openssl rand -hex 32`), stored in a
   Kubernetes `Secret` that you create (the manifest ships none, so applying it cannot
   deploy a known token), never baked into the image. The token is read once at startup:
   rotation means updating the Secret and restarting the pod.
3. **Scope and lifetime.** Treat the in-pod debugger as **break-glass / non-prod**. Do
   not leave it standing in production; bring it up for an investigation, tear it down
   afterwards. (An ephemeral-debug-container variant is noted as future work in
   [ADR-0010](adr/0010-pod-remote-debug-topology.md).)
4. **Least privilege.** The sidecar needs `CAP_SYS_PTRACE` and `shareProcessNamespace`;
   grant nothing more, and do not run it privileged. The shipped manifest also sets
   `allowPrivilegeEscalation: false`, the `RuntimeDefault` seccomp profile and resource
   limits. Running as non-root with all capabilities dropped is in the manifest only as a
   commented block that has **not been tested** against a real attach (see
   [deploy/README.md](../deploy/README.md)).
5. **Audit.** Each `tools/call` is logged under the `ClrVoyant.Audit` category as
   `tools/call <tool> -> ok | error | canceled`; a call that failed with an exception also
   carries its error message. Over HTTP every line ends with `remote=<address>`, and a
   request refused by the bearer check is logged as `auth -> denied remote=<address>`.
   What the caller presented is never logged, and neither are tool arguments. Note that an
   error message can contain paths or the tail of a build log (`debug_test`). On stdio the
   log goes to stderr (stdout is the JSON-RPC stream); on HTTP to the console sink. Ship
   those logs so debug sessions are attributable. Behind `kubectl port-forward` the
   remote address is the forwarder's, not the end user's.

## Supply chain

- **netcoredbg** is downloaded from its upstream GitHub release at build or publish time
  (or, as a last-resort fallback, on first run), pinned by version and **SHA-256
  verified**; a mismatch fails closed and nothing is extracted. The runtime fallback
  trusts its cache only when it is complete (engine plus version stamp).
- **Release artefacts.** A release is built by the `release` workflow from a version tag
  that must equal the version in the server project. The workflow publishes to NuGet.org
  through Trusted Publishing (a short-lived credential obtained over OIDC, no stored
  secret), and attaches to the GitHub Release the `.nupkg` and `.snupkg` files together
  with a `SHA256SUMS` file and a **build-provenance attestation** for each of them. To
  verify a release you downloaded from its GitHub Releases page, in the download
  directory:

  ```sh
  sha256sum -c SHA256SUMS
  gh attestation verify ClrVoyant.<version>.nupkg --repo matejo-dev/ClrVoyant
  # stricter: also pin the workflow that signed it
  gh attestation verify ClrVoyant.<version>.nupkg --repo matejo-dev/ClrVoyant \
    --signer-workflow matejo-dev/ClrVoyant/.github/workflows/release.yml
  ```

  The checksum proves the files match what the build produced; the attestation proves
  that this repository's workflow produced them. Both apply to the **files on the GitHub
  Release**. A package downloaded from nuget.org carries nuget.org's repository
  signature (a `.signature.p7s` entry inside the `.nupkg`), so it is not byte-identical to
  the file the workflow produced and will not match `SHA256SUMS`. Verify the release
  asset, and install that file with
  `dotnet tool install -g ClrVoyant --add-source <directory> --version <version>` if you
  need to be sure what you run. Only releases made by the current release workflow carry
  an attestation; older ones do not.
- Dependencies are kept current by Dependabot; CodeQL scans the C# code; NuGet audit
  reports vulnerable packages including transitive ones.

## Capabilities required (and why they are sensitive)

| Capability | Needed for | Risk |
|---|---|---|
| `CAP_SYS_PTRACE` | netcoredbg attach; ClrMD `/proc/<pid>/mem` read | Lets the container inspect and attach to other processes' memory. |
| `shareProcessNamespace` | the sidecar sees the app process | Containers in the pod can see and signal each other's processes. |

These are exactly why hardened clusters drop them by default: adding them is a
deliberate, operator-granted decision for a debugging window.

## Non-goals

- ClrVoyant does **not** sandbox `evaluate`, restrict which expressions run, which
  programs are launched or which environment they get. By design it is a full debugger:
  the control is *who can reach it*, not *what it can do once reached*.
- No per-tool authorization tiers (for example a read-only token) and no per-caller
  identity. Possible future work; today the token grants the full surface.

## Checklist before deploying HTTP

- [ ] `CLRVOYANT_AUTH_TOKEN` comes from a Secret you created, is strong, and is rotated
      by redeploying (read once at startup).
- [ ] The endpoint is not reachable from outside the cluster (no public Ingress), and
      is not exposed beyond loopback unless you chose that on purpose.
- [ ] A `NetworkPolicy` limits who can reach port 3001.
- [ ] The sidecar has only `SYS_PTRACE` (plus `shareProcessNamespace`), is not
      privileged, and the image is pinned to a version, not `:latest`.
- [ ] You know that every token holder can run code in the app and read its secrets.
- [ ] This is non-prod or a time-boxed break-glass session.
- [ ] Logs are shipped for audit.
