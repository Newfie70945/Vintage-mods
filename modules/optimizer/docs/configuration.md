# Configuration and profiler coverage

Newfie's Server Optimizer **0.9.0 engine-patch preview** reads `ModConfig/optimizer.json` and writes defaults on first launch. A ready-to-copy example is in `config/optimizer.json`. Restart after file edits, or apply supported switches live through Suite → Optimizations. Each engine module is independently controlled; exact compatibility and conflict guards retain stock behavior when needed.

## Capture controls

All commands require `controlserver`; console commands use `/optimizer ...`.

| Command | Result |
| --- | --- |
| `/optimizer status` | State, errors, conflicts, evidence problems and latest export |
| `/optimizer profile start` | Begin a new bounded capture; refuses to discard a running one |
| `/optimizer profile stop` | Freeze capture, remove profiling hooks and optionally export |
| `/optimizer profile clear` | Release a stopped report and lookup references; exported files remain, no forced GC |
| `/optimizer report` | Tick summary and sample-retention information |
| `/optimizer hotspots` | Top 12 sampled inclusive methods, when detailed timing was explicitly enabled |
| `/optimizer entities` | Actual Playing-session and loaded-entity counts |
| `/optimizer chunks` | Loaded chunk count and engine-reported generating queue columns |
| `/optimizer network` | Last-sample engine aggregate payload-byte deltas |
| `/optimizer gc` | Memory, collection and aggregate GC pause deltas |
| `/optimizer benchmark` | Methodology reminder, not a fake client or automatic performance claim |
| `/optimizer export` | Queue JSON and CSV for the current/last capture |

Captures remain in memory after stop for inspection. Starting another capture replaces them. `AutoStart` begins at the official `RunGame` phase; `AutoStopSeconds` includes the complete capture, so benchmark warmup should happen before manual start. A bounded exported capture is not proof of a representative workload.

## Fields and safe defaults

| Field | Default | Meaning and enforced bounds |
| --- | --- | --- |
| `Enabled` | `true` | Master switch for profiler captures and engine replacements. Engine replacements install at mod startup; profiler hooks install when capture starts. |
| `Profiling.AutoStart` | `false` | Start at `RunGame` |
| `Profiling.TickMetrics` | `true` | Read official pre-sleep processing durations after completed `ServerMain.Process` calls |
| `Profiling.ProcessMetrics` | `true` | Process CPU, memory, allocation/GC collection and aggregate GC pause deltas |
| `Profiling.WorldCounts` | `true` | Counts only; never clones terrain or walks loaded entities |
| `Profiling.NetworkCounters` | `true` | TCP+UDP engine payload counters; no packet rewriting or per-player attribution |
| `Profiling.MainThreadCpu` | `false` | Optional OS thread CPU on Linux/Windows; requires tick hook and SafeMode=false |
| `Profiling.SystemTimings` | `false` | Optional sampled inclusive official `ServerSystem.OnServerTick` hooks; SafeMode=false required |
| `Profiling.PhaseTimings` | `false` | Optional inspected entity loop, main dispatch, callbacks and autosave preparation; SafeMode=false required |
| `Profiling.CallbackTimings` | `false` | Optional sampled official callback wrappers attributed by assembly/type/method; SafeMode=false required |
| `Profiling.SystemSampleEvery` | `10` | Sample one call in N per timing key, clamped 1–1000. Deterministic sampling can alias periodic workloads. |
| `Profiling.SampleIntervalSeconds` | `1` | Process/world samples, clamped 1–60; actual elapsed interval is exported |
| `Profiling.MaxSamples` | `3600` | Historical ring capacity, clamped 60–7200. At 1 second, default retains roughly one hour. |
| `Profiling.MaxHotspots` | `64` | Unique timing keys, clamped 8–128. Omitted lookups are counted explicitly. |
| `Profiling.AutoStopSeconds` | `0` | 0 disables; otherwise clamped to 86400 seconds |
| `Profiling.ExportOnStop` | `true` | Serialize/write off the game thread after a bounded owning-thread snapshot |
| `Logging.MaxExports` | `12` | Retain 1–100 numbered JSON/CSV capture pairs, plus latest aliases |
| `Compatibility.SafeMode` | `false` | Skip optional detailed timing and main-thread OS CPU probes; leave stock simulation untouched |
| `Compatibility.DisableHookOnConflict` | `true` | Existing/late foreign Harmony owners disable the entire affected timing group, with visible diagnostics |
| `Compatibility.RequireExactGameVersion` | `true` | Exact 1.22.7 guard is mandatory in this preview; setting false does not enable unsupported engines. Running version is read from loaded API metadata, not an inlined build constant. |
| `Benchmarking.WriteSessionEvidence` | `false` | Opt-in test-only Playing-session UID evidence for real-client harness |
| `Benchmarking.MaxEvidenceBytes` | `16777216` | Current JSONL cap; clamped 65536–67108864 bytes. One previous epoch is retained. |
| `Benchmarking.PresenceIntervalSeconds` | `10` | Heartbeat interval, clamped 5–60, limited to 200 Playing sessions per pass |
| `Benchmarking.RunId` | empty | Optional test correlation string, limited to 128 characters |

