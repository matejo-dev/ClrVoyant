# ADR-0013: Bounded, paged tool results

- **Status:** Accepted
- **Date:** 2026-11-25

## Context

A tool result goes into a model's context. Four tools could return arbitrarily many
items: `list_tasks` and `get_async_graph` (every Task on the heap of a busy service),
`get_variables` (the children of a large collection) and `list_methods` (every method
of a large assembly). An unbounded array is both a cost problem (tokens) and a
reliability problem (a result too large for the client to accept).

## Decision

These four tools return a **page** instead of a bare array:

```json
{ "items": [ ... ], "total": 412, "truncated": true, "nextOffset": 100 }
```

- Parameters `max` (1-1000; default 100, 200 for `list_methods`) and `offset` (0 or
  greater). Out-of-range values are an error result, not silently clamped.
- `truncated` is true exactly when `nextOffset` is present: the caller passes it back as
  `offset` for the next page. Null fields are not serialised, so an absent `total` or
  `nextOffset` means "unknown" and "none".
- `total` is exact where the source is walked completely anyway (`list_tasks` counts
  every match of its filter, `get_async_graph`, `list_methods`). For `get_variables`
  it is omitted: the debugger does not report a count, so the session asks the debugger
  for `max + 1` children starting at `offset`, and the surplus item is the lookahead that
  says whether more exist. The cost of one page is one bounded debugger request.
- Where the source is walked completely, only the items inside the window are built
  (`PageBuilder`), so a page of 100 out of 100,000 Tasks does not build 100,000 results.
- Filters (`status`, `typeFilter`, `methodFilter`) remain the first way to narrow a
  result; the playbook tells the model to use them before paging.

## Consequences

- Breaking change for clients that parsed these four tools' results as arrays.
- Result size is bounded by construction; a new tool that enumerates runtime objects
  should return a `Page<T>` too.
- Offsets are positions in an enumeration of a live heap at one stop; they are stable
  while the target stays at that stop. Paging across a resume is meaningless, as is any
  address obtained before it.
- Reading the heap for `list_tasks` is still O(heap) per call (the total must be
  exact); only the result is bounded. Narrow with `status` where that matters.

## Alternatives considered

- **A hard cap with no way to continue.** Simple, but loses data silently. Rejected.
- **Cursor tokens instead of offsets.** More robust to change between calls, but the
  data is frozen at one stop anyway and offsets are easier for a model to use. Rejected.
- **Streaming/notifications.** Not needed for results of this size; see
  [ADR-0004](0004-wait-based-execution-model.md) on the transport surface. Rejected.
