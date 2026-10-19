# Block ID Migration — Suite 1.2.44

The old Block ID Repair page and whole-world repair staging have been replaced by **Block ID Migration**. This tool changes configured missing block IDs as existing saved chunks load. It never generates or visits terrain for you.

**Experimental: use at your own risk. Test on a world copy and make backups. The author is not responsible for block data lost through misuse.** This implementation supports the inspected official Vintage Story 1.22.7 build. Stratum, changed codec formats and conflicting chunk deserialization patches are refused.

## Webpanel

1. Open **Server Tools → Block ID Migration** with a panel Owner account.
2. Save the world, then **Scan Missing Blocks**. Leave source ID blank for all unresolved IDs, or enter one numeric ID. Scans read saved terrain without activating it.
3. Select the missing IDs to migrate. For each, enter a replacement numeric block ID or exact `domain:block-code`. Up to 32 manually chosen rules can share one configuration.
4. Search installed replacements or ask for suggestions. Suggestions show evidence/confidence and do not choose a destination for you. Removed originals may have no suitable installed replacement.
5. **Review Selected Mappings**, then **Enable Migration on Next Restart**. Alternatively, **Prepare All Exact-Code Matches** collects up to 4,096 exact installed identities and lists excluded IDs. Review before enabling.
6. Restart once. The migrator verifies its world/registry, creates and checks the backup, then starts before terrain loads. Configured IDs are replaced when saved chunks load. Normal saves persist successful repairs; no separate whole-world sweep runs.

The page shows configured rules, active/staged/failed state, session chunks/reference counts, refused chunks and the last refusal. **Disable Migration** stops further replacements immediately and persists that setting. Previously saved repairs are not undone. To change rules, disable, scan and review a new configuration, then restart.

Only missing definitions are eligible: coded `IsMissing` placeholders, code-less slots left by remapping and undefined numeric IDs. **Healthy registered source IDs are always protected**, even if an old client submits an allow-valid override. Scan totals include underground/unloaded references; they are not proof that every occurrence is a visible question-mark cube. Missing client textures, ordinary registered blocks and plants already saved as air are outside this recovery.

## In-game command

Requires `controlserver`. Commands and help are encoded for Vintage Story chat so error text cannot break subsequent chat formatting.

- `/nseblockmigrate help`
- `/nseblockmigrate scan [missing-ID]`
- `/nseblockmigrate preview OLDID game:rock-granite` — review one rule after its scan completes.
- `/nseblockmigrate automigrate` — review exact installed-code matches from the completed scan.
- `/nseblockmigrate enable PREVIEWID` — save the reviewed configuration; backup and activation occur on restart.
- `/nseblockmigrate disable` — stop future migrations immediately.
- `/nseblockmigrate status`
- `/nseblockmigrate cancel` — cancel scanning, discard the preview and disable a migration awaiting restart.

**Create an ID-wide rule from your hand:** hold the registered replacement block in your active hotbar slot, look at a missing block, and run `/nseblockmigrate replaceheld`. It stages a rule for **all missing blocks with that numeric source ID**, not just the looked-at position. Other enabled migration rules are retained; running migration pauses until restart activates the new configuration. A fresh verified automatic backup is created on restart. No scan is required for this command, and no held blocks are consumed. Use status to review rules, or disable to cancel. Healthy sources, invalid held items, fluid positions and incompatible container targets are refused. Actual chunk metadata is checked as chunks load; unknown/deleted inventory data is never invented.

The old `/nseblockrepair` command points to the replacement. Old pending repair files are retained with `.retired` suffix and are not executed. Existing rollback commands remain separate.

## Containers and removed mods

For exact saved-code identity reconnection, the migrator verifies the retained block-entity class and saved code and requires compatible metadata at every affected container occurrence in that chunk. Its saved entity bytes, including existing inventory contents/attributes, are preserved byte for byte before the official loader handles them.

It does not invent a bookshelf's shape/material, convert arbitrary mod inventories, resurrect deleted data, or change IDs inside player/container item stacks. A source retaining incompatible data skips the entire affected chunk. You may need the original mod restored or a verified world backup. An unavailable original Ape asset is not automatically replaced with a similar-looking vanilla block. Choose replacements explicitly for non-container assets.

The uploaded More Lanterns 1.4.4 contains `assets/game/config/remaps.json`; 1.4.6 removes that file, while the uploaded block/item asset definitions are unchanged. That establishes a change to the supplied global remap file, but does not by itself prove which commands changed the IDs in a particular world. If its saved original codes were overwritten, history or a pre-update backup is needed to recover the original identities.

## Backups, persistence and safeguards

Automatic backup defaults on. Disabling it requires making and acknowledging a manual world copy. Backup and risk options are bound to the reviewed preview; changing them requires reviewing again. Backups and settings live under the server data folder's `NewfiesBlockRepair` directory. Keep disk space for a full world copy.

A fresh configuration gets its own verified pre-activation backup, retained across restarts. A changed registry or healthy source causes activation to fail; a new scan/review is required. Unsupported bytes, incompatible layers and metadata cause the affected chunk to be skipped without replacement. Refusal logging is rate-limited. Already loaded chunks need unloading/reloading; the migrator does not walk active terrain. A migration snapshot reports counts for the current process session, not lifetime progress through the whole map.

Disabling cannot undo writes already saved. Restore a verified full world copy if you need to reverse a migration. Cached lighting/heightmaps and external map exports may need refreshing after changing block identities.
