# Block ID repair — experimental

**Experimental feature. Use at your own risk. Make backups before remapping; test on a world copy first. The author is not responsible for block data lost through misuse.**

Open **Suite webpanel → Server Tools → Block ID Repair**. Use a panel Owner account; in-game/console commands require `controlserver`. No standalone connector or optimizer is required. This implementation supports the inspected official Vintage Story 1.22.7 world format. A different server format, Stratum runtime, or a patched layer codec is refused.

1. Save the world normally if you want its latest live edits included. **Scan Saved Map** reads only existing saved chunks, in short read-only pages. It does not load/generate terrain, execute mod entity classes, or scan inventories. Progress, errors and cancel controls are shown.
2. Select a result or enter the **old numeric block ID**, then enter the **replacement numeric ID or exact block code**. Example: old `57213`, replacement `game:rock-granite`. A numeric destination such as `105` also works. Numbers are examples, not universal IDs: IDs belong to this particular world. Block codes must include the domain and exact variant; no wildcard or item-code lookup is performed. A code resolves to its current numeric ID, and the preview binds both that ID and its canonical code. Use `/bir getcode <id>` to inspect a number or `/bir getid game:rock-granite` to find a known destination's ID.
3. Leave **Create and verify an automatic world backup** enabled (the default), or disable it to show the manual-copy warning. To continue without automatic backup, first stop the server and make a manual world copy, then acknowledge it in the panel. The Suite records this acknowledgement but cannot verify your copy. Changing any repair option invalidates staging until you make a fresh preview. **Preview Replacement** shows the source/destination, scanned occurrence count and affected chunk count. Scan with a source ID selected to count a registered ID. Replacing a registered block requires the explicit valid-source option. Source ID 0 is always refused. Removing matches with destination ID 0 requires Allow Air.
4. **Stage for Next Restart** stores the reviewed, world-bound plan. It does not edit terrain now. Cancel removes the staged job. Changes to the world ID or numeric-ID mapping invalidate the plan.
5. On the next restart, during the AssetsFinalize phase before terrain processing begins, Suite rechecks the job and preflights saved data. If automatic backup is enabled, it creates and checks a full world backup; failure prevents edits. If disabled, it records a warning and relies on your acknowledged manual copy. It then rewrites only matching solid/fluid palette values and decor pairs in existing saved chunks. Other protobuf fields (including entity and mod data) retain their original bytes. It uses the official game database writer and normal save format. Progress and completion are logged.
6. Inspect the last-job result after startup, then rescan. A source's missing registry definition may still appear even after its last saved occurrence is gone; a complete scan distinguishes unused definitions from blocks actually on the map.

## Limits and recovery

- Counts describe saved chunk records read during the scan. Concurrent saves can change them. No player inventories, schematics, item stacks, block-entity inventory contents or log/rollback records are remapped.
- Valid destination blocks must not require a block-entity class. A source hosting a block entity causes preflight refusal; the generic tool does not discard chests or mod inventory data. Restore the missing mod or use a dedicated block-entity migration for those cases.
- Fluid destinations must match the affected layer. Air is an explicit removal option. Corrupt data, unsupported legacy compression or incomplete scans prevent staging/applying; convert legacy chunks using the official game first.
- Backups and job records are under `NewfiesBlockRepair` in the server data directory. With automatic backup enabled, reserve enough free disk space for a complete copy of the world database. That backup is never automatically deleted. With it disabled, the backup path is empty and no automatic recovery copy exists. Old staged plans without a backup option still default to creating a backup.
- Startup maintenance deliberately takes time proportional to the saved map. It reads bounded pages instead of activating the whole map. If another mod already started terrain or queued a save, the job is refused.
- This repairs numeric block references. Cached lighting, heightmaps and external webmap exports are preserved and can require relighting/re-exporting afterward.
- A repair is consumed before the first write, so a crash does not silently replay it on the next restart. Completed batches can remain if a later write fails; the result reports this and requests server shutdown. Stop the server and restore the retained automatic backup (or your manual world copy if automatic backup was disabled) before retrying. Restoring the entire backup also restores world progress made after the backup.
- A source-to-destination mapping can merge with existing destination blocks. Reverse remapping is not an undo; use the world backup.

## Commands

```
/nseblockrepair scan
/nseblockrepair scan <oldId>
/nseblockrepair status
/nseblockrepair preview <oldId> <newId-or-domain:code>
/nseblockrepair preview 57213 game:rock-granite
/nseblockrepair stage <previewId>
/nseblockrepair cancel
```

To disable automatic backup from the console, make a manual world copy first, then use `preview <oldId> <destination> no-backup manual-copy-confirmed`. Omitting the acknowledgement is refused by the server. Options can appear in any order.

