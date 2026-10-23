# NOS 0.13.0-preview / Suite 1.2.48 NOS Events1

This pass adds three opt-in runtime allocation controls and improves the Suite's optimizer page. All earlier NOS modules remain available. New controls default off; enable one at a time for matched workload comparisons.

## Native behavior and ownership

- Delayed callbacks: normal/debug event-manager methods reuse their single-delayed-block-callback key snapshot. Due times, callback order, position identity/dimensions, new-callback scheduling and even native errors when handlers remove later snapshot keys remain unchanged. Both core and mod event-manager instances retain their own native execution.
- Spawn opportunities: reuse only the inner spawner's private temporary list. The stock copy constructor and queued EntitySpawnerResult list stay unchanged. Spawn positions, RNG advancement, group sizes, caps and event vetoes remain native.
- Spawn chunk reads: reuse an exact-length temporary IWorldChunk array. The original acquisition, unpacking, incomplete-column skip and finally-release operations remain in place. All chunk references clear when the call completes, including exceptions.

Scopes isolate recursion and threads. At most one idle scope per target/thread is retained; lists cap at 4,096 slots and column arrays at 256 entries. Larger buffers use normal allocations or are discarded. Configuration changes invalidate old scopes. Native callback/spawn exceptions are preserved, never replayed, and disable only the affected target's reuse until reapplied. A failure count does not establish that NOS caused the original exception.

Exact official 1.22.7 hashes and expected method/allocation shapes are mandatory. Foreign ownership on relevant targets, snapshot/copy constructors and array-consuming spawn validators blocks the affected reuse. A custom spawn-validator patch can block column-array reuse without blocking the independent opportunity-list module. Stock bulk attribute packet helpers already use direct array copies; this pass does not pool their outgoing buffers.

## Suite and optimizer overhead

The Suite separates callback/spawn, block/chunk/save and other controls. Module name/reason search and state filters operate locally, preserve draft settings and make no configuration calls. Existing role, Safe Mode, capture, older-NOS and mobile safeguards remain.

The optimizer deduplicates shared Harmony metadata lookups inside each compatibility-monitor pass. A finally block releases every snapshot before returning; NOS patch changes invalidate the affected cached target. Existing late-conflict, exact own-patch and per-target failure checks remain. This is not a persistent cross-pass compatibility cache.

## Component evidence and limits

[Raw method fixtures](research/components-0.13.0/callback-spawn.json) use the real official methods with seeded synthetic worlds, intercepted spawn-position/chunk access and proxy chunk locks. Warm trials with tiered compilation disabled measured:

| Fixture | Stock bytes/call | Enabled bytes/call |
| --- | ---: | ---: |
| 1,342 future single-block callbacks | 10,880 | 88 |
| Spawn search with no eligible states | 32 | 0 |
| Eight-chunk spawn column | about 888 | 800 |

The callback case reduces snapshot allocation by 10,792 bytes per call. The empty spawn-search case adds some CPU overhead despite allocating less. Column timings varied. These figures include fixture/proxy work; they are not production TPS, player-capacity or whole-server heap claims.

[Monitor measurements](research/components-0.13.0/monitor.json) with the same enabled module set reduce allocation from about 91.5 KB to 73.5 KB per one-second compatibility check; timing differences do not establish an aggregate CPU improvement.

Checks cover callback timing/order/mutation errors, original positions, seeded spawn results/RNG/caps, queued-list independence, complete/incomplete column locks, cleanup after inner/logging failures, exact column sizes, caps, null arguments, nested/threaded isolation, rejected IL and target-specific conflicts/failures. Build and copied-server evidence accompany the download. No authenticated-player workload is represented by these fixtures; real matched-player soaks remain necessary.

## Build and copied-server validation

All 22 optimizer build/test suites pass, including 51 callback/spawn checks, 71 lifecycle/conflict checks and all earlier module checks. Suite and NOS compile with zero warnings/errors. Browser checks verify the new switches and local module filters alongside drafts, permissions, capture locks, older-NOS compatibility and mobile width; 22 root checks pass.

[Forty-nine copied-server checks pass](research/components-0.13.0/copied-server.json) with official 1.22.7 and the 25-ZIP Suite/selected content-mod pack. They verify independent activation/disable, saved controls, actual normal/debug callback scope execution, earlier terrain/path/animal/timeline behavior and diagnostic ZIP output. No callback/spawn module failures were recorded. There are no authenticated players; this does not exercise every loaded mod or establish live spawning throughput. Final tested assembly hashes are recorded with the result.
