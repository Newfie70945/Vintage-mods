# Configuration and profiler coverage

Newfie's Server Optimizer **0.5.0 engine-patch preview** reads `ModConfig/optimizer.json` and writes defaults on first launch. A ready-to-copy example is in `config/optimizer.json`. Restart after file edits, or apply supported switches live through Suite → Optimizations. Each engine module is independently controlled; exact compatibility and conflict guards retain stock behavior when needed.

## Capture controls

All commands require `controlserver`; console commands use `/optimizer ...`.

| Command | Result |
| --- | --- |
| `/optimizer status` | State, errors, conflicts, evidence problems and latest export |
| `/optimizer profile start` | Begin a new bounded capture; refuses to discard a running one |
| `/optimizer profile stop` | Freeze capture, remove profiling hooks and optionally export |
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

`EntityOptimization`, `ChunkOptimization`, `BlockEntities`, `MemoryManagement`, `Networking`, and `Scheduling` each have `Enabled=false`. These sections reserve the requested configuration structure for future measured research. **They do not implement an optimization**, and setting true reports `notImplemented` instead of changing gameplay. `Dashboard.Enabled` is similarly reserved: the dashboard is a separate local tool reading exported files; the mod creates no HTTP listener.

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
