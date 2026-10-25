# Audit: chunk loading, entity cleanup, containers, crafting and allocation (NOS 0.20.13 / Suite 1.2.51)

Scope: the five targets proposed after the production logs. This is a source audit plus one measurement change. It was done **without the official 1.22.7 assemblies or a .NET SDK**, so nothing here was compiled, run, or checked against the engine's IL. Statements are marked **verified** (read in this repository), **inferred**, or **to verify live**. No new engine patch was written: every existing module is pinned to an IL hash and has differential tests against the real game DLLs, and a patch written blind would not meet that bar.

## Summary

1. **The repository's own evidence says small allocation patches have not moved steady tick time.** The 0.20.12 pair (dispatch reuse off vs on, 6,000 entities) measured 187.08 ms vs 187.12 ms average tick, and 0.20.13 states the same limit. The chunk-load stall in the logs (246-303 ms in chunk-loaded processing, 1-2 ms of it block-entity loading) is therefore most likely **subscriber time that nothing measures**, not allocation.
2. **The unmeasured stages were exactly the ones in question.** `PhaseMethods()` times the entity loop, game-tick dispatch and autosave preparation, but not main-thread chunk-column load completion, chunk unload, or the entity-despawn send.
3. **Our own code runs in those stages.** The Suite's OreShield postfixes `ServerSystemSupplyChunks.mainThreadLoadChunkColumn`, and BlockLogger/AnimalLimiter handle every spawn and despawn synchronously. They look cheap (see below) but had no total-time metric, and the existing "slow >= 1 ms" metric cannot see thousands of fast calls adding up.
4. **A profiler policy trap.** `RuntimeProfiler` treated any Harmony owner other than itself as a foreign conflict and disabled the **whole** timing group. That included NOS's own engine modules and the Suite, so enabling an entity-lifecycle module or OreShield could silently switch off phase timings.

## What changed in this pass

| Change | Where | Notes |
| --- | --- | --- |
| `chunkLifecycleTimings` profiler module | `EngineHookTargets.ChunkLifecycleMethods`, `RuntimeProfiler.InstallChunkLifecycleGroup` | Times `ServerSystemSupplyChunks.mainThreadLoadChunkColumn(ChunkColumnLoadRequest)`, `ServerSystemUnloadChunks.UnloadChunkColumns` and `ServerSystemEntitySimulation.SendEntityDespawns` on **every call** (not 1-in-N), inclusive of other patches' work. Observational only: no behavior change, active only during a capture with `Profiling.PhaseTimings=true` and `SafeMode=false`. |
| Isolated from the existing phase group | same | `PhaseMethods()` is unchanged (still five targets). A missing method, an unknown foreign patch or a full hotspot table skips only that target and is reported in the module reason. |
| Companion owners no longer count as conflicts | `RuntimeProfiler.IsCompanionOwner` | Tolerates `newfies.serveroptimizer.*`, `newfies.serveressentials.*` and `newfiesserveressentialssuite.*`. Unknown owners still disable the group. |
| Every-call timing flag | `HotspotCounter.AlwaysSample` | Used only by the new group. |
| Suite burst timers | `SuiteBurstTimer`; `EntityHooks`, `OreShieldService` | New Performance-page metrics: `BlockLogger spawn handling (total per second)`, `BlockLogger despawn handling (total per second)`, `OreShield column-load hook (total per second)`. P95/max expose the worst one-second burst. |
| Tests | `EngineHookTests` (needs game DLLs); `tests/runtime/burst-timer` (Suite assembly) | Not run here. |

## 1. Chunk-load callbacks

**Existing (verified):** `EntityLoadedDispatchReuse` (0.20.12, off), `ListenerRegistrationIndex` (0.20.13, off; 6,000 registrations 11.86 -> 2.68 ms entity, 12.60 -> 2.49 ms block in a component test), `BlockUpdateListReuse`, `ChunkLoadRequestListReuse`, `ChunkUnloadListReuse`, `ChunkUnloadStagingReuse`, `DelayedCallbackSnapshotReuse`, `SpawnOpportunityListReuse`, `SpawnColumnArrayReuse` (all off), `WildBushCallbackFlatten` (on).

