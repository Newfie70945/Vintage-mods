# Block ID repair — experimental

**Experimental feature. Use at your own risk. Make backups before remapping; test on a world copy first. The author is not responsible for block data lost through misuse.**

Open **Suite webpanel → Server Tools → Block ID Repair**. Use a panel Owner account; in-game/console commands require `controlserver`. No standalone connector or optimizer is required. This implementation supports the inspected official Vintage Story 1.22.7 world format. A different server format, Stratum runtime, or a patched layer codec is refused.

1. Save the world normally if you want its latest live edits included. **Scan ? Blocks** counts only saved IDs whose current definition is a coded `IsMissing` placeholder (the game’s missing-block representation). Registered blocks, code-less/null registry slots, raw IDs without a placeholder and retained block-entity mismatches are excluded. It reads only existing saved chunks, in short read-only pages. It does not load/generate terrain, execute mod entity classes, or scan inventories. Progress, errors and cancel controls are shown.
2. Select a result or enter the **old numeric block ID**, then enter the **replacement numeric ID or exact block code**. Example: old `57213`, replacement `game:rock-granite`. A numeric destination such as `105` also works. Numbers are examples, not universal IDs: IDs belong to this particular world. Block codes must include the domain and exact variant; no wildcard or item-code lookup is performed. A code resolves to its current numeric ID, and the preview binds both that ID and its canonical code. Use `/bir getcode <id>` to inspect a number or `/bir getid game:rock-granite` to find a known destination's ID.
3. Leave **Create and verify an automatic world backup** enabled (the default), or disable it to show the manual-copy warning. To continue without automatic backup, first stop the server and make a manual world copy, then acknowledge it in the panel. The Suite records this acknowledgement but cannot verify your copy. Changing any repair option invalidates staging until you make a fresh preview. **Preview Replacement** shows the source/destination, scanned occurrence count and affected chunk count. Open **Advanced investigation → Advanced Diagnostic Scan** with a source ID selected to count a registered ID. The ordinary scan rejects a focus ID that is not a missing placeholder. An optional focus limits results to that ID. Replacing a registered block requires the explicit valid-source option. Source ID 0 is always refused. Removing matches with destination ID 0 requires Allow Air.
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
/nseblockrepair scan <missingPlaceholderId>
/nseblockrepair diagnostics
/nseblockrepair diagnostics <oldId>
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

**Finding retained stacks is not a recovery guarantee. Generic ID replacement still cannot bypass block-entity protection. Use the separate, restricted inventory restoration workflow below.** A dedicated migration would need to verify the original class, block code, inventory schema and item mappings before reconnecting data. If a damaged chunk has already lost its serialized contents and was saved over, another surviving copy such as a pre-incident backup is needed. Do not break or replace the damaged containers while investigating.

**Search Blocks** finds registered block codes/IDs; container targets are identified and cannot be selected for generic replacement. Choose an ordinary target to fill the replacement field, which invalidates the old preview. **Download Diagnostic Report** saves the current scan/plan/inspection as JSON on your computer. Keep this file private: it can contain inventory summaries and locations.

## BlockLogger recommendations and historical inventories

Enter the damaged block’s **absolute** coordinates and dimension, optionally choose a history cutoff just before the incident, and click **Recommend from BlockLogger**. The chunk-key input is for saved-data inspection only; recommendations always require an exact location. Console: `/nseblockrepair recommend <absoluteX> <Y> <absoluteZ> [dimension]` (current-time cutoff).

The lookup is restricted to the current world ID, numeric dimension and exact X/Y/Z. It uses the existing positional indexes, at most 200 latest events and 50 snapshots, and refuses an indexless fallback. It never scans the whole log database, guesses from unrelated locations, or uses old numeric IDs as globally stable identities. Legacy rows missing this world/numeric dimension are not inferred. A missing, disabled or pruned history may produce no evidence. A row cap downgrades confidence.

