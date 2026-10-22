# MigrationPosFix1 — Suite 1.2.45

The screenshot reports `Field not found: Vintagestory.API.Common.Entities.Entity.Pos`. The shared migration targeting method still referenced that field directly, despite the Suite already having a runtime-compatible position resolver. A runtime can resolve field tokens when preparing the method, so the reference could break a looked-at-block command even when the fallback branch would not execute.

The fix removes that field dependency. Looked-at targets use their selected block position. Explicit coordinate/dimension targeting uses the supplied dimension without reading the player position. Only coordinate targeting without a selected block or explicit dimension needs the existing compatibility resolver. If position resolution fails, the command refuses the target instead of silently using dimension zero.

Suite remains 1.2.45, build `ms11-migrationposfix1`; TickUI1, StorageSave1 and Performance1 remain included. No inventory evidence, migration rules, backup settings or compatibility checks are relaxed. Companion versions are unchanged.

Validation: official 1.22.7/.NET 10 release build, shipping-method fixtures for selected and explicit targets, invalid dimensions and unavailable fallback, and emitted-IL validation that the targeting method no longer carries a direct Entity.Pos field token. This verifies the diagnosed dependency and target behavior; it does not claim that the user's differing runtime was reproduced or that inventories missing from saved data can be recovered.

Inventory acknowledgement errors now identify the exact command flags and give a complete recover-empty example. The explicit risk/overwrite acknowledgements remain required. Added checks verify the required flag appears in both reconnection and recovery errors.
