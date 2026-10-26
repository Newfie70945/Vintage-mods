# NOS 0.20.14-preview: barrel recipe rescan skip

## Why

On a 28-player modded server, live 1.22.7 tick logs repeatedly show `BlockEntityBarrel` callbacks as the largest single cost in slow ticks. About 1,000–1,150 barrel callbacks land in one tick and take 45–257 ms together, roughly 100–225 µs each. This happens with `barrelIngredientPairing` and `barrelMatchCountGuard` already active.

The decompiled official `BlockEntityBarrel.OnEvery3Second` explains the cost. Every 3 s, a barrel that holds something but has no current recipe calls `FindMatchingRecipe()`. That walks the entire barrel recipe registry and calls `BarrelRecipe.Matches` for each recipe. Storage barrels never match anything, so they repeat the same full search forever. Mod packs add many barrel recipes, which makes each search longer. Every barrel registers its 3 s listener when its chunk loads, so the listeners fire in bursts.

## What changed

`EngineOptimizations.BarrelRecipeMemo` adds a new opt-in module. It is **off by default**.

- **When it skips.** It skips the recipe search only while both slots hold the same stack objects, collectibles and stack sizes as the barrel's last search. That last search must also have found no recipe and left the contents unchanged.
- **When it searches anyway.** If a search changes the contents (for example an instant recipe crafts), the result is never remembered and the next tick searches natively.
- **Periodic full search.** `BarrelRecipeRescanEvery` (default 10, range 2–100) forces a native search every N checks. Mod `MatchesForCrafting` hooks and in-place attribute changes therefore still take effect. A recipe that becomes valid through that kind of state alone starts within N × 3 s (30 s by default) instead of on the next tick. Any slot change still triggers the game's own immediate search through `SlotModified`.
- **Sealed barrels.** On a skipped tick, the module runs the native remainder exactly: a sealed barrel without a recipe is unsealed and marked dirty.
- **What stays native.** Barrels with a current recipe, empty barrels, custom barrel subclasses, non-standard inventories or slots, and client-side APIs all run the stock method. Recipe matching, sealing, crafting, outputs and recipe order are untouched.
- **Guards.** The module is guarded by the official 1.22.7 IL hashes of `OnEvery3Second` and `FindMatchingRecipe(IPlayer)`. It is blocked if VSSurvivalMod's hash differs, and it conflicts out if another mod patches the tick, the search, the recipe accessor, `Matches` or `PairInput`.
- **Failures.** An exception is propagated once, never retried, and stands the module down until settings are reapplied.

The Suite → Optimizations → "Blocks, chunks and saves" section has two new controls: the switch and the rescan interval. The new counters are `barrelRecipeMemoChecks`, `barrelRecipeMemoSkipped`, `barrelRecipeMemoScans`, `barrelRecipeMemoFallbacks` and `barrelRecipeMemoFailures`.

## Evidence

`tests/BarrelRecipeMemoTests` drives the real `BlockEntityBarrel.OnEvery3Second` and `BarrelRecipe` matching under Harmony. It has 19 checks:

- disabled behaviour matches stock;
- an unchanged barrel searches only on ticks 1 and 11;
- changes to stack size, the stack object, or either slot trigger a search;
- a hook-only state change starts the recipe within the rescan interval;
- barrels with an active recipe, empty barrels and custom subclasses keep the native path;
- skipped ticks unseal exactly like native;
- searches that change the inventory are not remembered;
- a scripted 40-step mixed sequence matches native results exactly;
- a throwing hook propagates once and disables the module.

Synthetic measurement: 1,000 storage barrels, a 300-recipe registry, three trials on the audit container.

| | One tick of all barrels | Allocated per barrel tick |
| --- | --- | --- |
| Stock | 32–68 ms | 67.5 KB |
| `BarrelRecipeMemo` (N = 10) | 3.3–4.0 ms | 6.8 KB |

These are component timings with synthetic recipes and items. They are not a production TPS claim, and real recipe counts and mod hooks will differ.

The full optimizer suite (`tools/build.py`) passes against the official 1.22.7 server binaries, verified against `build.py`'s hashes. Suite compiles with zero warnings, and the Suite optimizer controls browser test passes.

## Limits

- Barrels that hold contents matching a recipe that isn't valid yet still search natively whenever their contents change. Only the unchanged-storage case is skipped.
- Callback bursts are still aligned: this module makes each barrel cheap but does not spread barrels across ticks.
- Mods that change barrel recipe results from world state without touching the slots see their change applied up to N × 3 s late. Lower `BarrelRecipeRescanEvery`, or leave the module off, if that matters on your server.
