# Block ID Migration — Suite 1.2.44

The old Block ID Repair page and whole-world repair staging have been replaced by **Block ID Migration**. This tool changes configured missing block IDs as existing saved chunks load. It never generates or visits terrain for you.

**Experimental: use at your own risk. Test on a world copy and make backups. The author is not responsible for block data lost through misuse.** This implementation supports the inspected official Vintage Story 1.22.7 build. Stratum, changed codec formats and conflicting chunk deserialization patches are refused.

## Webpanel

For known IDs, use **Save Rule Without Scan** in the Migration Rules table, or use the held-block command below. Both enable automatically after backup/preparation. The scan workflow below is optional.

1. Open **Server Tools → Block ID Migration** with a panel Owner account.
2. Save the world, then **Scan Missing Blocks**. Leave source ID blank for all unresolved IDs, or enter one numeric ID. Scans read saved terrain without activating it.
3. Select the missing IDs to migrate. For each, enter a replacement numeric block ID or exact `domain:block-code`. Up to 32 manually chosen rules can share one configuration.
4. Search installed replacements or ask for suggestions. Suggestions show evidence/confidence and do not choose a destination for you. Removed originals may have no suitable installed replacement.
5. **Review Selected Mappings**, then **Enable Migration Now**. Alternatively, **Prepare All Exact-Code Matches** collects up to 4,096 exact installed identities and lists excluded IDs. Review before enabling.
6. The migrator verifies its world/registry, creates and checks the backup in the background, then enables automatically without restarting. Configured IDs are replaced when saved chunks load. Normal saves persist successful repairs; no separate whole-world sweep runs.

The page shows configured rules, preparing/backing-up/active/failed state, session chunks/reference counts, refused chunks and the last refusal. **Disable Migration** stops further replacements immediately and persists that setting. Previously saved repairs are not undone. The **Migration Rules** table lists each numeric source, target and active/pending state. Use **Save Rule Without Scan**, Edit, Remove or Clear All Rules to manage explicit IDs without waiting for scanning. Saving changes pauses migration until its background backup/preparation completes; it does not edit terrain immediately.

Only missing definitions are eligible: coded `IsMissing` placeholders, code-less slots left by remapping and undefined numeric IDs. **Healthy registered source IDs are always protected**, even if an old client submits an allow-valid override. Scan totals include underground/unloaded references; they are not proof that every occurrence is a visible question-mark cube. Missing client textures, ordinary registered blocks and plants already saved as air are outside this recovery.

## In-game command

Requires `controlserver`. Commands and help are encoded for Vintage Story chat so error text cannot break subsequent chat formatting.

- `/nseblockmigrate help`
- `/nseblockmigrate scan [missing-ID]`
- `/nseblockmigrate preview OLDID game:rock-granite` — review one rule after its scan completes.
- `/nseblockmigrate automigrate` — review exact installed-code matches from the completed scan.
- `/nseblockmigrate enable PREVIEWID` — save the reviewed configuration; backup and activation run automatically without a restart.
- `/nseblockmigrate disable` — stop future migrations immediately.
- `/nseblockmigrate status`
- `/nseblockmigrate rules [page]` — list saved source IDs and replacements in chat.
- `/nseblockmigrate cancel` — cancel scanning, discard the preview and disable a migration awaiting activation.

**Create an ID-wide rule from your hand:** hold the registered replacement block in your active hotbar slot, look at a missing block, and run `/nseblockmigrate replaceheld`. It saves an ID-wide rule for **all missing blocks with that numeric source ID**, not just the looked-at position. Other configured migration rules are retained. Changes enable automatically after the background backup and optional inventory preparation finish. No restart or scan is required for this command, including while another scan is running, and no held blocks are consumed. Use rules/status to review mappings and activation, or disable to stop. Healthy sources, invalid held items, fluid positions and incompatible container targets are refused. Actual chunk metadata is checked as chunks load; unknown/deleted inventory data is never invented.

