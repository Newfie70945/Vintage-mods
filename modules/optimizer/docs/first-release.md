# First public release preparation — official Vintage Story 1.22.7

NOS patches the official server in memory. It does not replace the runtime, rewrite game DLLs, or require a client installation. Suite is optional; its Optimizations page provides live controls. Exact-engine checks, conflict detection, independent switches and failure stand-down remain enabled.

This is a release preparation record for 0.20.16-preview, not a declaration that all optional modules are production proven. The supplied production module table contains **33 active modules, 3 conflicts and 24 disabled modules**. The owner reports substantial improvements with this combination. No matched stock/NOS production timing capture accompanied that table, so it does not quantify the gain or establish which module produced it. Numeric settings, profiling switches and modules omitted from the table cannot be inferred.

## How this setup differs from shipped defaults

The following **16 switches** were enabled in the supplied setup but default off. Two switches each control two modules: EntityLifecycleListReuse and ListenerRegistrationIndex.

| JSON setting | What it changes |
| --- | --- |
| CraftingCandidateDedup | Temporary candidate membership during recipe search; native crafting quantities and hooks remain |
| BarrelMatchCountGuard | Rejects impossible ingredient counts before more expensive matching |
| EntityLifecycleListReuse | Temporary entity tick-cleanup and despawn lists |
| CachedCollisionGenerationReuse | Callback allocation in the cached collision-generation path |
| ContainerTransitionScan | Container transition traversal allocations |
| ContainerTickTraversal | Container ticking traversal allocations |
| NearbyEntityListReuse | Nearby-entity query scratch storage |
| CuboidEntityListReuse | Cuboid-entity query scratch storage |
| ShapelessStackMerge | Stack-merge matching allocations |
| ShapelessWildcardMatch | Wildcard ingredient matching allocations |
| AttributeNoOpSkip | Avoids resync when removing an already-absent attribute |
| ListenerRegistrationIndex | Free listener-slot lookup for native block/entity registration |
| EntityDespawnDispatchReuse | Entity despawn event dispatch allocations |
| TemperatureAnimationChecks | Native temperature animation checks |
| EmotionSearchReuse | Animal emotion-search callbacks |
| DespawnCleanupTimings | Diagnostics when the native tick profiler runs; **not a performance optimization** |

The existing defaults for physics/despawn packets, player snapshots, bulk reads, AI dispatch, nearest/interactable searches, repulsion callbacks, worldgen request lookup, completed-save snapshot release, barrel pairing, bush callback flattening and animation flag scanning were also active. Keep these conservative defaults for the first release; the extra options remain opt-in until broader workload evidence supports promoting them.

`AssetCompressionCache` defaults on but was blocked by GeneLib. `IdleFirepitFastPath` and `CollisionTraversalReuse` default off but had been requested and were blocked by ARL and Scaffolding respectively. A conflict means overlapping patches, not proof that the other mod causes crashes. Do not bypass these guards.

## Reproduce the effective setup

The observed preset (`config/presets/newfies-observed-1.22.7.json` in source; `guides/presets/newfies-observed-1.22.7.json` in the ZIP) is an **EngineOptimizations overlay**, not an entire optimizer configuration and not an automatically loaded preset. It requests the 33 active modules from the supplied table and explicitly disables the three conflicted options. Unreported newer optional modules are off in this preset. Capacity/range/interval values remain outside this overlay; it does not reproduce unknown numeric settings.

For an existing server, copy these boolean values into its existing `EngineOptimizations` object while stopped, preserving the rest of its configuration, then restart. Alternatively set the corresponding switches in Suite and use **Save and Apply NOS Settings**. Do not replace the whole configuration with this partial file. Check `/optimizer modules` for actual activation; a checked switch is a request, not confirmation. Existing configurations keep their values on upgrade.

The automated module test loads this preset against the inspected official engine and checks the exact active set, disabled set, and example-vs-compiled default parity. It does not include the owner's full modpack or authenticated game clients.

## Next options to test, in order

Keep the working setup as a baseline. Test **one switch at a time**:

1. `EntityLoadedDispatchReuse`: complements the already active despawn dispatch reduction; exercise join, teleport, chunk loading and entity spawning.
2. `BlockUpdateListReuse`: test busy bases, construction and neighbor updates. It reuses temporary lists; it is separate from the experimental simulation budget.
3. `ChunkLoadRequestListReuse`: test exploration and teleport/join pressure, checking that terrain arrives promptly.
4. `ChunkUnloadListReuse`: test leaving/revisiting areas, unload callbacks and normal save/reload. This controls candidate-list and notification-coordinate reuse together.

These are candidates, not guaranteed wins. Stop after a regression, disable the last option, and repeat the same workload. Do not enable every pooling option together: lower allocation can trade against CPU cost and retained buffer capacity.

Keep distance-based AI/repulsion throttles, block simulation budgeting, main-thread path assistance and barrel recipe memoization off for the initial public defaults. They change scheduling or can delay detection of attribute-only recipe changes. Leave nearby-player query reuse off at ordinary player counts; earlier CPU results were mixed. Detailed timing and automatic capture should not be enabled merely for performance.

## Release validation and limits

Before removing the preview label:

- Run the full optimizer build/test suite against the exact supported server assemblies. Validate all-on conflict/failure/disposal behavior as well as the default and observed-preset activation sets.
- Compare stock vs conservative defaults vs observed preset on identical copies of the same world and modpack. Use representative player sessions with exploration, teleporting, ore crushing/shift crafting, active containers, animal AI and normal autosaves; bots supplement this work.
- Record player/entity/loaded-chunk counts, tick p50/p95/p99/max and long-stall counts, CPU, allocation rate, Gen2 pauses, post-Gen2 heap floor, committed memory and fragmentation. Include warm-up, several autosave cycles and multi-hour sessions; a short component benchmark is insufficient.
- Confirm crafting outputs/XP and container contents, chunk arrival, entity behavior, save/reload integrity, config persistence, disable/re-enable and clean removal. NOS does not intentionally change world format, but interacting runtime patches cannot be promised risk-free.
- Keep per-run versions, configuration, actual module states and conflicts with the results. Publish measured limits rather than a universal TPS/player-capacity claim.

No forced GC, automatic heap compaction or invasive autosave/worldgen replacement is introduced by this preparation pass. Removing NOS stops its patches on the next clean server start; it cannot undo gameplay changes already saved while any mod was running.

## Next server-side anti-cheat improvements

The Suite already has server action checks, shadow mode, noclip correction history, scoped exemptions, bounded incident evidence and occurrence counts. The most useful next work is:

1. **Activity-normalized findings:** invalid actions per 1,000 relevant requests as well as per-hour counts, so busy miners do not look suspicious solely through activity.
2. **Short authoritative combat history:** bounded server position history to evaluate latency-sensitive reach, with teleport/mount/dimension resets and explicit uncertainty for custom weapons.
3. **Inventory transaction evidence:** bounded before/after quantity and revision records around suspicious native operations, preserving crafting and mod hooks. Establish conservation evidence before adding denials.
4. **False-positive replay tests:** saved, anonymized movement traces for Draconis mounting/dismounting, doors, chiseling, elevators, server stalls and teleport corrections.
5. **Better evidence on corrections:** group consecutive attempts by collision volume, correction outcome and server health, rather than promoting a single client report to proof.

A modified client can conceal its mod list. Client integrity responses remain advisory; enforcement must rest on server-observed actions and sufficiently strong repeated evidence.