**Findings**
- **Verified:** per-subscriber scopes (`nos-entity-event:<type>.<method>`) exist for entity loaded/despawn only. Nothing attributes time inside the **chunk-column** events, and the profiler lists `chunkGenerationAndIoTimings` as unavailable.
- **Verified:** the Suite already defers its own chunk-load work. `OreShieldBlockVirtualizer.OnColumnLoaded` only queues boundary refreshes (about 4 lookups per Y layer under one lock); the real work runs later under `BoundaryRefreshesPerTick`. So "spread deferrable callbacks across ticks" is already true for our code. **Inferred:** OreShield is unlikely to explain 250 ms, but this was never measured; the burst timer now does.
- **Not recommended:** deferring *other mods'* `ChunkColumnLoaded` subscribers. Subscribers rely on synchronous completion and ordering; that is a gameplay-visible change, not an allocation change.

**Next steps (need the game DLLs)**
1. Capture with `Profiling.PhaseTimings=true`: compare `mainThreadLoadChunkColumn` against the tick. If it is most of the 246-303 ms, the stall is inside column completion; if not, look at unload or the despawn send.
2. If column completion dominates, add per-subscriber scopes for the chunk-column events, modelled on `EntityEventDispatchOptimizations` (immutable delegate enumeration, labels from `callback.Method`). **To verify:** the exact trigger method and delegate shape. `ChunkUnloadStagingTests` already references `ServerEventAPI.TriggerChunkColumnUnloaded`, so a loaded-side counterpart is likely but unconfirmed.
3. Only then consider patches. A named third-party subscriber consuming most of the time cannot be fixed by NOS (0.20.12 said the same of the BASICs chat-history handler).

## 2. Entity despawn and unload cleanup

**Existing (verified):** `EntityLifecycleListReuse` (tick/despawn local lists), `ChunkUnloadStagingReuse`, `EntityDespawnDispatchReuse`, `DespawnPacketProjection` (on), `DespawnCleanupTimings` (diagnostic). Frames are per-thread, epoch-invalidated, bounded (4,096) and cleared in finalizers.

**Findings**
- **Verified, no defect found** in `ChunkUnloadStagingOptimizations`, `EntityLifecycleOptimizations` and `BlockChunkScratchOptimizations`: nested-call parents, exception cleanup, oversize release and stale-epoch discard are all handled; lists are cleared (references released) before reuse.
- **Verified:** 0.20.13's mass-unload test ran about 100,000 cleanup calls per arm with zero failures but **did not reproduce** the production 150 ms per-entity waits. The cause is therefore still unattributed.
- **Fixed (measurement):** the Suite's despawn metric only recorded calls >= 1 ms. A mass unload of thousands of 0.05 ms calls was invisible. `BlockLogger despawn handling (total per second)` now sums them. Suite per-entity cost itself looks small (compiled-delegate accessors in `EntityRuntimeAccessors`, `HashSet<long>` buckets and cached rule resolution in AnimalLimiter).
- **Inferred:** `DespawnReason.ToString()` boxes and allocates per record; negligible next to the queue write.

**Next:** during a real unload, run phase timings plus `DespawnCleanupTimings` and read `SendEntityDespawns`, `UnloadChunkColumns` and the `nos-despawn-*` scopes together; the first one that is large decides the work.

## 3. Barrels, firepits and container ticking

**Existing (verified):** `IdleFirepitFastPath` (off), `BarrelIngredientPairing` (on), `BarrelMatchCountGuard` (off), `ContainerTransitionScan`, `ContainerTickTraversal` (off).

