# Security policy

## What this software is

ClrVoyant is a debugger exposed to an AI agent. **By design** it launches processes,
attaches to running ones, reads their memory and **evaluates code inside the
debuggee**. Anyone who can drive it has code execution and memory disclosure on the
target. That capability is the product, not a vulnerability. The full threat model,
the controls and a deployment checklist are in [docs/security.md](docs/security.md);
read it before running the HTTP transport or the Kubernetes sidecar.

## Supported versions

Only the **latest release** (as published on NuGet and in GitHub Releases) receives
security fixes. The project is pre-1.0; please upgrade before reporting.

## Reporting a vulnerability

Please report privately through GitHub's private vulnerability reporting: open the
repository's **Security** tab and choose **Report a vulnerability**
(<https://github.com/matejo-dev/ClrVoyant/security/advisories/new>). Do not open a
public issue or pull request for a suspected vulnerability.

Include the version, the transport (stdio or HTTP), the platform, and steps to
reproduce. This is a small open-source project maintained on a best-effort basis:
there is no response-time guarantee, but reports are read and taken seriously.

## What counts as a vulnerability

- Bypassing or weakening the HTTP bearer-token check: any request that reaches an MCP
  tool without a valid `Authorization: Bearer` token, or the server starting the HTTP
  transport without `CLRVOYANT_AUTH_TOKEN` (it must fail closed).
- Bypassing the SHA-256 verification of the downloaded debug engine (netcoredbg), or
  an unsafe extraction of its archive (e.g. path traversal).
- The token or other secrets leaking into logs, audit output or error responses.
- A way for a party who is **not** an authorized caller to cause code execution or
  file access on the host through ClrVoyant.

## What does not count

- An authorized caller using the documented tools (`evaluate`, `debug_launch`,
  `debug_attach`, `set_auto_attach`, memory/heap reads) to run code or read data in
  the debuggee or on its host. That is the intended capability; the token is the gate.
- The stdio transport being unauthenticated: the boundary there is the OS process
  boundary (see [docs/security.md](docs/security.md)).
- Exposing the HTTP endpoint publicly, or using a weak or reused token, contrary to
  the deployment guidance.
- Resource exhaustion by an authorized caller.
- Vulnerabilities in third-party components (netcoredbg, ClrMD, the MCP SDK, the .NET
  runtime) that ClrVoyant does not make worse. Please report those upstream; do tell
  us if ClrVoyant's use of them makes them exploitable in a way they otherwise would
  not be.
