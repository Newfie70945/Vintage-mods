# Test the crafting improvement with normal batches

NOS 0.20.8 preview adds **CraftingCandidateDedup**. Suite 1.2.50, build `ms11-crafting-search1`, exposes it under **Optimizations → Newfies Server Optimizer → Crafting recipe search**.

1. On the comparison server/world copy, replace the previous Suite and NOS ZIPs with this pair; avoid duplicate versions. Use the same recipes, mod versions, player skills and configuration for before/after runs.
2. Leave other optimization settings as they were. Enable **Faster duplicate checks in crafting recipe searches**, then **Save and Apply NOS Settings**. Stop an active NOS capture before applying.
3. Refresh actual module states. **craftingCandidateDedup** must say **active**. A disabled, conflict or unsupported state means it is not replacing the search. Do not override a foreign-patch guard.
4. Repeat the same four full stacks with the same ore grade, hammer, inventory room and shift-click action. Repeat a few times to distinguish cold startup from steady behavior. Record total processing time, native slow-tick duration, output quantities and XP before/after. Export diagnostics after the comparison.
5. Compare the same workload with only this switch disabled. The switch is opt-in and defaults off. There is no packet budget, batch-size cap, asynchronous crafting, XP aggregation or forced collection in this change.

If the long delay remains with the module active, capture **Tick Debugging → Capture Incident + Profile** before repeating the craft. This patch does not measure or fix every xSkills/compatibility callback. A profile must overlap the operation to identify the remaining cost.

## How it works

The original search repeatedly performs a linear duplicate check against an increasingly large recipe list. The replacement uses reference membership for large lists of official GridRecipe objects and reuses bounded, cleared scratch. Recipe order, shaped/shapeless priority, ingredient checks, matching callbacks and output generation remain native. Searches with custom recipe equality use native membership. No recipe decisions or item stacks are cached across searches. Nested calls have separate scratch, and oversized buffers are discarded after use.

## Handbook Optimizer distinction

The official Handbook Optimizer 0.19.1 archive has a Universal manifest, but its ModSystem returns false for the server side and only overrides StartClientSide. In a dedicated server process that public binary does not apply its client crafting patches. A copied ZIP in Mods is not evidence of active patches. A locally modified build can differ. On a singleplayer combined client/server process, client Harmony patches can affect shared methods; NOS refuses foreign ownership of its target/helper methods.
