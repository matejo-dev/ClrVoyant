# ADR-0009: HTTP transport with mandatory bearer auth (fail-closed)

- **Status:** Accepted
- **Date:** 2026-06-04

## Context

stdio transport ([0001](0001-standalone-headless-mcp-server.md)) ties the server to
a locally-spawned client. To debug a process running elsewhere (notably inside a
Kubernetes POD), the **agent** must reach the server over the network. The MCP C#
SDK offers a Streamable-HTTP transport via `ModelContextProtocol.AspNetCore`.

A debug server is not a neutral service: it can `debug_launch` processes and
`evaluate` expressions (which **executes code** in the debuggee). An unauthenticated
HTTP debug server is remote-code-execution as a service.

## Decision

Support **HTTP transport alongside stdio**, selected by `CLRVOYANT_TRANSPORT=http`.
The HTTP path:

- Runs as an ASP.NET Core `WebApplication` with `WithHttpTransport()` + `MapMcp()`,
  binding `CLRVOYANT_HTTP_URL` (default `http://0.0.0.0:3001`).
- **Requires `CLRVOYANT_AUTH_TOKEN`** and **fails closed**: the server refuses to
  start without it, throwing a clear error rather than serving unauthenticated.
- Gates every request with bearer-token middleware using a constant-time comparison
  (`CryptographicOperations.FixedTimeEquals`); missing/invalid token → `401`.

stdio remains the default and is unauthenticated by design (local, parent-spawned).

## Consequences

- Remote agents can use the server; the security burden moves from "implicit local
  boundary" to "explicit token + network policy".
- Fail-closed means a misconfiguration can't silently expose an RCE surface.
- The token is the *only* application-level gate; network exposure must be
  constrained operationally (see [0010](0010-pod-remote-debug-topology.md) and
  `docs/security.md`).
- HTTP needs the ASP.NET Core shared framework (FrameworkReference), hence the
  aspnet runtime image.

## Alternatives considered

- **No auth / optional auth on HTTP** — unacceptable for an RCE-capable endpoint.
  Rejected.
- **mTLS instead of bearer** — stronger, but heavier to provision; bearer + network
  policy is the pragmatic floor. mTLS is recommended *in addition* at the ingress,
  documented in `docs/security.md`, not enforced in-process.
- **OAuth / full identity** — overkill for a break-glass tool. Deferred.

## Update (2026-11-25)

- **Loopback by default.** The default of `CLRVOYANT_HTTP_URL` is now
  `http://127.0.0.1:3001`, not `0.0.0.0`. Reaching the server from the network is a
  decision the operator makes; the container image makes it explicitly (it sets
  `0.0.0.0` for its pod), and the server logs a warning at startup for every
  non-loopback address it listens on.
- **Stateless HTTP, set explicitly.** The MCP SDK 2.x flipped its default; the mode is
  now set in code. Debug sessions live in the process-wide `SessionManager`, addressed
  by the tools' own `sessionId`, and no tool pushes to the client, so MCP session state
  would buy nothing and cost a forced re-initialize after an idle timeout or restart.
  Revisit only if a feature needs unsolicited server push (for example "stopped"
  events).
- **Bearer check.** It is a middleware of its own; the scheme is matched
  case-insensitively (RFC 7235); the token comparison remains constant-time.
- **Authentication is audited.** A request refused with `401` is logged as
  `auth -> denied remote=<address>`, and over HTTP every `tools/call` audit line carries
  `remote=<address>`. What the caller presented is never logged.