**Findings**
- **Verified:** guards are strong: IL hash pinned, exact-type checks (subclasses and custom slots fall back), custom accessors never read twice, `Ingredients` re-read after matching hooks, exceptions propagate and stand the module down. Active cooking, fermentation and inventory changes are not touched by design.
- **To verify live (possible semantic gap, not a confirmed bug):** the idle firepit fast path requires `furnaceTemperature == 20` and `burnstate == "cold"`, and then skips the native tick, writing only `inputStackCookingTime = 0`. If the native tick also relaxes an idle firepit's temperature toward the ambient climate temperature, then in biomes where ambient is not 20 degrees C the fast path either rarely applies (counted as fallbacks) or holds a never-lit firepit at 20. The authors list `enviromentTemperature` and `changeTemperature` as dependencies, so this was likely considered. Check it empirically: read `calls` vs `fallbacks` from `/optimizer modules` on the real world, and compare a cold-biome firepit's temperature after several minutes with the module on and off.
- Recommendation: do not widen "idle" definitions (for example idle barrels) without an equivalent IL-hash review and an on/off differential test over a long run.

## 4. Crafting and inventory synchronization

**Existing (verified):** `CraftingCandidateDedup` (off; reference-equality set once a query exceeds 16 candidates, native `Contains` path for any `GridRecipe` subclass), `ShapelessStackMerge`, `ShapelessWildcardMatch`, `AttributeNoOpSkip` (all off).

**Findings**
- **Verified:** `CraftingCandidateOptimizations` changes only duplicate membership and temporary storage. It does not reduce the number of recipes evaluated, so it cannot fix a stall caused by searching the whole recipe list on each grid change.
- **Inferred:** a real fix for repeated recipe searches is an index or prefilter in front of `InventoryCraftingGrid.FindMatchingRecipe`. That changes evaluation order and therefore which recipe wins, and it must preserve batches, attributes, matching hooks and XP. It needs the official source and a differential test over every loaded recipe before it is attempted.
- **Not enough evidence:** redundant inventory synchronization. Only missing-attribute resync is covered today; anything broader changes what clients see and needs packet-level evidence.
- **Unknown:** which mod's ore crushing stalls. Use Suite -> Tick Debugging -> Capture Incident + Profile during a crush. The new cause classification separates "single dominant marker" (name the mod/marker) from GC and unmarked time, which decides whether this is recipe search, sync or mod code.

## 5. Lower allocation pressure

**Findings**
- **Verified:** most validated modules are default-off and their component savings are small (tens to low hundreds of bytes per call). 0.20.12 saw no steady-tick or meaningful GC change from the dispatch pair.
- **Verified (supplied run):** about 16 GB managed heap, 7.4 GB fragmented, 75 MB/s allocation. That points at heap shape and large-object behavior, not per-call garbage.
- **Not done by policy:** forced collections, latency-mode changes and heap limits. Agreed: forcing GC would add stalls.
- **Suggested experiment (separate from patch claims, like the physics-thread comparison):** a labelled A/B of GC settings on a staging copy (for example concurrent vs non-concurrent GC, `GCConserveMemory`, server vs workstation GC), measuring GC pause total and p99 tick under the same workload. Find the largest allocation sources with `dotnet-counters` / `dotnet-trace` before touching more call sites.

## How to use the new measurements

1. Set `Profiling.PhaseTimings=true` and `Compatibility.SafeMode=false` in `optimizer.json` and restart.
2. `/optimizer profile start`, travel between populated areas, `/optimizer profile stop`.
3. `/optimizer modules` should list `chunkLifecycleTimings` as active (or say exactly which target was skipped and why). `/optimizer hotspots` then shows the three stages with exact call counts; calls are not sampled.
4. In the Suite Performance page, compare `OreShield column-load hook (total per second)` and the BlockLogger spawn/despawn totals against the stage times to see our own share.
5. If the module reason says `hotspot limit reached`, raise `Profiling.MaxHotspots` (up to 128): `SystemTimings` alone can use most of the default 64 keys, and the three chunk stages are installed after the phase group.
6. Summarise an export without reading raw JSON: `python3 tools/profile_findings.py <export-dir>/latest.json` (add `--help` for options). It ranks tick spikes, stage share of tick time, GC pause share, module failure counters and capture-quality caveats. Run its tests with `python3 -m unittest discover -s tools/tests`.
7. Treat all timings as inclusive and overlapping (a stage contains subscribers and other patches). Profiling adds overhead, so do not compare profiler-on tick latency with profiler-off runs.