The old `/nseblockrepair` command points to the replacement. Old pending repair files are retained with `.retired` suffix and are not executed. Existing rollback commands remain separate.

## Containers and removed mods

For exact saved-code identity reconnection, the migrator verifies the retained block-entity class and saved code and requires compatible metadata at every affected container occurrence in that chunk. Its saved entity bytes, including existing inventory contents/attributes, are preserved byte for byte before the official loader handles them.

Choose an inventory mode before reviewing a migration, or add command flags:

- **Preserve** (default): preserve compatible surviving container bytes; never recreate lost items.
- **Recover Empty**: preserve occupied containers and fill empty compatible retained inventories from the latest exact-position BlockLogger snapshot at/before the chosen cutoff.
- **Replace Contents**: explicitly replace retained contents with that snapshot. Requires an additional overwrite acknowledgement.

Examples: `/nseblockmigrate replaceheld recover-empty confirm-inventory-risk` or `/nseblockmigrate replaceheld replace confirm-inventory-risk confirm-overwrite before=2026-10-01T00:00:00Z`. Without flags the held command retains the configured inventory mode.

Rules save immediately. Recovery evidence is prepared separately in the background and queues behind an existing scan; backup and activation start automatically after preparation reports complete; no restart is needed. The page shows preparation totals/skipped examples and runtime recovery counters. Preparation reads existing saved data without activating terrain. Supported vanilla containers need surviving, compatible block-entity metadata. Cross-code conversion is restricted to verified vanilla bookshelf layouts; arbitrary mod containers and missing shape/material are refused. In recovery modes, an unprepared/changed container stays untouched while compatible neighbors can migrate.

**BlockLogger snapshots contain item codes and quantities, not written-book text, durability, perish state or custom item attributes.** Reconstructed stacks use defaults. Surviving inventory attributes are preserved byte for byte. Later logged block changes/item transfers invalidate stale snapshots, but unlogged transfers cannot be detected. Durable receipts prevent restoring the same old inventory repeatedly across plans/restarts; an uncertain crash after receipt reservation also blocks replay. Do not delete these receipts to retry blindly.

This does not recreate deleted block-entity metadata, alter player/item IDs, or restore already healthy remapped shelves globally. Restore original mods or a verified backup where compatible evidence is unavailable.

The uploaded More Lanterns 1.4.4 contains `assets/game/config/remaps.json`; 1.4.6 removes that file, while the uploaded block/item asset definitions are unchanged. That establishes a change to the supplied global remap file, but does not by itself prove which commands changed the IDs in a particular world. If its saved original codes were overwritten, history or a pre-update backup is needed to recover the original identities.

## Backups, persistence and safeguards

Automatic backup defaults on. Disabling it requires making and acknowledging a manual world copy. Backup and risk options are bound to the reviewed preview; changing them requires reviewing again. Backups and settings live under the server data folder's `NewfiesBlockRepair` directory. Keep disk space for a full world copy.

A fresh configuration gets its own verified pre-activation backup, retained across restarts. Online SQLite backups use dedicated background connections and small page batches; the server thread does not copy the whole world. The backup covers saved world data, not unsaved changes in loaded chunks. Activation waits for the backup and integrity check. Cancelling/replacing rules cancels obsolete activation; a failed backup leaves migration inactive. A one-hour backup limit and a 30-second continuous database-busy limit prevent indefinite attempts. A changed registry or healthy source causes activation to fail; correct the settings and review or resave the rules. Unsupported bytes, incompatible layers and metadata cause the affected chunk to be skipped without replacement. Refusal logging is rate-limited. Already loaded chunks need unloading/reloading; the migrator does not walk active terrain. A migration snapshot reports counts for the current process session, not lifetime progress through the whole map.

Disabling cannot undo writes already saved. Restore a verified full world copy if you need to reverse a migration. Cached lighting/heightmaps and external map exports may need refreshing after changing block identities.
