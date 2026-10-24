# NOS 0.20.8 preview — full-batch server crafting search

This release addresses real work inside crafting rather than reducing batch sizes. **Full shift-click batches remain native.** There is no batch cap, packet deferral, XP aggregation, asynchronous inventory change or forced GC in this change.

## Implemented change

The official `InventoryCraftingGrid.FindMatchingRecipe` collects candidate recipes from ingredient-index entries. Each addition uses a linear list membership check to avoid duplicates. Large overlapping candidate lists can therefore cause quadratic duplicate-checking work, repeated for each ingredient consumption in a bulk craft.

The opt-in `EngineOptimizations.CraftingCandidateDedup` module keeps the official search body, ingredient enumeration and candidate insertion order. For large lists of exact official GridRecipe objects it uses a temporary reference-membership set to replace repeated linear membership scans. Small lists retain native membership; encountering custom recipe equality switches membership back to native for the rest of that search. The temporary candidate list is also reused.

All ingredient checks, matching events, shaped-before-shapeless priority, recipe selection, output generation and crafting callbacks still execute in their original order. No recipe decision, item stack or matching result is cached across searches. Scratch is thread-local and isolated for nested searches, cleared on success/failure, and bounded to four idle contexts with list/set capacities at most 4,096. Larger searches are completed in full and their oversized buffers discarded afterward. Configuration epochs prevent stale scratch from returning to the pool.

The exact official 1.22.7 method body and expected allocation/helper-call layout are checked before installation. Independent enable/disable, master/Safe Mode, conflict monitoring and failure stand-down use the existing NOS lifecycle. Foreign ownership of the replaced target/bypassed helpers blocks installation; callbacks are never replayed after an exception. Compatibility guards are not an absolute guarantee for arbitrary mod interactions.

## Controlled four-stack result

Two separate-process comparisons used normal .NET JIT settings and five alternating native/patched rounds each. Each timed workload used the actual official search, ItemSlotCraftingOutput.TryPutInto/CraftMany, recipe consumption/output methods and crafting event dispatch: four full 64-item input stacks and a deliberately large synthetic index containing 3,000 recipes referenced through eight ingredient groups. Each mode warmed one stack before timing. No server run or other CPU-intensive test was running concurrently.

| Median for four full stacks | First final run | Independent repeat |
|---|---:|---:|
| Native execution time | 8,239.90 ms | 8,384.92 ms |
| Optimized execution time | 113.26 ms | 109.43 ms |
| Native allocated bytes | 36,103,472 | 36,097,960 |
| Optimized allocated bytes | 19,250,400 | 19,250,400 |

This fixture measured approximately **98.6–98.7% less elapsed crafting time** and **46.7% fewer allocated bytes**. Times are Stopwatch elapsed measurements, not server TPS or process CPU utilization. Output was 1,024 units from 256 consumed inputs in every measured arm; native consumption, output-generation and crafted-event counts matched.

These are **synthetic recipe/API fixtures, not the owner's 222-mod server, xSkills 1.1.8, or an authenticated client session**. The large overlapping index deliberately stresses the identified algorithm: nonmatching decoy recipes have no resolved ingredients, so they fail early and the measurement emphasizes candidate collection/deduplication. Real resolved recipes and mod matching/XP hooks can add substantial work that this fixture does not reproduce. The result demonstrates the improvement in that algorithm; it does not establish the same speedup on a different recipe index or prove that every part of the owner's reported 20-second stall is fixed. The previous observation of similar slow crafting makes this a concrete test candidate, not a production guarantee.

Additional correctness tests exercise yields of four, six and eight items per craft, including partial transfers across destination stack boundaries; exact input/output totals and original callback event payloads match native. Tests also cover absent/disabled recipes, live attribute changes, priority, custom equality, recursive/custom-inventory searches, parallel ownership, errors without replay, configuration changes and oversized buffers. The final suite additionally checks recursion beyond the idle-pool bound. There is no authenticated client prediction/synchronization test in this pass.

## Suite control and installation

Suite remains **1.2.50**, build **ms11-crafting-search1**, with a new control under Optimizations → Newfies Server Optimizer → Crafting recipe search. The new switch defaults off and preserves saved settings. Enable **Faster duplicate checks in crafting recipe searches**, Save and Apply NOS Settings, then verify **craftingCandidateDedup: active**. Old NOS builds do not advertise this setting and the corresponding UI control stays disabled. [Exact before/after test guide](crafting-test-guide.md).

## Handbook Optimizer finding

The public **Handbook Optimizer 0.19.1** archive was downloaded from its official Mod DB listing and checked directly by reflection: ShouldLoad(Server) is false, ShouldLoad(Client) is true, and StartServerSide is inherited unchanged from ModSystem. Although its manifest says Universal, it installs its crafting patches in StartClientSide only. A dedicated server having that unmodified ZIP in Mods does not receive those patches. The diagnostic bundle lists the same version and no Handbook Optimizer Harmony owner. This does not cover a locally modified DLL or a singleplayer shared client/server process. NOS's new server patch refuses foreign ownership rather than stacking with it.

[Raw first run](research/crafting-0.20.8/final-default.json) · [Raw repeat](research/crafting-0.20.8/final-repeat.json) · [Handbook side check and official ZIP hash](research/crafting-0.20.8/handbook-side-check.json).
