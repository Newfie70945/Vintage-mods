# Newfie's Server Optimizer 0.16.1 preview

A server-only mod for the **official Vintage Story 1.22.7 / .NET 10 server**. This version adds real, independently controlled Harmony replacements to the profiling foundation. Patches are installed in memory during mod startup and removed on disposal; game DLL files are left intact.

## Implemented API optimizations

| Module | Change | Correctness boundary |
| --- | --- | --- |
| Distant AI evaluation (opt-in) | Spaces out new task evaluations for eligible distant animals | Running tasks and full entity ticks continue; pets, mounts, combat and custom implementations exempt |
| Distant wander checks (opt-in) | Reduces idle wandering evaluations away from players | Independent interval and code filters; normal nearby behavior |
| Distant stationary repulsion (opt-in) | Reduces repulsion checks while stationary and far from observers | Moving, hurt, owned and otherwise exempt animals remain stock |
| Waypoint candidate pooling (opt-in) | Reuses up to 4,096 candidate nodes per official solver | Raw node-returning searches outside waypoint scope remain unpooled; exact official solver only |
| Missing-attribute resync suppression (opt-in) | Avoids full synchronization after removing a nonexistent key | Actual removals, null-key handling and subclasses remain stock |
| Empty broadcast exclusions (opt-in) | Normalize empty method-local exclusion arrays to null before the original raw/packet/UDP broadcast | All original send, serialization, compression, enumeration and nonempty filtering logic remains |
| Entity lifecycle lists (opt-in) | Bounded, isolated temporary cleanup and despawn lists | Every entity tick and send remains stock; packet arrays are not pooled; references cleared on success/failure |
| Despawn packet projection | Removes three LINQ projection iterators per despawn packet | Same IDs, reasons, damage sources, order and independently owned output arrays |
| Physics packet list reuse | Bounded per-thread temporary lists | Original packet arrays, filtering, batching and sends; references cleared on failure and success |
| Nearest-entity search reuse | Reuses per-thread search closure/delegate state | Same stock walker, distance/tie rules, callback order and exceptions; no inputs retained between calls |
| Interactable-entity search reuse | Reuses composed search/filter state | Same interactability and caller-filter order; independently switchable |
| Path worker limit fix | Checks the request cap before dequeuing | Prevents dropping one request at the cap; stock A* unchanged |
| Main-thread path assistance (opt-in) | Defers help until an observed head wait, then limits searches and elapsed work | Changes scheduling; a single search cannot be interrupted and may exceed the soft budget |
| Generation request reuse | Removes per-lookup closures from indexed generation requests | Original queue order, pass escalation and priority retained |
| Completed save snapshot release | Releases saved chunk references after successful background saves | Existing save lock, retry inputs and transactions retained |
| AI filter dispatch | Removes invocation-list arrays while preserving every veto callback and its order | No AI decision, priority, task or tick-frequency change |
| Repulsion hot path | Reuses the per-entity walk delegate and eliminates an always-true range predicate | Original collision, dimensions, mounting and mod callbacks retained |
| Online player snapshots | Builds the required fresh result array without the original LINQ iterator pipeline | Official client snapshot order, pending-player filtering and runtime array type preserved |
| Nearby player queries (opt-in) | At 128+ connected clients, reuses bounded per-thread temporary lists rather than repeatedly growing throwaway lists | Same range math, dimensions, callback order, short circuit and caller-owned results |
| Bulk stream reads | Uses a bulk byte copy for eligible reads of 256 bytes or more | Invalid ranges, aliased buffers, derived streams and unusual layouts run the original method |
| Server-assets compression cache | Reuses compressed output for unchanged server-assets bytes | Independent send buffers, source/compressor checks and bounded memory; stock compression handles other packets |

Temporary player-query lists retain no player references between calls and have a 4,096-client capacity ceiling. Larger calls use the stock implementation. The allocation modules do not change simulation ranges, AI tick cadence, physics rules, packet ordering, terrain algorithms or save scheduling. The separately enabled path-assistance module changes which thread handles waiting paths and when; it is off by default.

## Install and controls