Suggestions show source event/snapshot IDs, timestamps, placement/use/container/removal evidence and current registration status. Confidence is Medium or Low, not a probability or guarantee. A recent recorded placement takes precedence over frequent older activity; a break describes a removed block. Conflicting or incomplete history needs manual review. A per-location suggestion does not justify replacing all occurrences of a numeric ID across the map.

Up to three historical container snapshots include slot IDs, item codes and counts (at most 128 displayed occupied slots each). The stored JSON is bounded at 64 KiB, depth 8, and validated against recorded counts and slot IDs. Invalid/partial snapshots are labelled. Existing BlockLogger snapshots store **codes/counts only**, not complete attributes such as written book text, durability, perish timers or nested bag contents. Full inventory recovery is not guaranteed. Snapshot logging was disabled by default in the inspected configuration; useful snapshots exist only if enabled and retained at the time. The new inspector does not invent missing snapshots retroactively.

Choosing an ordinary suggestion fills the destination and invalidates the previous preview. Container suggestions remain visible but disabled for generic remapping. Review later transfers before any separate recovery plan: copying an old snapshot can duplicate removed items or overwrite legitimate changes. Suggestions do not restore inventories or apply themselves. The separate restoration workflow below requires its own preview and staging. Owner-only diagnostic reports include this evidence; non-Owner status responses omit it.

## Automatic inventory restoration (Suite 1.2.44)

This experimental workflow restores **one absolute location**, on the next restart before terrain loads. It supports retained vanilla bookshelf, shelf and standard chest/container metadata, with up to 64 slots. It never creates missing block-entity metadata or performs a global inventory conversion. Machines, custom/mod-replaced container classes, incompatible serialization and patched initialization methods are refused. The bounded inspector also refuses oversized records (over 2 MiB), individual UTF-8 strings over 4 KiB, excessive nesting and unsupported attribute types; preservation claims apply only to fully verified records.

1. Save normally, clear players from the target area, then inspect the saved location. Ensure there is a retained, fully decodable container record with its original block code, class and standard inventory layout. For a bookshelf, its shape/material and usable slot layout must be verifiable.
2. Choose **Reconnect retained inventory** to repair the host block at that coordinate while preserving its complete block-entity payload byte for byte, including item attributes and written text. All retained item IDs must still exist. This mode requires compatible retained metadata and a host block that needs reconnecting; both empty and occupied inventories are supported. It does not create items or replace inventory data.
3. Alternatively, use **Recommend from BlockLogger**, choose a historical snapshot, then select **Restore snapshot into empty inventory**. The snapshot must match the exact world, dimension, coordinates, original block code and slot count. It must contain valid registered item codes/counts that fit the retained layout and current stack limits. Current inventory contents are never overwritten. Future-dated snapshots, later logged block changes or container take/put events, including ambiguous same-timestamp changes, prevent restoration; an over-limit later history is refused too.
4. Review the risk acknowledgement. Historical snapshots contain codes/quantities only, so restored items receive default attributes. They cannot recreate written book text, durability, freshness or contents of bags. Unlogged item transfers cannot be detected; an owner must still check for duplication risk.
5. Leave the shared automatic-backup checkbox enabled, or make a manual world copy and acknowledge it before disabling backup. **Preview Inventory Restore** shows the exact location, target block, source, item count and fidelity. Changing coordinates, mode, snapshot, backup or acknowledgements invalidates the staging token. A preview expires after 15 minutes.
6. **Stage Inventory Restore for Restart** persists the reviewed plan. Only one inventory/global repair job may be staged. Cancel removes the staged job. On restart the tool rechecks the world and both ID registries, saved block/entity hash, container class/layout, historical snapshot and later logged mutations. Saved data changed by a normal save or another mod causes refusal; inspect and preview again instead of bypassing the check.
7. If enabled, a complete world backup is created and verified. A durable location receipt is reserved before the single chunk write, and the staged job is consumed so it never automatically replays. Snapshot IDs predating a prior restore intent at that location are refused, even when someone chooses a different old snapshot. A newer snapshot after a subsequent incident may be eligible, subject to all checks. Uncertain writes stop the server and require inspection/recovery from the backup. Do not delete receipts merely to force a retry.

