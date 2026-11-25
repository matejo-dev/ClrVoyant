# ADR-0012: Tool errors are returned as results, from one dispatch-boundary filter

- **Status:** Accepted
- **Date:** 2026-11-25

## Context

The tools tell the calling model how to recover through their exception messages:
`Session 's1' is 'Running', not stopped...`, `No active debug session; 2 sessions
exist (s1, s2): pass sessionId.`, a build log tail from `debug_test`. The MCP C# SDK
(checked on 1.3.0 and on 2.4.0) replaces the message of every exception except
`McpException` with a generic `An error occurred invoking '<tool>'`, so that guidance
never reached the model. Rewriting every throw site to `McpException` would couple the
core library to the protocol layer and would not cover exceptions raised by the
libraries underneath.

## Decision

One call-tool filter at the dispatch boundary (`ToolErrors`) turns a failed call into
an error result (`isError: true`, the text as the only content):

- For the exception kinds this code base throws on purpose to explain a condition
  (`InvalidOperationException`, `ArgumentException`, `KeyNotFoundException`,
  `TimeoutException`, `IOException`, `DapException`, `McpException`) the result carries
  the message unchanged.
- Any other exception is treated as a probable bug: the message is prefixed with the
  exception type (`Unexpected NullReferenceException: ...`), and the full exception with
  its stack trace is written to the server log (category `ClrVoyant.ToolErrors`), never
  sent to the client.
- Client cancellation and `McpProtocolException` (JSON-RPC errors such as invalid
  parameters) are not converted: cancellation stays cancellation, protocol errors stay
  protocol errors.

**Order.** The filter is registered before the audit filter, so it is the outer one:
the audit filter sees the original exception (its message, and cancellation), records
the outcome as `ok` / `error` / `canceled`, and only then is the exception turned into
the result.

All filters are registered in one place (`McpRegistration`), which stdio, HTTP and the
tests share, so a test exercises what runs in production.

## Consequences

- Every present and future tool gets actionable errors without any per-tool code; the
  rule for tool authors is "throw an exception type from the list with a message
  written for the caller".
- Messages of the listed exception types reach the client verbatim. They must not
  contain secrets; the audit log writes the same message.
- A new exception kind that should be shown verbatim must be added to the list; until
  then it appears with the `Unexpected <Type>:` prefix, which is the safe default.
- Clients see tool failures as results the model can read, rather than protocol errors.

## Alternatives considered

- **Throw `McpException` from every tool and from the core library.** Couples the core
  to the SDK and misses exceptions thrown by the layers below. Rejected.
- **Catch inside each tool.** Repetitive, and a forgotten tool silently loses its
  messages. Rejected.
- **Send exception messages unfiltered, whatever their type.** Messages of arbitrary
  framework exceptions are not written for the caller and can leak paths or internals;
  the allow-list keeps the verbatim promise to the exceptions we write ourselves.