1. Back up, then install the single optimizer ZIP on a staging server. Clients do not need it.
2. Start the server and run `/optimizer modules` to verify which modules actually activated.
3. Open Suite → Optimizations → Newfies Server Optimizer to save/apply switches immediately. Manual optimizer.json edits require a server restart.
4. Use `/optimizer profile clear` or **Release Stopped Capture** to release a stopped report without deleting exports or forcing GC.
5. Capture a representative workload with `/optimizer profile start`, `/optimizer profile stop` and `/optimizer export`.

```json
{
  "Enabled": true,
  "Compatibility": { "SafeMode": false },
  "EngineOptimizations": {
    "OnlinePlayerSnapshots": true,
    "NearbyPlayerQueries": false,
    "MinimumNearbyClients": 128,
    "BulkStreamReads": true,
    "AssetCompressionCache": true,
    "AssetCacheMaxMiB": 16
  }
}
```

`Enabled=false` disables the optimizer. `Compatibility.SafeMode=true` prevents the API replacement modules from installing. **Existing 0.1.0 configurations keep their previous SafeMode=true value**; explicitly set false on a staging server to try these modules. New 0.2.0 defaults use false with strict engine identity checks.

The module switches operate independently. **Nearby-query reuse defaults off**: it cut allocation in component tests but its CPU timings were mixed, including regressions. Enable only for a controlled workload comparison. Nearby-query reuse stays unpatched below its activation threshold, and unpatches again below threshold minus 32 clients to avoid churn. Detailed profiler timing switches remain separate and default off. Stopping a profile does not disable API optimizations.

## Compatibility and fallback

The patch registry verifies the actual running game version, exact inspected official API/server assembly hashes, method layouts and Harmony owners. Altered/unrecognised builds are blocked. Stratum, Tungsten and Synergy runtime/optimizer assemblies are blocked rather than stacked without validation.

A foreign patch on a target (or the stream Position accessors) prevents that module from installing. A once-per-second monitor detects late conflicts or recorded module failures and disables/removes only our prefix. Other modules stay active. Disposal leaves foreign patches untouched. If the monitor cannot start, installed replacements are rolled back.

An exception after observable work is propagated, never replayed through stock code; the monitor then stands that module down. A failing caller-supplied query predicate can trigger this conservative stand-down. No guarantee is made about arbitrary patch interactions during the interval before the monitor runs.

## Evidence and limits

See [0.3.1 lifecycle validation](docs/validation-0.3.1.md), [0.3.0 validation](docs/performance-0.3.0.md), [earlier performance evidence](docs/performance-0.2.0.md), [external capture analysis](docs/research/supplied-probe.md), [configuration](docs/configuration.md) and [limitations](docs/limitations.md).

Component tests exercise actual official methods before and after Harmony injection. Their uninitialised engine fixtures are **not authenticated players or a multiplayer server**. Component timing/allocation results are not server TPS gains or proof of sustainable player capacity. The uploaded Stratum/Lithos capture has no matched NOS/stock baseline.

## Build

Set `VINTAGE_STORY` to a separately installed official 1.22.7 server and use a .NET 10 SDK:

```sh
python3 tools/build.py --game-dir "$VINTAGE_STORY"
```

This compiles the mod, runs C# and tooling tests, verifies official dependency hashes and packages only the mod's own DLL, manifest and guides. Game binaries are not included. The optimizer has its own build flow, separate from the regular Suite module packager.

## Suite controls (0.3.0)

With Suite 1.2.47 installed, use its Optimizations page to configure the independent API modules and see actual runtime activation. Settings persist to optimizer.json and apply immediately; stop a running NOS capture first. The Suite bridge uses an in-process, versioned event rather than an extra network listener or a shared assembly dependency.

`EngineOptimizations.AssetCompressionCache` enables a single-entry cache for the official server-assets buffer only. Source contents are compared before reuse; each packet gets an independent output array. `AssetCacheMaxMiB` bounds the combined retained source snapshot and compressed result (default 16, range 1–32). Payloads outside the budget and non-asset packets use stock compression. Disabling the module releases its cached arrays.

See [the Stratum change review](docs/stratum-review.md). The existing allocation and bulk-read modules remain independently guarded. No entity/AI throttling, worldgen scheduling or save-format changes were added in this pass.