For explicit air removal: `preview <oldId> 0 allow-air`. For a registered source: `preview <oldId> <newId> - allow-valid`. Full structured results are safely printed to console; chat gets a short summary and preview token. Stage the exact preview token you reviewed.

## Read-only bookshelf / inventory investigation

Use **Inspect saved block / inventory data** before attempting to repair a broken container. Enter absolute saved block X/Y/Z and dimension (not spawn-relative map coordinates), or paste the exact signed chunk key from a scan sample. A chunk-key inspection lists retained block entities throughout that chunk. The command equivalents are:

```
/nseblockrepair inspect <absoluteX> <Y> <absoluteZ> [dimension]
/nseblockrepair inspectchunk <savedChunkKey>
/nseblockrepair search bookshelf
```

Inspection runs in the background and shares the scan/cancel worker; wait for one operation to finish before starting another. It reads one saved chunk, closes its database reader, and never activates terrain, executes mod block-entity/attribute classes, resolves item stacks, or writes world data. No saved chunk means nothing is generated. Owner access or `controlserver` is required. Owner inspections remain available in Safe Diagnostics Mode, while staging/edit controls remain locked. Non-Owner status responses omit retained inventory summaries.

The report includes original serialized class/code/position, current solid/fluid IDs and code labels, any class/code mismatch, inventory tree paths, positive-quantity serialized stacks, and SHA-256 hashes of the saved chunk and entity payloads. Current code labels do not convert historical item-ID mappings. Up to 64 stacks per entity are listed; totals include serialized nested stacks, not a verified count of recoverable items. Stack attributes and opaque mod data are not exported. Custom byte storage may contain items the inspector cannot interpret.

Bounds: 64 MiB per chunk, at most 256 block-entity records / 8 MiB of entity payloads per inspection, 2 MiB per decoded entity, depth 20, 16,384 attribute nodes, 4,096 bytes per string and bounded arrays. Partial/unsupported/oversized records and limits are explicitly reported. If a mod overrides built-in attribute types, inspection is disabled. An absent inventory or stack in a partial report is not evidence that it was deleted.

**Finding retained stacks is not a recovery guarantee. This pass does not restore bookshelf inventories or bypass generic block-entity protection.** A dedicated migration would need to verify the original class, block code, inventory schema and item mappings before reconnecting data. If a damaged chunk has already lost its serialized contents and was saved over, another surviving copy such as a pre-incident backup is needed. Do not break or replace the damaged containers while investigating.

**Search Blocks** finds registered block codes/IDs; container targets are identified and cannot be selected for generic replacement. Choose an ordinary target to fill the replacement field, which invalidates the old preview. **Download Diagnostic Report** saves the current scan/plan/inspection as JSON on your computer. Keep this file private: it can contain inventory summaries and locations.

## BlockLogger recommendations and historical inventories

Enter the damaged block’s **absolute** coordinates and dimension, optionally choose a history cutoff just before the incident, and click **Recommend from BlockLogger**. The chunk-key input is for saved-data inspection only; recommendations always require an exact location. Console: `/nseblockrepair recommend <absoluteX> <Y> <absoluteZ> [dimension]` (current-time cutoff).

The lookup is restricted to the current world ID, numeric dimension and exact X/Y/Z. It uses the existing positional indexes, at most 200 latest events and 50 snapshots, and refuses an indexless fallback. It never scans the whole log database, guesses from unrelated locations, or uses old numeric IDs as globally stable identities. Legacy rows missing this world/numeric dimension are not inferred. A missing, disabled or pruned history may produce no evidence. A row cap downgrades confidence.

Suggestions show source event/snapshot IDs, timestamps, placement/use/container/removal evidence and current registration status. Confidence is Medium or Low, not a probability or guarantee. A recent recorded placement takes precedence over frequent older activity; a break describes a removed block. Conflicting or incomplete history needs manual review. A per-location suggestion does not justify replacing all occurrences of a numeric ID across the map.

Up to three historical container snapshots include slot IDs, item codes and counts (at most 128 displayed occupied slots each). The stored JSON is bounded at 64 KiB, depth 8, and validated against recorded counts and slot IDs. Invalid/partial snapshots are labelled. Existing BlockLogger snapshots store **codes/counts only**, not complete attributes such as written book text, durability, perish timers or nested bag contents. Full inventory recovery is not guaranteed. Snapshot logging was disabled by default in the inspected configuration; useful snapshots exist only if enabled and retained at the time. The new inspector does not invent missing snapshots retroactively.

Choosing an ordinary suggestion fills the destination and invalidates the previous preview. Container suggestions remain visible but disabled for generic remapping. Review later transfers before any separate recovery plan: copying an old snapshot can duplicate removed items or overwrite legitimate changes. This pass does not restore inventories or apply suggestions automatically. Owner-only diagnostic reports include this evidence; non-Owner status responses omit it.
