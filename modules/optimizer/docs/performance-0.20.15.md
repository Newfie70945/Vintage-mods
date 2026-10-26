# NOS 0.20.15-preview: barrel recipe rescan skip

## Why

On a 28-player modded server, live 1.22.7 tick logs repeatedly show `BlockEntityBarrel` callbacks as the largest single cost in slow ticks. About 1,000–1,150 barrel callbacks land in one tick and take 45–257 ms together, roughly 100–225 µs each. This happens with `barrelIngredientPairing` and `barrelMatchCountGuard` already active.

The decompiled official `BlockEntityBarrel.OnEvery3Second` explains the cost. Every 3 s, a barrel that holds something but has no current recipe calls `FindMatchingRecipe()`. That walks the entire barrel recipe registry and calls `BarrelRecipe.Matches` for each recipe. Storage barrels never match anything, so they repeat the same full search forever. Mod packs add many barrel recipes, which makes each search longer. Every barrel registers its 3 s listener when its chunk loads, so the listeners fire in bursts.

## What changed

`EngineOptimizations.BarrelRecipeMemo` adds a new opt-in module. It is **off by default**.

- **When it skips.** It skips the recipe search only while both slots hold the same stack objects, collectibles and stack sizes as the barrel's last search. That last search must also have found no recipe and left the contents unchanged.
- **When it searches anyway.** If a search changes the contents (for example an instant recipe crafts), the result is never remembered and the next tick searches natively.
- **Periodic full search.** `BarrelRecipeRescanEvery` (default 10, range 2–100) forces a native search every N checks. Mod `MatchesForCrafting` hooks and in-place attribute changes therefore still take effect. A recipe that becomes valid through that kind of state alone starts within N × 3 s (30 s by default) instead of on the next tick. Any slot change still triggers the game's own immediate search through `SlotModified`.
- **Spread rescans.** Barrels register their 3 s listener with no random offset, so barrels loaded together tick together. Each barrel's first memo therefore starts at a phase derived from its position. Forced rescans then spread across the interval, instead of every barrel rescanning on the same tick every 30 s.
- **Sealed barrels.** On a skipped tick, the module runs the native remainder exactly: a sealed barrel without a recipe is unsealed and marked dirty.
- **What stays native.** Barrels with a current recipe, empty barrels, custom barrel subclasses, non-standard inventories or slots, and client-side APIs all run the stock method. Recipe matching, sealing, crafting, outputs and recipe order are untouched.
- **Guards.** The module is guarded by the official 1.22.7 IL hashes of `OnEvery3Second` and `FindMatchingRecipe(IPlayer)`. It is blocked if VSSurvivalMod's hash differs, and it conflicts out if another mod patches the tick, the search, the recipe accessor, `Matches` or `PairInput`.
- **Failures.** An exception is propagated once, never retried, and stands the module down until settings are reapplied.

The Suite → Optimizations → "Blocks, chunks and saves" section has two new controls: the switch and the rescan interval. The new counters are `barrelRecipeMemoChecks`, `barrelRecipeMemoSkipped`, `barrelRecipeMemoScans`, `barrelRecipeMemoFallbacks` and `barrelRecipeMemoFailures`.

## Evidence

`tests/BarrelRecipeMemoTests` drives the real `BlockEntityBarrel.OnEvery3Second` and `BarrelRecipe` matching under Harmony. It now has 24 checks, including follow-up failure tests:

- disabled behaviour matches stock;
- an unchanged barrel searches once, then only on its phased rescan schedule;
- barrels loaded together spread their forced rescans across rounds;
- changes to stack size, the stack object, or either slot trigger a search;
- a hook-only state change starts the recipe within the rescan interval;
- barrels with an active recipe, empty barrels and custom subclasses keep the native path;
- skipped ticks unseal exactly like native;
- searches that change the inventory are not remembered;
- a scripted 40-step mixed sequence matches native results exactly;
- a throwing hook propagates once and disables the module; a failure in the skipped unseal/dirty remainder also stands the module down, even before native-search state exists.

Synthetic measurement: 1,000 storage barrels, a 300-recipe registry, three trials on the audit container.

| | Average tick of all barrels | Worst tick of all barrels | Allocated per barrel tick |
| --- | --- | --- | --- |
| Stock | 31–34 ms | 34–70 ms | 67.5 KB |
| `BarrelRecipeMemo` (N = 10) | 3.1–3.7 ms | 4.2–6.0 ms | 6.8 KB |

A spread check confirms that no 3 s round rescans more than 250 of the 1,000 barrels. With phases forced to zero, that check fails, because every barrel rescans on the same round.

These are component timings with synthetic recipes and items. They are not a production TPS claim, and real recipe counts and mod hooks will differ.

The full optimizer suite (`tools/build.py`) passes against the official 1.22.7 server binaries, verified against `build.py`'s hashes. Suite compiles with zero warnings, and the Suite optimizer controls browser test passes.

## Limits

- Barrels that hold contents matching a recipe that isn't valid yet still search natively whenever their contents change. Only the unchanged-storage case is skipped.
- Callback bursts are still aligned: this module makes each barrel cheap but does not spread barrels across ticks.
- Mods that change barrel recipe results from world state without touching the slots see their change applied up to N × 3 s late. Lower `BarrelRecipeRescanEvery`, or leave the module off, if that matters on your server.

## Follow-up: slow block-update diagnostics

Optional `BlockUpdateDiagnostics` logs slow native 100 ms update passes at most once per 10 seconds. Default off, threshold 50 ms (10–1000). Counts cover queued neighbour notifications, resends, modified/no-relight/minimal/decor blocks and dirty block entities. Native neighbour notifications retain their 500-entry cap; several other queues drain natively without a cap.

Changed-block handling and block-entity sync have separate observational hooks. Missing/conflicting child hooks report unavailable; residual time is unmeasured. Inclusive elapsed time includes pauses and waits, so this does not prove a mod or GC caused a stall. It is diagnostics, not a simulation budget or performance optimization.

Observer failures increment `blockUpdateDiagnosticFailures` and stand down the observer while preserving native execution and exception identity. Tests inject queue-read and logging failures, check direct sync timing, disabled children, rate limiting and unchanged queue contents.
