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

**Observed compatibility:** Attribute Rendering Library 3.2.0 patches `canHeatInput` in the heavy test pack. This deliberately blocks `IdleFirepitFastPath`; its configured switch can be on while actual state remains conflict. Do not bypass this guard or remove a dependency merely to activate the shortcut. Barrel pairing, bush flattening and unload staging are evaluated independently.

The first three modules check exact inspected official method IL. Berry flattening also verifies its base callback remains a no-op. Public engine data and save formats are unchanged. No server DLL is written to disk.

## Suite improvements

Suite version is 1.2.49 (`ms11-block-ticks1`). The Optimizations page exposes each control and counters. Exact native firepit, barrel and wild-bush root markers now resolve to Survival Mode instead of being lost in ambiguous core namespace matches. Custom or child markers continue through normal attribution. A native callback can still contain mod hooks or GC pauses: ownership does not prove a particular mod caused a stall.

## Measurement limits

Allocation comparisons use actual official methods with isolated fixtures. Barrel pairing removes temporary allocations; berry setup removes a redundant wrapper. Chunk staging reuse reduces allocation pressure but adds scope/cleanup overhead, including on empty unload passes. It should be evaluated separately under real chunk churn, not enabled as a general CPU or disk-speed fix. Component nanosecond measurements are sensitive to tiering, JIT warmup and host contention.

These changes do not explain or fix every recorded stall. Entity animation, entity behaviors and network processing remain substantial in these captures. Catch-up growth and entity unload callbacks can legitimately consume time. No growth throttling, deferred saving, forced GC or more aggressive terrain unloading is introduced.

## Validation results

- Official 1.22.7 / .NET 10: 28 optimizer test suites pass, including 89 block callback checks, 13 unload staging checks and 115 module lifecycle/conflict checks.
- Suite 1.2.49 builds with zero warnings/errors. 22 root checks and 8 exact native-marker identity checks pass. Browser checks cover new switches, independent saved settings, older-NOS omission, locks and mobile layout.
- Copied official server with 25 mod ZIPs: 101 checks pass. Barrel pairing, berry flattening and unload staging activate independently. ARL's firepit overlap stays blocked; no guard is bypassed. Actual registry items/terrain and A* are exercised, with no authenticated-player workload.
- Three additional sequential component trials with tiered compilation disabled: matching two-slot barrel pairing allocates 384 -> 88 bytes/call; bush setup with behavior saves 96 bytes/batch (568 -> 472 in the proxy fixture), and empty bush setup 32 -> 0. Native empty unload passes allocate 88 -> 24 bytes; private staging helper components with 256 records/list allocate 28,848 -> 0 after warmup. Remaining allocations, including owned recipe results and native downstream work, are retained.
- Empty/cold firepit CPU comparisons are mixed; no reliable firepit CPU improvement is claimed. Unload staging also has CPU tradeoffs. Do not translate these component allocations/timings into a production TPS or disk-I/O claim.

[Sanitized validation and assembly hashes](research/block-ticks-0.18.0/validation.json), [block component trials](research/block-ticks-0.18.0/block-components.json), [unload component trials](research/block-ticks-0.18.0/unload-components.json), and [copied-server checks](research/block-ticks-0.18.0/copied-server.json). The user's raw profiler exports are excluded.
