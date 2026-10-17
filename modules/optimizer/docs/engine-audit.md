# Vintage Story 1.22.7 engine audit

This is a static audit of the official Linux server distribution, performed on 2026-10-17. It identifies real measurement points and constraints; it does **not** establish that a candidate optimization improves a live server. The machine-readable companion is [engine-targets.json](engine-targets.json). The original NOS source was not available for this rebuild.

## Exact target

The installed `VintagestoryServer.runtimeconfig.json` targets `net10.0`, Microsoft.NETCore.App 10.0.0. The server announces game **1.22.7**, network **1.22.6**, API **1.22.0**. These versions are distinct; a network-version field of 1.22.6 is correct for this release.

| Official assembly | SHA-256 |
| --- | --- |
| VintagestoryLib.dll | `e08f22b493b92feaf0aaeb79d22437ea0f7efc38aa7f72a04a47f98bc0e40df0` |
| VintagestoryAPI.dll | `034283e7e9d98eae45ee63005576fd89badc3c995b531cc4c3fe46f3eb2d3296` |
| Mods/VSEssentials.dll | `a28565c5c9181f8cc84b98a2b7457ab824b8ecb7763714448da1f7c245aecd6e` |

Inspection used ILSpy 11.1.0.9782 with references from the official game and .NET 10. No modified official assemblies or decompiled engine project are distributed here. A matching version string alone is insufficient for an invasive patch: the expected type, signature, IL shape, and conflicts must also match. An unfamiliar binary should leave the affected module disabled with diagnostics.

## Actual tick timing

`Vintagestory.Server.ServerMain.Process()` executes server systems, registered game callbacks, then `ProcessMain()` before recording its processing duration. It subsequently sleeps to satisfy `Config.TickTime`.

The useful stock measurement is:

```text
stats = server.StatsCollector[server.StatsCollectorIndex]
lastIndex = (stats.tickTimeIndex + stats.tickTimes.Length - 1) % stats.tickTimes.Length
processingMilliseconds = stats.tickTimes[lastIndex]
```

`StatsCollection.tickTimes` has ten integer-millisecond entries; `ticksTotal`, `tickTimeTotal`, and `tickTimeIndex` update at the same point. Four collectors rotate about every two seconds. A postfix must confirm that a new sample was written. Suspended and standby calls return without recording one, and a fatal exception can also skip the sample. Comparing only the array index is insufficient because a ten-entry ring eventually wraps; use collector identity/index and tick counter. No optimizer should claim submillisecond precision from these stock measurements.

Prefix/postfix wall time around `Process()` includes deliberate sleep, so it is **not** processing cost. Likewise, an ordinary 50 ms mod tick callback interval measures callback scheduling, not the cost of the entire engine tick. Record effective TPS as actual completed engine iterations per elapsed time, independently from processing duration and configured interval. Fraction of the tick budget consumed is elapsed busy work, not an OS thread-CPU measurement.

## Threads and attribution

Main-thread systems include entity simulation, player awareness, chunk transmission, packet dispatch, and save preparation. Separate stock loops include `chunkdbthread`, `CompressChunks`, `Relight`, `ServerBlockTicks`, `UdpSending`, packet parsing, additional world-generation workers, and the Essentials `ai-pathfinding` worker. `ReducedServerThreads` changes which work executes separately.

`ServerMain.Process()` loops over `ServerSystem` instances and calls `OnServerTick(float)` only after each system's own update interval. A hook on the abstract/base declaration does not automatically instrument every override. Resolve concrete overrides deliberately, bound the target count, check conflicting Harmony owners, and isolate failure to the measurement module concerned.

Measurements of parent and child methods are inclusive. Entity simulation includes AI; AI includes path and callbacks; save preparation can contain mod callbacks. Their times must not be added to obtain total tick time. Worker-thread totals can overlap each other and the main thread. Keep labels for category, thread context, and measurement coverage.

An instrumentation prefix/postfix/finalizer must preserve all arguments, return values, original exception behavior and callback order. Telemetry failures must be caught internally. Never run the original method a second time after part of it has already executed as a fallback.

## Measurement coverage and limits

