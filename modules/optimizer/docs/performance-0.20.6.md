# NOS 0.20.6 preview — bounded re-entrant entity-search scratch

Nearest-entity queries now keep a bounded cache of extra cleared search contexts for nested calls. Previously an inner query occupied the one idle slot, so the outer context was discarded. Repeated outer queries allocated a new context/delegate again even after warming. A regression test reproduces that churn in 0.20.5 and passes with this change.

Ordinary flat queries keep the existing single-slot fast path and unchanged context object layout. Additional storage is created only after a nested call needs it. One primary plus at most three spare contexts are retained per thread; deeper recursion allocates temporary contexts normally and never truncates queries. All position, predicate and result references are cleared before retention. Configuration epochs invalidate idle scratch; exceptions propagate once without replay. Native partition traversal, strict distance comparisons, predicate order, results, AI cadence and world data remain unchanged.

Existing independent NearestEntitySearch / InteractableEntitySearch switches control the replacement. No defaults or Suite protocol changed. Suite remains 1.2.50.

## Controlled component results

Seven alternating before/after trials in the same process used the actual official search methods and partition walker, a pinned author-owned 0.20.5 implementation, 16 synthetic creatures and a dispatch-proxy API. Tiered compilation was disabled for this comparison to avoid comparing different JIT tiers. These fixtures are not a player or server tick workload.

| Case | 0.20.5 | 0.20.6 candidate |
|---|---:|---:|
| Flat nearest, median ns | 297.45 | 297.86 |
| Interactable, median ns | 295.48 | 296.66 |
| Nested nearest, median ns | 1283.60 | 1277.00 |
| Nested nearest, bytes per outer query | 1288 | 1152 |

The improvement is removal of 136 bytes of NOS-owned recurring allocation in this nested fixture. Shared native walker/proxy allocations remain. CPU differences are small and do not establish a speedup. An initial linked-context prototype used more CPU and was rejected; the final spare cache leaves the ordinary context layout unchanged.

Tests cover native results and predicate order, recursive reuse, cross-kind nesting, exceptions without replay, deep recursion beyond the retention cap, configuration changes during a query, concurrent thread-local ownership and cleared references. Mutating query positions remain observed as in the native implementation.

The compatibility monitor was also reviewed: the prior all-module fixture measured approximately 0.13 ms per once-a-second poll, insufficient to explain the reported live slowdown. Its conflict checks and cadence were retained.

This release does not establish a server-wide tick improvement or resolve the owner's live performance regression. Previous matched server tests showed essentially unchanged tick times with slightly higher CPU when NOS was installed, despite lower allocation. Keep NOS disabled on that live server pending the exact modpack/configuration investigation. The previous intensive NPC fixture recorded one search context and did not demonstrate re-entrant queries; its measurements are not relabelled as evidence for this change. No new 3,000-animal or authenticated-client comparison was performed for this narrowly scoped scratch fix.

[Controlled rows](research/entity-search-0.20.6/controlled.json) · [Native comparison and regression evidence](research/entity-search-0.20.6/native-comparison.json).
