# NOS 0.18.0-preview and Suite 1.2.49 — block ticks and unload staging

The two supplied manual 30-second profiles identify firepit and barrel callbacks as recurring costs. Their callback markers represent 5.33% / 10.25% and 3.39% / 5.45% of captured tick time respectively. Wild fruiting bushes vary from 4.68% to 0.52%. The first run records 213 ms in chunk unloading, including a 180 ms peak; unloading is outside the second export's top-30 phases. The runs report NOS 0.14.0 and are different player workloads, not a controlled before/after comparison.

## Independent default-off modules

| Setting | Scope | Behavior boundary |
| --- | --- | --- |
| `IdleFirepitFastPath` | Recognize an exact official, empty, cold firepit at its native 20-degree resting temperature | Retains the native cooking-counter reset. Burning, cooling, stocked, constructed and custom firepits run the original callback. No callback interval changes. |
| `BarrelIngredientPairing` | Avoid temporary ingredient-list clones and predicate closures in native one/two-ingredient pairing | Fresh, independently owned result lists. Native ingredient checks and collectible hooks retain order and vetoes. Larger/custom recipes remain native. Recipe results, quantities and fermentation time are not cached. |
| `WildBushCallbackFlatten` | Remove the wrapper around an otherwise empty base callback | The behavior's own per-batch closure is recreated normally. Native random calls, catch-up iterations, climate queries, growth, dormancy and harvest timing remain. Custom block-entity types use stock. |
| `ChunkUnloadStagingReuse` | Reuse the private temporary chunk/map-chunk lists inside native unloading | Dirty records are copied normally into native save queues; buffers clear on success/failure. Eligibility, ages, force-loaded checks, callbacks, disposal, locking and disk writes remain native. Buffers above 4,096 capacity are dropped. |

Use Suite Optimizations -> Newfies Server Optimizer -> block/world settings, or `EngineOptimizations` in optimizer.json. Configured state and actual activation may differ. Existing/late foreign Harmony dependency patches, unsupported method contracts and runtime failures disable the affected module. Exceptions are preserved without retrying partially completed work. These preview controls are not a guarantee against all mod interactions.

The first three modules check exact inspected official method IL. Berry flattening also verifies its base callback remains a no-op. Public engine data and save formats are unchanged. No server DLL is written to disk.

## Suite improvements

Suite version is 1.2.49 (`ms11-block-ticks1`). The Optimizations page exposes each control and counters. Exact native firepit, barrel and wild-bush root markers now resolve to Survival Mode instead of being lost in ambiguous core namespace matches. Custom or child markers continue through normal attribution. A native callback can still contain mod hooks or GC pauses: ownership does not prove a particular mod caused a stall.

## Measurement limits

Allocation comparisons use actual official methods with isolated fixtures. Barrel pairing removes temporary allocations; berry setup removes a redundant wrapper. Chunk staging reuse reduces allocation pressure but adds scope/cleanup overhead, including on empty unload passes. It should be evaluated separately under real chunk churn, not enabled as a general CPU or disk-speed fix. Component nanosecond measurements are sensitive to tiering, JIT warmup and host contention.

These changes do not explain or fix every recorded stall. Entity animation, entity behaviors and network processing remain substantial in these captures. Catch-up growth and entity unload callbacks can legitimately consume time. No growth throttling, deferred saving, forced GC or more aggressive terrain unloading is introduced.