| Area | Verified source or measurement point | Interpretation |
| --- | --- | --- |
| Main tick | `ServerMain.Process`, stock `StatsCollector` | Processing time before sleep, integer milliseconds. |
| Process CPU | `Process.TotalProcessorTime` delta / elapsed time | State whether one logical core equals 100%; separate generator CPU. |
| Thread CPU | OS thread CPU counters, if available | A main-thread stopwatch is not CPU. Expose unavailable explicitly where unsupported. |
| Memory | `GC.GetTotalMemory`, `GC.GetGCMemoryInfo`, `Process.WorkingSet64`, `PrivateMemorySize64` | Heap, committed/fragmented GC space, working set and private memory differ. Process private minus managed live bytes is not a precise native-leak diagnosis. |
| Allocations/GC | `GC.GetTotalAllocatedBytes`, collection counters, `GC.GetTotalPauseDuration` where supported | Cumulative process deltas; the last GC's `PauseDurations` cannot be summed repeatedly as new pauses. No forced GC in normal collection. |
| Entity simulation | `ServerSystemEntitySimulation.OnServerTick` / `TickEntities` | Includes player ray traces, entity behavior, deaths/despawns. |
| AI | `AiTaskManager.OnGameTick`, `StartNewTasks`, `ProcessRunningTasks` | Task lifecycle plus eligibility checks and hooks; not only combat AI. |
| Pathfinding | `PathfindingAsync.OnMainThreadTick`, `ProcessQueue` | Main overflow and background work are distinct. Queue Count is an approximate concurrent snapshot. |
| World generation | `ServerSystemSupplyChunks.GenerateChunkColumns_OnSeparateThread`, `runGenerators` | Stage-aware, potentially concurrent; aggregate durations do not equal wall-clock throughput. |
| Chunk reads | `TryLoadChunkColumn` | Includes database/cache/deserialization work, not a disk-only timing. |
| Chunk transmission | `ServerSystemSendChunks.OnServerTick` / `sendAndEnqueueChunks` | Per-player chunk collection, serialization and sending work. |
| Block systems | `ServerSystemBlockSimulation.OnServerTick` and `OnSeparateThreadTick` | Main queued updates and worker chunk scanning have different costs and semantics. |
| Registered callbacks | `GameTickListener.OnTriggered(long)`, `GameTickListenerBlock.OnTriggered(IWorldAccessor,BlockPos,long)` | Attribute by bounded/cached handler method and assembly; retain no handler/world/player references. Both `Handler` and `HandlerBare` matter for block callbacks. |
| Delayed/event callbacks | `EventManager.TriggerGameTick`/`TriggerGameTickDebug`, event dispatch | Some delayed handlers are invoked directly and bypass the two listener methods. Report this coverage gap. |
| Network | `ServerMain` cumulative TCP/UDP byte counters and incoming parse/dispatch | Engine-reported byte counts, not measured NIC wire usage; a TCP path counts bytes before send succeeds. |
| Save preparation | `ServerSystemAutoSaveGame.doAutoSave`, `SaveGameWorld(bool)` | Main pause, mod notification, player/mapregion serialization, preparation for offthread save. |
| Save completion | `ServerSystemLoadAndSaveGame.OnSeparateThreadTick` while `runOffThreadSaveNow` | Actual dirty chunk/mapchunk/savegame work; report separately and correlate with preparation for end-to-end latency. |
| Disk I/O | OS process I/O counters plus save/read timing, if available | Method duration alone does not measure bytes physically read from/written to storage. |
| Third-party mods | Bounded callback/method attribution | Identifies the callback's defining assembly where possible, not all indirect work caused by a mod. |

Snapshot large collections on the engine thread or use supported thread-safe count APIs. A profiler must not enumerate mutable world dictionaries concurrently simply because a dashboard poll arrived on an HTTP thread. Sample at a configured low frequency, bound histories/labels, and export immutable snapshots.

## Evidence-backed allocation candidates

These are **candidates for focused experiments**, not claims that they are enabled, production validated, or faster.

1. `ServerMain.GetPlayersAround(Vec3d,float,float,ActionConsumable<IPlayer>)` constructs a temporary `List<IPlayer>` and returns `ToArray()`. A checked-out, per-thread temporary list could avoid temporary collection allocations while preserving a fresh caller-owned array. It must retain exact state/range checks, callback count/order and exceptions. Reentrant predicates require another temporary buffer or the stock path; clear references in `finally`, and discard oversize buffers. Never cache returned player arrays across calls.
2. `ServerMain.AllOnlinePlayers` uses a LINQ pipeline over `Clients.Values` and returns a fresh array. Eliminating enumerator/pipeline allocations may help frequently called paths, but reading `ConnectedClient.Player` twice introduces a race relative to the original single read. Pending connections mean the client collection size is not the player count. A naive full-size array plus shrink may allocate **more** than stock. Preserve caller ownership and compare no-pending, pending, join/leave and reentrancy cases.
3. `ServerSystemEntitySimulation.TickEntities(float)` allocates a temporary despawn list each invocation. Reuse may reduce temporary allocation, but any patch must preserve per-entity catches, iteration order and exact despawn phase. Because this is an authoritative gameplay loop, a broad copied implementation is a substantial compatibility burden. Prefer no patch until allocation sampling demonstrates useful volume.
4. `ServerSystemSendChunks.sendAndEnqueueChunks` allocates a short temporary list for unresolved forced mapchunk requests. Existing reusable send buffers and per-tick packet caches show the engine already reduces allocation here. Reuse must handle exceptions/reentrancy and preserve forced-send semantics; profiling must justify the extra patch.