The legacy `EntityOptimization`, `ChunkOptimization`, `BlockEntities`, `MemoryManagement`, `Networking`, and `Scheduling` sections reserve broad research flags; their `Enabled` values do not enable engine patches. Use the explicit `EngineOptimizations` switches and actual module states. Entity, memory and scheduling summaries now explain the limited implemented scope. `Dashboard.Enabled` remains reserved; the mod creates no HTTP listener.

## Measurement semantics

Tick work is the inspected official integer-millisecond `StatsCollector` timing recorded **before** engine sleep. Effective TPS is completed ticks divided by actual capture wall time. Main-thread work percentage divides summed processing time by wall time; it includes waits inside processing and is not OS CPU utilization. Process CPU is a percentage of one logical core and may exceed 100%.

The fixed tick histogram covers the full capture, with 1 ms bins through 8192 ms. Histogram overflow is counted and affected percentiles become null, rather than presenting the cap as a measured value. Mean/worst remain exact to the original source resolution. Detailed hotspot percentiles use 0.25 ms bins and are rounded down. Empty measurements are null. Sample `p95TickMs` is the cumulative capture P95, while `averageTickMs` and `effectiveTps` describe the most recent actual interval.

Optional method timings are inclusive, overlap, and cover sampled calls only: do not add parent/child totals or extrapolate to all calls. They can perturb hot paths; compare matched profiler-disabled and enabled trials. Callback metadata is bounded and stores method descriptors, never delegate targets, players, entities, inventories or chunk references. The self-timed overhead reports observer work and explicitly excludes Harmony dispatch, export work and total observer effects.

`managedHeapBytes` is `GC.GetTotalMemory(false)`; GC committed and fragmented bytes describe the most recent GC. They are not proof of a leak. Allocation and total GC pause deltas are process-wide, including other mods and the profiler. No GC is forced. Network bytes are engine payload counters, not physical-interface traffic. Chunk count reads one inspected dictionary Count; no `AllLoadedChunks` cloning, terrain request or generation occurs.

Unavailable capabilities include per-AI/worker costs, exact chunk I/O/worldgen completion durations, complete asynchronous save latency, per-player bandwidth, queue latency and gameplay throughput/correctness. The report lists these gaps explicitly.

## Export and evidence files

The game data directory contains `NewfiesOptimizer/profile-<timestamp>-<serial>.json` and `.csv`, plus atomically replaced `latest.json` and `latest.csv`. One concurrent export is allowed; a second request is rejected instead of queueing unbounded work. The two formats are individually atomic, not a multi-file transaction. The stopped capture is exported on normal mod disposal when configured.

JSON uses schemaVersion=1, kind=`newfies-optimizer-profile`; `tick`, bounded `samples`, `hotspots`, `capabilities`, `modules`, `overhead`, `samplesOverwritten`, `hotspotLookupsOmitted`, and explicit correctness limitations are included. Normal captures contain no account IDs. Reports do not claim bot count or scenario identity; the external runner supplies verified workload evidence.

When opted in, `sessions.jsonl` includes `schemaVersion`, `kind` (`connected`, `presence`, `disconnected`), exact `clientId` (UID), UTC time, runId and the official server's actual `Config.VerifyPlayerAuth` as `authenticationEnabled`. A connected event means the official `PlayerNowPlaying` event, not an NPC. It is not transaction/action evidence. The harness must verify matching accounts and authenticated-server state. Keep this private test evidence off the public website.

The event queue is bounded at 512. Dropped events, file cap/write failure and presence cap are visible in status and report modules. On a new server process the previous evidence file is moved to `sessions.jsonl.previous`; a fresh epoch is started. Cap/write failures invalidate complete-session assumptions; do not treat them as successful clients.

## Failure and rollback

Optional timing groups are independent. Any target conflict or installation failure disables that whole group, so later successful targets cannot hide missing coverage. Foreign owners added later are checked approximately every 10 capture seconds; no claim is made that Harmony owner checks detect every possible external runtime mutation. The stock engine method always remains authoritative. Final-sample failures do not prevent cleanup, unsubscription or patch removal.

To stop collection: `/optimizer profile stop`. To disable across restart: `Enabled=false`. To remove completely, stop the server and remove the mod ZIP. No engine DLL, database schema or saved world data has been patched. Detailed compatibility and real-player benchmark validation are still required before a production optimization release.

