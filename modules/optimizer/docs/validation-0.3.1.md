# NOS 0.3.1 lifecycle validation

This pass addresses reload and shutdown correctness rather than adding speculative gameplay patches. Suite 1.2.46 remains compatible through control protocol 1; no Suite update is required for owners already using it.

## Fixes

- Compression operations carry an activation generation. Work started before disable/re-enable, buffer replacement or a cache-budget change cannot publish into the new cache.
- Changing the budget invalidates an entry immediately even if its source buffer is unchanged.
- Each engine module records its failure-counter baseline on activation. Historical failures stay visible in cumulative diagnostics while new activations can retry. A new failure still stands the module down.
- Failure to unregister the monitor cannot prevent owned patch removal. Disposed callbacks are inert. Monitor startup failure also marks standby modules failed instead of leaving them waiting forever.
- Resolved method/dependency metadata is retained per module instead of repeatedly allocating dependency arrays and repeating reflection during monitoring. Cache diagnostics obtain one consistent counter snapshot per request.

## Tests

- 1,030 asset-cache checks, including 500 disable/re-enable cycles, payload round trips and retained-memory bounds.
- Weak-reference/GC verification that disabled cache source and result arrays can actually be collected.
- Delayed completion from a previous activation and same-buffer budget shrink.
- 27 actual Harmony module lifecycle checks, including old/new failure counters, monitor failure, removed/foreign patches, failure during unregister and disposed callbacks.
- Existing player-query, stream-copy, engine metadata, profiler and Python tooling suites.
- Suite optimizer-control browser checks remain passing.

An isolated official 1.22.7 server runs repeated Suite/NOS configuration cycles followed by a 60-second engine capture, report, world save and clean shutdown. It has no authenticated players and is a lifecycle test, not a production capacity benchmark. Component timing evidence from 0.3.0 is not promoted into a new whole-server performance claim.
