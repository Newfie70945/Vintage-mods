# NOS 0.12.0-preview / Suite 1.2.48 NOS Chunks1

Three opt-in allocation modules reuse verified temporary scratch in official Vintage Story 1.22.7. All earlier NOS modules remain. Suite adds live save/apply controls and execution/failure counters; each target retains independent runtime conflict/failure diagnostics.

## Actual scope

- Block updates: reuse the three local lists used to batch ordinary, no-relight and decor changes. Neighbor callbacks, individual position object identity and packet ordering remain stock.
- Chunk-load requests: reuse the staging list and a private two-integer coordinate read immediately into original request arguments. Queue locking, capacity/back-pressure, request order, pass and pending requests remain stock.
- Chunk unloading: reuse numeric candidate lists, notification lists and private packet-building coordinates. Preserve original aging, generation-completion checks, forced loads, client tracking, packet order and independently owned outgoing arrays.

This is not a general chunk load/unload or disk I/O rewrite. No terrain algorithm, random-tick rate, world save transaction, chunk delivery limit or unload interval changes. Official 1.22.7 already reuses random-tick working BlockPos objects. Accepted queued copies can escape to callbacks and are deliberately not pooled. ChunkPos is a struct, so pooling that coordinate would not remove heap allocations.

## Bounded retention and fallback

Thread-local nested scopes isolate recursion and threads. Block/chunk references clear on both normal and exceptional completion. Each target/thread retains at most one idle scope. Ordinary lists/coordinate slots cap at 4,096; the unload-candidate long list caps at 65,536 slots (512 KiB), allowing repeated large candidate scans without unbounded retention. Larger buffers are discarded. Packet arrays, serialized data and actual chunk objects are never returned to a pool.

Exact engine hashes, method contracts and allocation-layout counts must match before patching. Foreign ownership on targets and relevant list consumers/coordinate decoder causes visible conflicts. Native exceptions propagate unchanged, with immediate reuse disable and subsequent removal of only that target's patch. Partial callbacks/queue operations/sends are never retried. This conservative failure response does not establish which mod caused the exception.

## Component evidence

[Measured fixtures](research/components-0.12.0/block-chunk.json) call official methods with intercepted sends/request creation, not real players. Repeated warmed runs with tiered compilation disabled reported:

| Fixture | Stock bytes/call | Enabled bytes/call |
| --- | ---: | ---: |
| 64 decor updates, original position objects | 1,144 | 0 |
| 64 staged chunk requests | 2,704 | 24 |
| Empty unload-candidate scan | 32 | 0 |
| 64 unloads, four fixture clients | 34,360 | 23,880 |

These include fixture queue/tracking work. The decor/request fixtures avoid actual transport and request construction. Their reduction does not imply zero-allocation chunk loading or block simulation. Candidate idle timing increases from about 32 ns to 48 ns, while allocating less. Other timings varied; no aggregate TPS, tick-time or managed-heap reduction is established.

Correctness checks cover original block callback and send order, retained position identity/dimensions, queue capacity and pending requests, coordinate arithmetic including negative/extreme numeric inputs, age/force-load/generation rules, per-client tracking/coordinates, outgoing array ownership, nested calls, cross-thread scopes, exceptions without replay, retention caps and unknown-IL rejection. Build/lifecycle/browser and copied-server validation accompany the download.

All new switches default off. Enable one at a time and compare matched real-player workloads, allocation rate, GC pauses and tick percentiles. Empty-server/component tests do not replace those comparisons.
