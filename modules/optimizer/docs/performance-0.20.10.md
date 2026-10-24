# NOS 0.20.10: allowed-variant recipe matching

Adds an allocation-free path for native trailing-star wildcard recipes with an allowed-variant list, inside the existing opt-in `ShapelessWildcardMatch` module. It compares the variant using a string span and preserves native wildcard/domain checks, quantity/tag checks, collectible hooks and caller-owned stack handling. No item, inventory or recipe result is cached. Full crafting batch sizes and XP hooks are unchanged.

Patterns outside this exact case, absent variant lists, null AssetLocation arguments and mod AssetLocation subclasses continue through the official matcher. The engine now checks foreign Harmony ownership of both the wildcard matcher and its variant helper; a conflict retains native behavior. Existing per-module switch, failure diagnostics and exception handling remain.

Suite 1.2.51 already exposes the switch as **Reduce shapeless wildcard-check allocations** on Optimizations. It remains off by default. Enable it, save/apply and confirm `shapelessWildcardMatch` reports active. No Suite implementation change or new Suite version was needed in this pass.

## Validation

All 31 automated validation suites passed against the inspected official Vintage Story 1.22.7 build, with none skipped. The recipe/query suite passes 10,068 checks, including 10,000 native-vs-new pattern/variant comparisons and unchanged hook order and stack-list results for allowed variants. The block suite passes 109 checks; engine lifecycle/conflict coverage passes 138, including the newly guarded dependencies.

Four alternating comparisons against the actual compiled NOS 0.20.9 prefix use the same native item stack and crafting hook, with 500,000 measured queries per arm after warmup. Median elapsed time was **57.70 → 43.36 ns/match**, about 25% lower, and allocation **32 → 0 bytes/match**. Tiered compilation was disabled for this isolated comparison. These are matching-helper measurements, not a production TPS result.

A separate disposable official server loaded Suite 1.2.51, NOS 0.20.10 and a lab fixture. With the module active, four alternating 100,000-query windows compared native helper execution and the optimized helper using the server's real stick item and a scratch stack. Stack size 64 and a fixture attribute remained intact. Save completion, clean exit and SQLite integrity all passed, with zero logged errors. No authenticated players, live inventories or production worlds were used.

[Previous-release comparison](research/recipe-0.20.10/comparison.json) · [Native-server checks](research/recipe-0.20.10/server.json).

## Limits

This improves a specific kind of crafting match. It does not establish that it fixes the production mass-crafting/XSkills stall, long-term memory retention, autosave lag or overall server tick latency. The server fixture exercises matching helpers; it does not simulate player crafting or XP awards. No new heavily populated-server comparison is claimed for this change.

The barrel alternatives evaluated in this pass did not outperform the current implementation and were discarded. Barrel code and defaults are unchanged.
