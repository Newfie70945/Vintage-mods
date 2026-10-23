# Autosave lag and public-server optimizer testing

Suite 1.2.48 / Optimization UI1, NOS 0.16.0-preview, inspected official Vintage Story 1.22.7.

## What the existing save optimization does

`SaveSnapshotRelease` clears the completed background save's chunk-reference snapshot under the native save lock. A failed save keeps its retry snapshot. This reduces retention; it is not incremental saving and does not guarantee a shorter autosave stall. Native 1.22.7 already moves part of normal saving off-thread. Save preparation, mod save callbacks, player/map serialization and GC can still occupy the main thread, while background work can contend for disk/CPU.

Compare several actual save windows in Performance Center. A cyan save marker indicates overlap, not proof that the save caused the entire pause. Inspect the incident's measured execution, GC pause evidence, save preparation and callbacks. Where needed, make a bounded NOS capture with phase/callback timing enabled in its configuration during a controlled observation window; detailed profiling itself has cost. Do not leave broad detailed profiling running merely to optimize saves.

Check local SSD/NVMe latency, available disk space, CPU quota/throttling, swap/memory pressure and host backup/compression jobs during those same windows. Schedule competing backups apart from gameplay peaks. Keep save transactions and durability intact. Raising the save interval reduces frequency but can enlarge each save and increases potential lost progress after a crash. Do not disable autosaves to hide stalls.

## Recommended testing order

1. Retain your currently working primary allocation modules: player snapshots, AI filter/task-start dispatch, nearest/interactable searches, despawn projection, physics temporary lists, repulsion callback reuse, bulk reads, bounded asset cache, generation request lookup and completed-save snapshot release. These are targeted reductions, not guarantees of lower CPU or risk-free operation.
2. With matching NOS 0.16 installed, try `AnimationCollisionFlagScan` alone. It preserves native frames and triggers. Compare tick p95/p99, save-window durations, allocation rate and post-GC memory at matched player/chunk/entity counts across several saves.
3. Separately try `ContainerTransitionScan`, then `ContainerTickTraversal` if container work is significant. Monitor spoilage, room behavior, inventory interactions and errors as well as timings.
4. Leave normal/cached collision generation and collision traversal off unless workload measurements justify them. Component tests reduce allocation but show mixed/slower collision CPU timings.
5. Keep main-thread pathfinding assistance, distant AI/wander/repulsion scheduling, nearby-player population scaling, waypoint pooling and other experimental switches off for the initial public-server baseline. These have different compatibility/behavior/CPU trade-offs; do not use All On as a preset.

No preset is applied by opening this guide or the Optimizations page. Each supported setting remains independent; inspect Actual state because configured on can still mean standby, conflict or failed. Loading an unsupported runtime does not bypass compatibility guards. Do not stack NOS's engine patches with Synergy/Tungsten; the optimizer blocks conflicting runtime combinations.

## Reversibility and risk

NOS patches loaded methods in memory and does not intentionally rewrite installed server DLLs or the world format. Disable it or remove its ZIP after a clean stop to return to official methods. This does not guarantee that a bug cannot crash a server, affect simulation or lose unsaved progress. Test a copy first and retain a recoverable world/config copy before public testing. Compare one change at a time and roll it back if gameplay or errors regress. No forced GC, transaction changes, autosave frequency changes or incremental-save patch is added in this UI pass.
