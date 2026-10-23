# NOS 0.14.0-preview / Suite 1.2.48 NOS Recipes1

This pass expands independently implemented coverage in the entity-query and RecipeBase allocation areas documented by Tungsten, alongside earlier targeted Synergy-inspired AI, repulsion, path and sync modules. It does not establish full parity. Public feature catalogs were rechecked on 2026-10-23; [coverage and remaining gaps](replacement-coverage.md) stay explicit.

## Four independent runtime controls

New controls default off in both new and existing configurations:

- `NearbyEntityListReuse`: reuse GetEntitiesAround's private result accumulation list.
- `CuboidEntityListReuse`: separately reuse GetEntitiesInsideCuboid's private result accumulation list.
- `ShapelessStackMerge`: remove temporary LINQ iteration and captured first-match predicates in the base recipe merge helper.
- `ShapelessWildcardMatch`: remove temporary filtering iterators in the base wildcard-ingredient helper.

Suite → Optimizations → **Entity queries and shapeless recipes** provides live controls, cumulative execution/failure counters and per-target module state/reasons. Missing fields on older NOS remain disabled and unsent. Read-only roles, Suite Safe Diagnostics Mode, NOS Safe Mode/master switch and capture locks remain enforced. Suite version remains 1.2.48, build `ms11-nos-recipes1`.

## Behavior, ownership and fallback

The query transpiler replaces exactly one temporary-list constructor, verifies the expected owned ToArray and leaves the full official method body intact. Chunk order, predicate side effects, bounds, dimensions, despawn/null-tail behavior and result ownership remain native. Recursion/thread scopes clear entity references even on exceptions; at most one idle list per target/thread is retained, capped at 4,096 slots. Oversized buffers are discarded. Configuration epochs prevent stale scope reuse. Client GameMain calls do not borrow server scratch.

Recipe helpers preserve first matching stack, attribute-sensitive comparison, clone ownership, stack counts, wildcard/tag/allowed-variant checks and collective hook order. Stock's crafting hook still runs even when preliminary type/code/quantity/tag tests fail. Virtual overrides stay intact. No stacks, inventories, recipe results or registries are cached or pooled; consumption/output behavior remains native.

Exact official 1.22.7 assembly identities are required. Foreign target patches block the affected optimization and late foreign changes remove only its own target hooks. Observed exceptions disable the affected gate immediately, preserve the error, clear query references and trigger patch removal by the monitor. Work is never replayed after side effects. Errors from a recipe/filter mod do not prove NOS caused them; resolve the cause before reapplying settings.

## Measured component evidence

[Raw repeated fixtures](research/components-0.14.0/recipe-query.json) use actual official helpers/query methods, synthetic item/chunk fixtures and warm alternating stock/enabled runs with tiered compilation disabled. Each measurement repeats 20,000 operations after warm-up.

| Fixture | Stock bytes/call | Enabled bytes/call |
| --- | ---: | ---: |
| Five-slot shapeless merge | 2,184 | 1,736 |
| Single wildcard ingredient | 136 | 0 |
| Nearby query over 16 fixture entities | 664 | 336 |
| Cuboid query over 16 fixture entities | 664 | 336 |

The merge still allocates independent item/attribute clones. Queries still allocate their result arrays. Proxy calls contribute to fixture allocation/timing. CPU timings improved in these fixtures, but are not proof of production tick/TPS, GC-pause or aggregate memory improvement. Benefit depends on actual calls and workload. No forced GC, heap compaction, altered network cadence or gameplay sleep policy is added.

Behavior checks cover attributes/counts/clone ownership, native helper errors, collectible veto/hook order and exceptions, recursive hooks/queries, concurrent query isolation, independently owned arrays, native bounds/dimension/filter order, oversized buffers, mid-query disable, unsupported IL and per-target late conflicts/failures. The paired build also runs earlier NOS and Suite checks. Copied-server validation exercises real item matching, query counters and controls with the Suite/content pack; its final report accompanies the download. Authenticated-player soak testing remains outside this evidence.