Low-compressibility or over-budget asset buffers use stock compression after the initial probe, avoiding repeated cache-copy overhead. A new asset buffer or reapplying settings permits a fresh probe. Cache hits require at least 25% size reduction on that probe.

Entity/AI hot paths run with profiling OFF. Suite 1.2.47 exposes independent `AiFilterDispatch` and `RepulseDelegateReuse` switches; both default on. See [performance validation](docs/performance-0.4.0.md).

## World generation and save snapshot modules

`WorldgenRequestReuse` removes per-lookup closure allocations from existing generation requests. `SaveSnapshotRelease` clears the stock chunk-reference snapshot after a successful background save, inside the stock save lock. Failed saves retain their snapshot for retry. Both default on, have independent Suite controls, and retain stock behavior on unsupported builds or patch conflicts. Generation order, terrain algorithms, save format and database transactions stay unchanged. See [validation](docs/performance-0.5.0.md).

## 0.6.0 task-start callback allocation reduction

`AiTaskStartDispatch` accelerates explicit `AiTaskManager.ExecuteTask` API requests (used by commands/mods), not the normal `StartNewTasks` selection loop. It removes the invocation-list array and LINQ enumerator from the official task-start veto loop. Every handler still runs in order, with the same task/previous-task arguments, veto aggregation, subscription snapshot and exception behavior. The stock transition code remains in place. Independent Suite control, exact-engine guards, changed-IL rejection, foreign-owner checks and stock fallback apply. It does no scheduling, distance throttling or task removal.

See [modpack validation and measurements](docs/performance-0.6.0.md). All six Suite modules and the selected third-party pack are checked for enabled state by the laboratory runner; this does not establish authenticated-player capacity or exercise every feature of every loaded mod.

See [0.7.0 validation and limits](docs/performance-0.7.0.md) for component measurements, real-terrain tests and the Opttage Path review.

See [0.8.0 validation](docs/performance-0.8.0.md) for physics allocation measurements and test limits.

See [0.9.0 validation](docs/performance-0.9.0.md) for despawn packet equivalence and allocation measurements.


Previous memory/performance pass: [0.9.1 validation](docs/performance-0.9.1.md) and [Synergy/Tungsten coverage audit](docs/replacement-coverage.md). These changes reduce optimizer/Suite overhead; NOS does not yet replace every feature of those mods.

## New distance and allocation policies (0.10.0)

All five new switches default **off**. Enable them individually in Suite → Optimizations → Newfies Server Optimizer, save/apply and inspect the actual activation reason and counters. Default include is `game:*`; exclude is `game:player, game:trader-*`. Empty include means no animals qualify. Patterns use a qualified domain and `*`, not regular expressions. Custom entity, behavior and AI-task implementations are automatically exempt even if their code matches.

Distance policies protect 64 blocks plus a 16-block margin by default. Only grounded, living, interactable, unowned, unnamed, unbred official animals with no active combat/special task qualify. Damage grants a ten-second grace. Nearby observers and missing observers in the dimension use stock scheduling. Proximity refresh is at most 100 ms apart, or immediately when the animal moves eight blocks or changes dimension. Playing-client position changes can take that refresh interval to be observed. These policies intentionally reduce distant decision frequency; they are not full entity sleep and do not skip hunger, breeding, health, physics or running AI tasks.

Candidate pooling is restricted to official `FindPathAsWaypoints` searches. Raw paths returned outside that scope keep independent nodes. Overflow candidates allocate normally; disabled, nested/busy and custom-solver scopes use stock allocation. Every reused node is reset, including dimension, action, parent and heap index. A pool adds bounded candidate slots per live solver; it does not bound the engine's own search sets or parent graphs.

See [coverage](docs/replacement-coverage.md) and [validation](docs/performance-0.10.0.md). No production TPS or memory reduction is claimed from component checks.

## Broadcast and lifecycle allocation pass (0.11.0)

`EngineOptimizations.BroadcastFilterReuse` and `EntityLifecycleListReuse` default false. Save/apply them through Suite → Optimizations. The broadcast switch reports raw, normal-packet and UDP activation separately. It only changes empty exclusion arguments to null inside the method. Stock treats both as “exclude nobody”, while the null form avoids constructing per-client predicate delegates. Nonempty/null input, original arrays, compression, socket preparation, packet ordering and transport behavior remain stock. This does not batch TCP sends or reduce network update frequency.