Retained mode changes only the solid block at this position. Snapshot mode changes only this position's solid block if necessary and the selected container's serialized `inventory/slots` subtree. Other block-entity metadata, other containers and unrelated chunk fields retain their original bytes. Lighting/heightmap and external map caches may need refreshing if the host block changed. The result and backup path remain under `NewfiesBlockRepair`; inventory jobs use `inventory-pending.json`, `inventory-last.json` and location receipt files.

Commands (all require `controlserver`; automatic backup stays on unless explicitly disabled):

```
/nseblockrepair restorepreview <X> <Y> <Z> <dimension> retained 0 confirm-risk
/nseblockrepair restorepreview <X> <Y> <Z> <dimension> snapshot <snapshotId> confirm-risk
/nseblockrepair status
/nseblockrepair restorestage <restorePreviewId>
/nseblockrepair cancel
```

To disable automatic backup, append `no-backup manual-copy-confirmed` only after making a manual world copy. Owner web accounts alone may preview/stage; Safe Diagnostics Mode blocks restore operations. Read-only inspection and history recommendations remain available to Owners.

## Practical selection workflow (Suite 1.2.44)

You can now work from an in-game target instead of finding its numeric ID yourself. All commands below require `controlserver` and affect saved data on the next restart, after preview and staging. Save the world normally before previewing; if a subsequent save changes the selected chunk, startup refuses the stale plan rather than overwriting it.

```
/nseblockrepair help
/nseblockrepair select
/nseblockrepair results
/nseblockrepair suggest
/nseblockrepair results
/nseblockrepair replace game:rock-granite confirm-risk
/nseblockrepair results
/nseblockrepair selectedstage PREVIEW_ID
```

`select` locks the looked-at position for your administrator account for 15 minutes. Moving your crosshair afterward does not move the selected location. `suggest` reads exact-position BlockLogger history; `results` shows its evidence/confidence and the saved ID, retained metadata and available preview ID. No suggestion applies itself. `replace` accepts a registered numeric destination ID or an exact `domain:code` and previews **one solid cell**, including when its source is a valid registered ID. Other occurrences of that ID are untouched. Fluid positions and generic replacement of retained block entities are refused.

For removed plants, look at the ground beneath the missing plant and use `/nseblockrepair select above`. Alternatively use `/nseblockrepair select at X Y Z [dimension]` with absolute saved coordinates. If the selected position is already air, review its history and use `replace CODE confirm-risk allow-air-source`. Only that position may be restored from air; global replacement of air stays blocked. Without logs, retained metadata or a world backup, air cannot reveal the plant that used to be there. The tool does not invent a map-wide list of deleted plants.

For a retained bookshelf/container, `replace game:bookshelf confirm-risk` routes to the existing retained-inventory workflow when the selected saved metadata identifies that exact original code. Use `results`, then `/nseblockrepair restorestage PREVIEW_ID`. Empty compatible retained shelves can now be reconnected too. A missing or incompatible block entity requires a backup or the supported historical rollback path; choosing a container code alone cannot recreate its shape, material, machine state or lost inventory.

The Owner webpanel offers the same one-location preview/stage path, using the inspection coordinates, and a **Use Latest In-game Selection** button. BlockLogger suggestions have separate buttons for one-location repair and retained-container recovery. The global remap remains a distinct operation that changes every scanned occurrence of an ID; its preview count and container protections still apply.

Advanced diagnostic scans show **retained-container mismatches** even when the host ID is registered or air, and flag saved/current registry code mismatches for review. Up to 256 coordinate examples are retained. This detects surviving evidence rather than assuming all removed content retains an invalid ID. Unsupported or oversized metadata remains unverified and cannot safely be treated as an empty/lost inventory.

Selected-location jobs use `selected-pending.json`, `selected-last.json` and consumed/rejected records in `NewfiesBlockRepair`. They share automatic/manual backup policy and the one-staged-job limit with global and inventory repairs. Saved chunk bytes, world identity and block registry must still match at startup. Preview expires after 15 minutes. Cancel removes staged jobs without editing terrain.


