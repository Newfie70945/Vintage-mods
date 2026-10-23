# NOS replacement coverage — 0.11.0 preview

Public feature catalogs checked at [Tungsten](https://mods.vintagestory.at/tungsten) and [Synergy](https://mods.vintagestory.at/synergy). This compares documented purposes, not copied implementations or equivalent performance. NOS patches the inspected official 1.22.7 assemblies in memory; the original engine remains responsible for gameplay and networking. The installed NOS module status, not this table, establishes whether a patch is active.

| Area documented by the other mods | Current NOS coverage | Remaining difference |
| --- | --- | --- |
| Online/nearby-player queries and ServerMain LINQ | Independent online/nearby queries plus optional empty-exclusion broadcast fast paths | No blanket replacement of all ServerMain/broadcast LINQ |
| Entity proximity searches | Nearest/interactable query state reuse | Does not replace every GetEntitiesAround overload |
| AI and repulsion | Allocation reductions plus opt-in distant task/wander/stationary repulsion scheduling | Full entity sleep remains absent |
| Physics packet temporary collections | Bounded independent temporary-list reuse | Does not cover every physics simulation/method list or stationary collision shortcut |
| Entity despawn packets | Direct stock-equivalent projections with packet-owned arrays | Optional stock lifecycle despawn-list reuse added; bulk-attribute paths remain separate |
| Generation requests and save retention | Existing request allocation reduction; completed save snapshot reference release | No general chunk I/O/load/unload rewrite, incremental autosave or GenTerra noise/BitArray/native-noise replacement |
| Asset compression and stream reads | Bounded server-assets cache and guarded bulk reads | No chunk packet pooling or altered terrain/send budgets |
| Inventory dirty scan | No NOS patch | Official 1.22.7 already checks InventoryBase.IsDirty before scanning dirty slots; mod-specific overrides and whole-inventory iteration require separate evidence |
| Inventory/cooking/recipes/drop lists/propick | Not implemented | No container, grid recipe, RecipeBase, drop-list or prospecting replacement |
| General list/registry optimizations | Targeted modules above only | Entity spawning, block/event-manager lists, registry placeholders/frozen lookup, random-block-tick BlockPos pooling and wildcard paths remain unimplemented |
| Pathfinding | Worker cap correction and optional bounded main-thread assistance | Optional waypoint candidate-node pooling; no general path scheduling by distance or raw-node pooling |
| Network fluidity | Missing-attribute removal resync suppression added; stock transports and entity update cadence | No TCP flush consolidation, distance send-frequency reduction, tracking hysteresis or spawn priority |
| Client features | Server-only module | No client entity-load budgeting or negotiated delta position protocol |

Removing Synergy/Tungsten can remove benefits in the remaining areas. NOS cannot currently promise equal or better aggregate performance. Public percentage claims from other mods are not NOS benchmark evidence.

## Priorities for closing the gaps

1. Locate actual allocation stacks under the server's player workload. With tens of thousands of chunks, inspect terrain/block serialization, path searches and container work as well as Suite archives.
2. Prove path-node ownership and retention before adding a bounded pool. Returned PathNode lists, parent chains, overridden A* methods and mod hooks make indiscriminate reuse unsafe.
3. Profile AI/collision cost before opt-in distance policies. Add per-entity exclusions and preserve combat, breeding, hunger, pets, mounts and scripted behavior. Skipping ticks is a gameplay policy, not an allocation-only equivalent.
4. Measure packet queue latency before batching or reducing send frequency. Keep chunk delivery, teleport/spawn ordering and vanilla-client compatibility intact.
5. Profile world generation and save I/O before deeper changes; preserve save ownership and generated terrain.

## Current memory report

The supplied run has 54,564 chunks, 3,374 entities and 27 players, about 16,092 MB managed heap, 23,568 MB committed and 7,384 MB fragmented. The recent post-Gen2 floor falls by 1,673 MB. This shows substantial fragmentation/allocation pressure, not proof of a growing leak in that interval. The older snapshot's allocation delta implies roughly 75 MB/s over 234 minutes, but does not identify a mod. Match workload and process identity when comparing; no collection or heap compaction is forced by NOS.
