# Optional dropped-item clumping — NOS 0.20.17-preview

Enable **Clump compatible dropped items in the same block cell** under Suite → Optimizations → Newfies Server Optimizer, then Save and Apply. It is off by default. Standalone configuration: `EngineOptimizations.DroppedItemClumping=true`, followed by a restart. Older NOS versions do not expose this switch.

This is a conservative first implementation, not a radius vacuum or an entity-deletion limit. Settled dropped items may combine when their block cell, dimension, collectible, owner, persistent attributes and temporary attributes match. The native AutoMerge slot path decides transfer quantities and stack limits. Only a source emptied by merging is marked removed; partial remainders remain. The older drop survives at its original position without resetting its age. Newer merged contents inherit that older drop's remaining lifetime and can therefore despawn sooner than they would separately.

Drops younger than two seconds, moving/floating/burning drops, nonstackable/full stacks, custom EntityItem implementations, collectible merge overrides and custom entity behaviors and all collectible behaviors are excluded. Native collectible classes supplied by data-only mods can qualify; custom compiled implementations fall back. This deliberately favors compatibility over merging every possible drop.

Work is limited to 128 loaded-entity entries and 16 successful merges per 100 ms callback, with a 1 ms soft budget and at most 2,048 indexed cell/type/owner keys. A native merge cannot be interrupted mid-operation. The module keeps a dictionary enumerator rather than cloning the loaded entity collection. It stores entity IDs, never item inventories or terrain, and clears its cursor/index on stop. It does not request chunks or scan saved terrain.

The actual module row is `droppedItemClumping`. It checks the core engine guard, Safe Mode and known foreign patches on native merge methods. The core compatibility monitor remains installed for a clumping-only configuration. Late incompatible runtimes and worker failures stop further merging. A failed native operation is never replayed or rolled back blindly.

Counters report loaded-entity entries scanned, successful merges, item quantities transferred, emptied entities removed, indexed cells and failures. Scanned entries include entities that are not eligible drops. Counters are evidence of work, not a TPS estimate.

Tests exercise native slot transfers and removal reasons, stack caps, exact attributes, owner/cell/dimension separation, pickup grace, motion/position exclusions, preserved survivor age, default-off behavior, conflicts, failure stand-down and listener disposal. A 3,000-drop fixture verifies total quantity conservation and consolidation near native stack limits. It is not an authenticated-client or full-modpack performance test. Test a recoverable world copy and verify dropped valuables and modded items before enabling on a public server.