## Engine modules

- `EngineOptimizations.OnlinePlayerSnapshots`: true by default; reduces iterator overhead while returning a fresh official-type array.
- `EngineOptimizations.NearbyPlayerQueries`: false by default; enables population-adaptive temporary-list reuse.
- `EngineOptimizations.MinimumNearbyClients`: 128 by default, clamped 0–4096. Below the activation threshold the original nearby-query method stays unpatched. It unpatches again below threshold minus 32 clients; individual calls below threshold use stock behavior. Zero forces activation for controlled component testing.
- `EngineOptimizations.BulkStreamReads`: true by default; accelerates eligible non-aliased reads of at least 256 bytes.

`Compatibility.SafeMode=true` blocks these engine modules. Existing 0.1.0 config files normally retain this value; change it deliberately on staging. New 0.2.0 defaults set it false. Each module reports active, standby, disabled, blocked, conflict or failed status via `/optimizer modules`. Counters are also exported in profile JSON.

These engine modules always require the exact inspected 1.22.7 hashes, even if a legacy compatibility setting is relaxed. They do not implement the older placeholder entity/world-generation/network scheduling options.

Nearby-query reuse remains opt-in because component CPU timings were mixed despite substantially lower allocations. Online snapshots, bulk stream reads, the bounded asset cache, AI filter dispatch, repulsion callback reuse, generation request reuse and completed save snapshot release default on. Nearby-player query reuse remains opt-in.

## Suite integration

Suite 1.2.48 provides NOS status, master/safe-mode switches, independent engine switches, population threshold, asset-cache bound, capture start/stop and export. The in-process control protocol is version 1. Settings are validated and persisted before active patches are replaced. A failed persistence operation leaves runtime settings unchanged. Stop captures before applying settings. Applying settings retains all existing profiling/export configuration not exposed by this editor.

- `EngineOptimizations.AssetCompressionCache`: true by default; accelerates repeated official assets compression only.
- `EngineOptimizations.AssetCacheMaxMiB`: 16 by default, range 1–32. Combined retained source/compressed payload budget. Foreign Harmony owners or a mismatched engine disable the module.

Low-compressibility or over-budget asset buffers use stock compression after the initial probe, avoiding repeated cache-copy overhead. A new asset buffer or reapplying settings permits a fresh probe. Cache hits require at least 25% size reduction on that probe.

## Entity, generation and storage modules

- `EngineOptimizations.AiFilterDispatch`: true by default. Avoids invocation-list allocations while preserving filter order, vetoes and exceptions.
- `EngineOptimizations.RepulseDelegateReuse`: true by default. Reuses weakly held walk callbacks and removes the exactly verified always-true range predicate. Collision results and tick rates remain unchanged.
- `EngineOptimizations.WorldgenRequestReuse`: true by default. Removes capturing factory allocations from generation request lookup; preserves pass escalation, queue order and generation priority.
- `EngineOptimizations.SaveSnapshotRelease`: true by default. Clears the chunk snapshot only after successful background save completion, inside the existing save lock. Failure/retry inputs, dirty flags, formats and transactions are unchanged. Previously saved chunks can become collectible when they unload and have no other references.

These modules work with profiling stopped. Disabling a module restores its stock method. Snapshot counters (`saveSnapshotReferencesReleased`, `saveSnapshotCompletions`) are cumulative for this server process, including across settings reloads; they count references, not memory bytes or disk writes. No forced garbage collection is performed. Chunk-generation algorithms, network batching, chunk I/O, incremental save scheduling and world-generation thread counts are not changed.

### AI task starts

`EngineOptimizations.AiTaskStartDispatch` defaults to true. Suite → Optimizations → Newfies Optimizer exposes its separate checkbox. It removes temporary callback-enumeration allocations in explicit `ExecuteTask` API requests only. Normal autonomous task selection uses a different stock loop; this module is not a blanket AI speedup. Disabling it unpatches the target; a conflict or unsupported official engine leaves stock execution active. No AI interval, range, priority, task or physics rule is changed.

### Suite automatic profiling and updater compatibility

These settings belong to the Suite's `NewfiesWebServer.json`, not `optimizer.json`:

- `AutomaticIncidentProfilingEnabled`: false by default. Profiler → **Automatic 3s captures (adds overhead)** opts in. The lightweight health sampler and manual 10s/30s captures are independent. Safe Diagnostics Mode blocks automatic captures. Disabling this switch cancels a running automatic capture, while a manual capture continues.
- `AllowSameMinorGameModUpdates`: false by default. Mods → **Allow earlier same-version patches** explicitly allows releases tagged for earlier stable patches of the same major/minor version (for example 1.22.0 on 1.22.7). This is broader than exact compatibility. Review the mod author's notes. ZIP identity, version, archive safety, dependency checks, backup and restart staging still apply; no newer/other major-minor game versions are queried and no downgrades are staged.

