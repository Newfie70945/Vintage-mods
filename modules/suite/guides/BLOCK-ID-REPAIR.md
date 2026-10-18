# Numeric block ID repair

Open **Suite webpanel → Server Tools → Block ID Repair**. Use a panel Owner account; in-game/console commands require `controlserver`. No standalone connector or optimizer is required. This implementation supports the inspected official Vintage Story 1.22.7 world format. A different server format, Stratum runtime, or a patched layer codec is refused.

1. Save the world normally if you want its latest live edits included. **Scan Saved Map** reads only existing saved chunks, in short read-only pages. It does not load/generate terrain, execute mod entity classes, or scan inventories. Progress, errors and cancel controls are shown.
2. Select a result or enter the **old numeric block ID**, then enter the **replacement numeric block ID**. Example: old `57213`, replacement `105`. These numbers are examples, not universal IDs: IDs belong to this particular world. Codes appear only as explanatory labels. Use `/bir getcode <id>` to inspect a number or `/bir getid game:rock-granite` to find a known destination's ID.
3. **Preview Replacement** shows the source/destination, scanned occurrence count and affected chunk count. Scan with a source ID selected to count a registered ID. Replacing a registered block requires the explicit valid-source option. Source ID 0 is always refused. Removing matches with destination ID 0 requires Allow Air.
4. **Stage for Next Restart** stores the reviewed, world-bound plan. It does not edit terrain now. Cancel removes the staged job. Changes to the world ID or numeric-ID mapping invalidate the plan.
5. On the next restart, during the AssetsFinalize phase before terrain processing begins, Suite rechecks the job and preflights saved data. It creates and checks a full world backup, then rewrites only matching solid/fluid palette values and decor pairs in existing saved chunks. Other protobuf fields (including entity and mod data) retain their original bytes. It uses the official game database writer and normal save format. Progress and completion are logged.
6. Inspect the last-job result after startup, then rescan. A source's missing registry definition may still appear even after its last saved occurrence is gone; a complete scan distinguishes unused definitions from blocks actually on the map.

## Limits and recovery

- Counts describe saved chunk records read during the scan. Concurrent saves can change them. No player inventories, schematics, item stacks, block-entity inventory contents or log/rollback records are remapped.
- Valid destination blocks must not require a block-entity class. A source hosting a block entity causes preflight refusal; the generic tool does not discard chests or mod inventory data. Restore the missing mod or use a dedicated block-entity migration for those cases.
- Fluid destinations must match the affected layer. Air is an explicit removal option. Corrupt data, unsupported legacy compression or incomplete scans prevent staging/applying; convert legacy chunks using the official game first.
- Backups and job records are under `NewfiesBlockRepair` in the server data directory. Reserve enough free disk space for a complete copy of the world database. The backup is never automatically deleted.
- Startup maintenance deliberately takes time proportional to the saved map. It reads bounded pages instead of activating the whole map. If another mod already started terrain or queued a save, the job is refused.
- This repairs numeric block references. Cached lighting, heightmaps and external webmap exports are preserved and can require relighting/re-exporting afterward.
- A repair is consumed before the first write, so a crash does not silently replay it on the next restart. Completed batches can remain if a later write fails; the result reports this and requests server shutdown. Stop the server and restore the retained backup before retrying. Restoring the entire backup also restores world progress made after the backup.
- A source-to-destination mapping can merge with existing destination blocks. Reverse remapping is not an undo; use the world backup.

## Commands

```
/nseblockrepair scan
/nseblockrepair scan <oldId>
/nseblockrepair status
/nseblockrepair preview <oldId> <newId>
/nseblockrepair stage <previewId>
/nseblockrepair cancel
```

For explicit air removal: `preview <oldId> 0 allow-air`. For a registered source: `preview <oldId> <newId> - allow-valid`. Full structured results are safely printed to console; chat gets a short summary and preview token. Stage the exact preview token you reviewed.