Lifecycle reuse replaces only method-local list construction in official TickEntities/SendEntityDespawns. Per-thread nested scopes use separate lists. All lists clear references on success and exceptions; capacities above 4,096 are discarded. Stock ToArray output buffers remain independent. Full entity ticks, despawn queues, save data and exceptions retain their original behavior.

[0.11.0 validation and diagnostic findings](docs/performance-0.11.0.md) separate component allocation results from production causes and player performance.

## Block/chunk allocation pass (0.12.0)

Three independent switches default off: `BlockUpdateListReuse`, `ChunkLoadRequestListReuse` and `ChunkUnloadListReuse`. Suite controls save/apply them live. They reuse block-update batching lists, generation-request staging lists/temporary coordinates, and unload candidate/notification scratch. All original random ticks, neighbor callbacks, generation queue limits, unload ages, forced loads, chunk sends and saves remain unchanged.

This does not pool positions queued for block ticks, alter growth simulation, speed up terrain generation or rewrite disk I/O. Official 1.22.7 already reuses its random-tick working positions; `ChunkPos` is a value type. Outgoing arrays and chunk/save objects retain native ownership. Each patched target has its own conflict/failure state. [Validation and limits](docs/performance-0.12.0.md).

## Callback and spawn allocation pass (0.13.0)

Three new opt-in switches default off: `DelayedCallbackSnapshotReuse`, `SpawnOpportunityListReuse` and `SpawnColumnArrayReuse`. They reuse single-delayed-block-callback snapshots, private mob-spawn search lists and chunk-read arrays. Callback timing, spawn selection/caps, RNG and block locks remain native. Already queued spawn results retain separate lists. All retained chunk/position/entity-property references clear on scope completion, including failures; list retention caps at 4,096 and column arrays at 256 entries.

The Suite groups callback/spawn and block/chunk controls separately and adds local module search/state filters. Compatibility checks now deduplicate shared patch inspection within each pass and release those snapshots afterward. [Validation and limits](docs/performance-0.13.0.md).

## Entity-query and recipe allocation pass (0.14.0)

Four new switches default off: `NearbyEntityListReuse`, `CuboidEntityListReuse`, `ShapelessStackMerge` and `ShapelessWildcardMatch`. Enable them individually in Suite → Optimizations → Entity queries and shapeless recipes. Query traversal stays native and each result keeps its own array; cleared scratch lists retain at most 4,096 slots per target/thread. Shapeless helpers eliminate temporary iterators/delegates while preserving stack attributes/clones and collectible crafting hooks. No inventories, recipes or results are cached.

Failures preserve the original exception without replaying a partially completed operation, disable the affected gate and appear in the module status/counters. Reapply settings to retry after resolving the cause. Foreign target patches and unsupported official binaries retain native behavior. [Validation and remaining Synergy/Tungsten gaps](docs/performance-0.14.0.md).

## Collision and container traversal (0.15.0)

Three new controls default off: `CollisionTraversalReuse`, `ContainerTransitionScan` and `ContainerTickTraversal`. They replace private iterator objects with method-local structs in verified native loops; no geometry, inventory or result pool is retained. Collision resolution/flags/hooks, room checks, food transitions and sync callbacks still run. Custom inventory GetEnumerator overrides retain their native iterator and disposal. [Measured evidence and limits](docs/performance-0.15.0.md).

## Collision generation and animation (0.16.0)

Three independent controls default off: `CollisionGenerationReuse`, `CachedCollisionGenerationReuse` and `AnimationCollisionFlagScan`. Collision callbacks reuse cleared thread-local context only with the verified synchronous official walker; custom walkers retain native allocation and ownership. Animation scans use a struct dictionary enumerator without skipping frames, poses or triggers. Collision fixtures allocate less but show mixed CPU timings, including slower warm cache hits. See [evidence and limits](docs/performance-0.16.0.md).

0.16.1 improves the existing collision bounds-cache return and adds searchable settings, pending-edit review and execution cards in Suite Optimization UI2. Collision defaults remain off. [Evidence and limits](docs/performance-0.16.1.md).
