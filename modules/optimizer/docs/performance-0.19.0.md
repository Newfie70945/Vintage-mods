# NOS 0.19.0-preview — barrel input-count guard

The supplied native logs repeatedly report barrel callback totals of 60–210 ms. These are callback wall times, not evidence that recipe pairing alone caused them. This pass adds one narrow, independently controlled optimization to that path; it is not a replacement for every Synergy/Tungsten feature.

`EngineOptimizations.BarrelMatchCountGuard` defaults false. In Suite 1.2.49 ms11-barrel-count1, enable Reject impossible barrel input counts under Optimizations, Save and Apply, then inspect actual activation and counters. Older NOS builds leave the new control disabled.

The verified native two-argument BarrelRecipe.Matches first calls PairInput, which allocates an empty list even when occupied slot count differs from ingredient count. The new guard returns false and zero output size for that case without allocating a list. It only accepts exact native BarrelRecipe and standard ItemSlot/ItemSlotLiquidOnly slots (maximum two). Other cases retain native matching. Potential matches still execute native pairing, collectible hooks and output calculation. The player-specific Matches overload still runs its recipe event before invoking the guarded overload. No recipe registry, result, inventory or match decision is cached. Fermentation, cooking, quantities, item attributes, XP events and save policies are unchanged.

Dependencies include Matches, PairInput and the Empty getter. Foreign patches, unsupported runtime/contracts and failures retain stock behavior; exceptions are not retried. The switch and calls/fallback/failure counters are independent of barrelIngredientPairing. Existing defaults are unchanged.

The native mismatch fixture measures approximately 32 bytes/call with stock Matches versus zero steady-state bytes with the guard. CPU timings vary by run; this cannot predict production TPS, full barrel callback speed or resolution of multi-second stalls. Tests exercise actual Harmony targets, mismatch/output equivalence, matching-count fallback and custom recipe fallback.

Inspection of official 1.22.7 packet constants confirms net-read-7 is ActivateInventorySlot. It can include inventory/crafting work and hooks; its identity does not by itself establish which mod is expensive. The separate 37-second tick remains unexplained. No claimed fix for ore-crushing XP or creative-mode switching is included.

Suite retains the previous anti-cheat callback batching, cache-write throttling, duplicate-kick guard and stage timings. Those changes are separate from this new NOS allocation module. Raw diagnostic uploads and decompiled game/mod source are excluded from releases.

Validation: all 28 optimizer test suites pass, including 96 block callback checks and 118 module lifecycle/conflict checks. Browser checks cover new control save/drafts, unsupported older NOS, permissions/locks and mobile layout. Suite builds with zero warnings/errors. Copied official server with 25 mod ZIPs passes 91 integration checks, including independent activation/disable of the new guard. Synthetic fixtures and copied terrain do not represent authenticated player workloads. [Component evidence](research/barrel-count-0.19.0/components.json), [sanitized integration](research/barrel-count-0.19.0/copied-server.json).