## Placeholder-only and advanced scans

Placeholders1 keeps Suite **1.2.44**. The default web button and `/nseblockrepair scan` now detect only coded `IsMissing` block definitions; a familiar saved code or registered copy elsewhere does not suppress a real placeholder. This is a server-side definition check, not a camera/visibility test. Underground and unloaded saved placeholders still count. Missing textures on otherwise valid blocks and plants already saved as air cannot be detected this way.

Use the separate **Advanced Diagnostic Scan** or `/nseblockrepair diagnostics [oldId]` only to investigate broader ID/code and retained block-entity evidence. The report records `scan.Mode` as `missing-blocks` or `diagnostics`; the panel labels the active scope. Default scans skip retained block-entity inspection entirely. Neither mode edits terrain.

## Reading large scan totals

A finding's count is the number of saved occurrences of that ID across solid, fluid and decor layers. It is **not a verified count of visible question-mark blocks**. In advanced diagnostics, fluids and widespread decor can produce large counts; unfinished scans are partial. A chunk containing several different suspect IDs contributes to several per-ID chunk counts, so those chunk counts must not be summed as unique chunks.

QueryFix1 labels unresolved IDs separately from registered saved/current-code mismatches and deliberately focused registered IDs. It also reports when the saved code resolves to a registered runtime ID elsewhere. That may indicate a legacy/duplicate mapping; it is advisory evidence to verify before any repair. A familiar-looking code name does not itself prove a block is valid or invalid. Retained block-entity mismatches include non-inventory state, such as generic clutter, and are not automatically recoverable containers.

## Reviewed multi-ID remaps (SmartRemap1, Suite 1.2.44)

1. Run **Scan ? Blocks**. Check just the IDs you intend to repair in the results table. Up to 32 distinct source IDs can share one plan; unchecked IDs are excluded. A fresh scan clears the selection.
2. Choose **Recommend for Checked IDs**. The advisor compares the exact saved code with registered blocks, and looks up BlockLogger history at up to two sampled solid locations in different chunks per source. It uses exact world/dimension/coordinate indexes, bounded history rows and a 15-second lookup budget. It never scans the entire BlockLogger database or generates terrain. Missing/pruned/indexless history is reported, not guessed.
3. Read confidence, restrictions and **Show evidence**, then click **Use suggestion for ID ...**, or type your own destination ID or exact `domain:code` in that source’s field. No destination is selected automatically. Same-family variants and cross-domain name matches remain low confidence; conflicting plausible sample histories lower confidence. Even exact-code identity or two agreeing locations cannot prove every saved occurrence has the same original identity.
4. **Preview Checked Remaps** lists each source, resolved target ID/code and occurrence/chunk counts. The shared backup/air/registered-source options apply to the whole plan. Any change to selected sources, destinations or options disables staging until you preview again. Chunk counts per mapping overlap; do not sum them as unique chunks.
5. **Stage Checked Remaps for Restart** submits the entire reviewed mapping list. Startup revalidates world/registry, each target, saved layer compatibility, container metadata and each selected source’s occurrence count before backup or edits. Changed counts cause refusal and require a fresh scan/preview. One verified automatic world backup covers the plan unless you explicitly acknowledge a manual copy.
6. Each original numeric ID is replaced once, simultaneously across solid/fluid/decor references; chain/cycle mappings do not cascade. Only affected chunks are written, with other serialized fields preserved. A crash or write failure can still leave a partial repair across chunks; use the retained backup as instructed by the result. Staged jobs are consumed before writing and never silently replay.

Container/block-entity targets cannot be selected for a generic ID remap, and any selected source hosting retained metadata blocks the entire preflight. Use location-specific retained inventory recovery instead. Recommendations never create inventory data. Default scans remain placeholder-only; advanced diagnostic scans remain available separately. Legacy single-ID commands and pending plans are still supported.