Focused tests need retained-reference cleanup, exceptions, recursive callbacks, thread separation, oversize bursts, empty results and identical ordering. Allocation bytes per call are useful evidence for the helper; they are not evidence of a meaningful whole-server tick improvement. Production recommendations require matching player workloads, A/B/C/D trials, long sessions and mod compatibility.

## Why the broad scheduling proposals remain gated

**Entity activation and AI.** Stock entity ticking checks dimension eligibility; repulsion already checks inactive state, interaction/movement flags, mounted entities and startup delay. AI manager calls include critical attack/escape behavior and mod task lifecycle hooks. Blanket distance-based skipping changes simulation. Class mods, xSkills and modded creatures require explicit behavior exemptions, matching combat tests and measured benefit before any scheduler is enabled.

**Repulsion and partitions.** `EntityBehaviorRepulseAgents` uses existing spatial partitions, dimension checks and `ICustomRepulseBehavior`. Changing pair order, math or frequency changes physical motion. `EntityPartitioning.PartitionEntities` runs every 32 ms and rebuilds partition chunks, but players retain `entityListForPartitioning` references and `Partitions` is public. A simple pooled replacement can corrupt retained collections. Diagnose allocation first.

**Pathfinding.** One concurrent queue feeds both worker and main overflow execution. The main path has a nominal 1000-task cap; worker `ProcessQueue` is called with 100. A static concern is that each loop dequeues `Next()` before testing its remaining count, potentially consuming one extra task at the cap. This is a suspected correctness issue, not yet a reproduced defect in this audit. Reproduce with a deterministic queue fixture and inspect `Finished` before proposing a separate targeted correction. Reducing work budgets can delay navigation and combat; dropping or deduplicating mutable task objects without matching semantics is unsafe.

**Chunks/world generation.** Ordinary chunk requests already use an indexed queue, stages, neighborhood dependencies, request disposal/requeue flags and bounded attempts. Fast loads, peeks, simple loads, forced sends, and additional worldgen workers have different lifecycles. New fairness policies must preserve dependency ordering, request completion, teleport semantics and save ownership. Profile queue latency and throughput before replacing them.

**Block simulation.** Stock worker code deduplicates overlapping player-near chunk scans in a dictionary and marks chunks/mapchunks fresh. Main block callbacks are already limited by `MaxMainThreadBlockTicks`. Removing freshness updates, lowering simulation range or deferring gameplay ticks would affect world behavior; it is not an allocation-only improvement.

**Autosave and I/O.** The official server already splits normal autosave preparation from offthread dirty-chunk/mapchunk/savegame work. Replacing it with an incremental policy without measuring both phases and crash/reload behavior risks persistence. Keep diagnostics separate from future experimental save changes. Do not change the native SQLite provider resolver or load a second SQLite native image into a server already using the official provider.

**Network.** Chunk packet caches distinguish entity payloads and expire within one send tick. OreShield can make content player-specific. Broader sharing, caching or batch suppression must preserve concealment, anti-cheat observation, logging, reliable ordering and critical state changes. Packet drops are never a valid performance technique.

## Real client feasibility

The server distribution contains networking and shared code, but that does not make it a complete, authorized headless client. Normal admission requires the inspected versioned packet handshake, authenticated sessions, mod/assets synchronization, spawn readiness, connection maintenance, movement and interaction state, and correct TCP/UDP handling. A socket that merely connects is not a joined player and cannot establish multiplayer performance.

Use official clients with authorized separate account sessions and automation first. Keep `VerifyPlayerAuth=true`; do not bypass account authentication or licensing. The harness must confirm the player is admitted/spawned through real network sessions and report failed login/interaction attempts. If the required accounts, graphical client installation, or resources are unavailable, report the maximum authenticated population actually tested as zero. NPCs, fabricated `ServerPlayer` objects and direct API calls are focused component workloads only.

An official-client harness can launch isolated client data directories, execute reproducible UI input scenarios, capture logs, and measure generator resources separately. It still needs per-client verified checkpoints for join/spawn/movement/interactions; unsupported automation capabilities must be documented rather than reported as completed actions. Do not stress the live Newfies server or arbitrary public hosts.

## Promotion criteria

A module remains experimental/off until its exact target and conflicts are checked, ownership/lifetime are verified, focused correctness tests pass, and paired workloads show benefit beyond profiler overhead and trial variation. The same world snapshot, pack, settings, accounts, warmup, duration and hardware allocation must be used across unmodified baseline A, profiler-only baseline B, one-feature candidate C, and validated combination D. Track completed work, errors, disconnects, save/reload integrity and queue latency alongside CPU, tick percentiles and memory. A short idle startup run can validate loading and report generation; it cannot prove player scaling or long-term leak freedom.