Floral Zones uses PascalCase `ModID`/`Version`/`Dependencies`; the Suite now accepts these with the same casing tolerance as the game. Ambiguous duplicate fields are rejected. Some Forgotten Armory releases have earlier game-patch tags; their availability can depend on the explicit compatibility choice above.

## Entity allocation and path queue controls (0.7.0)

| EngineOptimizations setting | Default | Scope |
| --- | --- | --- |
| NearestEntitySearch | true | Reuse search state; stock server partition traversal retained |
| InteractableEntitySearch | true | Reuse interactable/filter search state independently |
| PathQueueLimitFix | true | Prevent dequeueing an extra worker request at its cap |
| PathMainThreadBudget | false | Experimental bounded main-thread assistance; worker remains active |
| PathMainThreadBudgetMs | 2 | Soft elapsed-work limit, 1–10 ms; one search may exceed it |
| PathMainThreadMaxSearches | 1 | Maximum searches per assistance callback, 1–8 |
| PathMainThreadGraceMs | 300 | Same queue head must have been observed for this long, 0–5000 ms |

The grace period measures how long the same head has been observed, not its original enqueue timestamp. A worker advancing the queue can reset it. This conservative policy can delay paths; compare behavior under your actual animal workload before enabling. The helper passes each request's creature type to official A*.

Search scratch state is checked out per thread and cleared in finally, including errors. Nested searches use a separate temporary context; only one idle context per thread is retained. No entity, position or filter references remain in idle state. Disable/reapply invalidates scratch generations. Completed report release removes references; the GC decides when memory is collected or returned to the OS. NOS does not call GC.Collect, compact the heap or alter process-wide GC modes.

## Physics packet temporary lists (0.8.0)

`EngineOptimizations.PhysicsPacketListReuse` defaults to true. It reuses only the three temporary lists in the official physics send loop. Packet arrays remain independently owned; no wire format, batching size, send frequency, filtering or simulation changes. One empty buffer set is retained per participating thread, with at most 4,096 entries of capacity in each list; larger sets are discarded. Nested calls use separate sets, and finalizers clear references on success and exceptions. Disable/reapply invalidates idle generations. Suite displays passes, buffer sets created and oversized sets released. These are work counters, not byte savings or TPS estimates.

## Despawn packet projection (0.9.0)

`EngineOptimizations.DespawnPacketProjection` defaults to true. It removes three temporary LINQ projection iterators from the official entity-despawn packet factory. All three stock field-projection passes, output order, default reasons/damage sources and independently owned output arrays remain. Null input uses the original method, including original exception details. This does not change despawn policy, entity lifetime or send frequency. Suite counters show packets and entry counts, not estimated network savings. Unsupported builds and foreign patches retain stock behavior.

## Experimental distance/synchronization modules (0.10.0)

Under `EngineOptimizations`, `AiDistanceThrottle`, `DistantWanderThrottle`, `RepulsionDistanceThrottle`, `WaypointNodePooling` and `AttributeNoOpSkip` each default false. Each has independent activation, conflict and fallback status. Suite controls persist them to optimizer.json and apply immediately; manual JSON edits require restart.

| Setting | Default | Bounds |
| --- | --- | --- |
| EntityProtectionRadius | 64 | 48–256 blocks, plus a fixed 16-block safety margin |
| AiDistanceIntervalMs | 250 | 100–1,000 ms |
| DistantWanderIntervalMs | 1500 | 250–10,000 ms |
| RepulsionDistanceIntervalMs | 100 | 50–500 ms |
| EntityPolicyInclude | game:* | Up to 64 qualified wildcard patterns |
| EntityPolicyExclude | game:player, game:trader-* | Up to 64 qualified wildcard patterns |

Patterns cannot override safety exclusions. Include/exclude inputs are comma-separated in the webpanel. These policies affect distant decisions, so start with one switch and compare real animal behavior and tick distributions at the same player/chunk/entity load. No forced GC or global GC configuration changes are added.

Leave AttributeNoOpSkip disabled if a mod intentionally removes an absent key to force a full attribute resync. Custom behavior/task implementations can exempt many animals on a heavily modded server; matching a code cannot override those safety exclusions.

## Broadcast/lifecycle allocation switches (0.11.0)

EngineOptimizations.BroadcastFilterReuse and EngineOptimizations.EntityLifecycleListReuse default false, with separate actual statuses for each target. Configure in Suite → Optimizations and Save and Apply. Broadcast filtering normalizes only empty method-local arrays and runs stock send code. Lifecycle lists retain at most 4,096 slots per temporary list/thread/scope and release entity references after each call; packet arrays remain caller owned. No tick suppression, TCP consolidation or transport policy is added.
