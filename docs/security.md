# Security model

ClrVoyant is a debugger exposed as a service. That makes it powerful and dangerous:
a caller who can drive it can run arbitrary code in the debugged process. This page
states the threat model and the controls.

## What an authorized caller can do

- **`evaluate`** executes expressions **in the debuggee** — property getters, method
  calls — and can mutate state or cause side effects. It is flagged dangerous in its
  tool description; prefer `get_variables` for plain inspection.
- **`debug_launch`** starts a new process; **`debug_attach`** attaches to an existing
  one; **`set_auto_attach`** attaches children.
- Reading memory/heap (`get_variables`, `list_tasks`) can expose **secrets** held in
  the process (connection strings, tokens, PII).

Net: **access to ClrVoyant ≈ code execution + memory disclosure on the target host /
POD.** Treat it accordingly.

## Trust boundaries by transport

### stdio (default)
The agent **spawns** the server as a local child over stdio. The boundary is the OS
process boundary: whoever can run the binary already has that user's privileges.
stdio is intentionally **unauthenticated** — adding auth there protects nothing the
OS doesn't already gate. ([ADR-0009](adr/0009-http-transport-mandatory-auth.md))

### HTTP
HTTP removes the implicit local boundary, so it is **authenticated and fails
closed**:

- `CLRVOYANT_AUTH_TOKEN` is **required**; the server refuses to start without it
  (no silent unauthenticated exposure).
- Every request needs `Authorization: Bearer <token>`; checked with a constant-time
  comparison; missing/invalid → `401`.
- The token is the **only application-level gate**. Everything else is operational.

## Controls (defense in depth)

1. **Network exposure** — keep HTTP **cluster-internal**: ClusterIP +
   `kubectl port-forward`, or mTLS at an ingress. **Never** a public Service/Ingress.
   Add a `NetworkPolicy` restricting who can reach port 3001.
2. **Token hygiene** — strong random secret (`openssl rand -hex 32`), stored in a
   Kubernetes `Secret` (not baked into the image). The token is read once at startup,
   so rotation means updating the Secret and restarting/redeploying the pod — there is
   no hot reload.
3. **Scope & lifetime** — treat the in-POD debugger as **break-glass / non-prod**.
   Don't leave it standing in production; bring it up for an investigation, tear it
   down after. (An ephemeral-debug-container variant is noted as future work in
   [ADR-0010](adr/0010-pod-remote-debug-topology.md).)
4. **Least privilege** — the sidecar needs `CAP_SYS_PTRACE` and
   `shareProcessNamespace`; grant nothing more. Don't run it privileged.
5. **Audit** — the server logs each `tools/call` (tool name and outcome:
   `ok` / `error` / `canceled`) under the `ClrVoyant.Audit` category. On stdio this
   goes to stderr (stdout is the JSON-RPC stream); on HTTP it goes to the console
   sink. Ship those logs so debug sessions are attributable.

## Capabilities required (and why they're sensitive)

| Capability | Needed for | Risk |
|---|---|---|
| `CAP_SYS_PTRACE` | netcoredbg attach; ClrMD `/proc/<pid>/mem` read | Lets the container inspect/attach other processes' memory. |
| `shareProcessNamespace` | sidecar sees the app process | Containers in the POD can see/signal each other's processes. |

These are exactly why hardened clusters drop them by default — adding them is a
deliberate, operator-granted decision for a debugging window.

## Non-goals

- ClrVoyant does **not** sandbox `evaluate` or restrict which expressions run — by
  design it is a full debugger. The control is *who can reach it*, not *what it can
  do once reached*.
- No per-tool authorization tiers (e.g. "read-only token"). Possible future work;
  today the token grants the full surface.

## Quick checklist before deploying HTTP

- [ ] `CLRVOYANT_AUTH_TOKEN` set from a Secret, strong, and rotated by redeploy
      (read once at startup).
- [ ] Endpoint not reachable from outside the cluster (no public Ingress).
- [ ] `NetworkPolicy` limits who can hit port 3001.
- [ ] Sidecar has only `SYS_PTRACE` (+ `shareProcessNamespace`), not privileged.
- [ ] This is non-prod or a time-boxed break-glass session.
- [ ] Logs are shipped for audit.
