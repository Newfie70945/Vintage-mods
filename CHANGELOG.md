# Current release changes

## Server Essentials Suite 1.2.44

- RestartWarnings1: fix saved restart warning arrays merging with initialized defaults on load. Custom lists such as `[1,5,10]` and explicitly empty arrays now survive reloads. The default list still applies when the setting is absent.
- Treat saved restart schedule arrays as replacements too, preventing deleted entries from surviving JSON population. Existing clamping, UTC normalization and warning scheduling remain in place.
- Suite remains **1.2.44**, build `ms11-restartwarnings1`; companion versions are unchanged. Existing files previously rewritten with defaults need their intended warning list saved once after updating.


- MigrationReview1: add read-only `/nseblockmigrate previewheld` using the same held-target/rule validation without changing terrain, rules or backups.
- Add `/nseblockmigrate repairheld confirm-risk manual-copy-confirmed` for one plain missing block, without creating an ID-wide rule or automatic backup. Protect healthy blocks, fluids, retained metadata, containers and chiseled hosts; verify serialized live-chunk metadata before changing the cell. Held items are not consumed.
- Improve `inspect` with saved identity, matching rule, migration state, live block-entity presence and per-material rules. Distinguish surviving metadata from historical inventory recovery evidence.
- Show up to 32 recent loaded-cell refusal locations, source/target IDs, specific reasons and repeated attempt counts in webpanel diagnostics. Samples are bounded and contain no chunk/entity references; they are not a complete audit or undo log.
- Suite remains **1.2.44**, build `ms11-migrationreview1`; companion module versions are unchanged.


- DiscordEvents1: added a separate Temporal storms announcement route to the Discord webpanel controls, with enable toggle, channel routing and editable format. Suite stays 1.2.44, build `ms11-discordevents1`.

## Server Essentials Discord 1.0.14

- Relay the actual game-generated death announcement, including randomized hunger, lightning and creature flavour text, once per death. Use a cause-aware fallback if native death announcements are disabled or the observer cannot be installed.
- Upgrade only the exact former stock death format to `{message}`; preserve custom templates. Add `{cause}`, `{source}` and `{killer}` placeholders.
- Add independently enabled temporal-storm alerts for approaching, imminent, started, waning and ended phases, with per-event channel/format settings and `{phase}`/`{strength}` placeholders. Startup establishes a baseline without replaying stale phases.
- Bound and escape event text, prevent repeated identical storm-phase alerts and detach native observers/timers on shutdown. No Discord credentials or external delivery were used for validation.

## Earlier Suite 1.2.44 changes

- MigrationContainers1: add explicit `replaceheld reconnect confirm-inventory-risk` and a matching webpanel mode for compatible retained containers. Reconnect changes their block identity while preserving current contents/attributes, including empty inventories; it requires no BlockLogger snapshot or world-wide preparation scan. Missing/incompatible metadata stays protected.
- Improve bookshelf refusals with actionable reconnect and historical-recovery commands. Historical recovery remains a separate opt-in operation with evidence and replay protection.
- Repair numeric missing materials inside vanilla chiseled blocks without replacing the host or voxel geometry. `inspect` lists material IDs; `source=ID` resolves multiple missing materials; `at=X,Y,Z dimension=N` targets loaded blocks that cannot be selected normally. Rules apply to both loaded and saved material palettes while protecting healthy materials.
- Preserve unrelated chiseled bytes, geometry, name and rotation; refuse air/fluid/container material targets and unverifiable custom metadata. Show material progress/refusal diagnostics. Direct replacement searches now work for missing IDs without a whole-world scan.

- MigrationLive1: `/nseblockmigrate replaceheld no-backup manual-copy-confirmed` saves and activates an ID-wide rule without creating an automatic world backup. Missing acknowledgement is refused; existing rules and the held stack are preserved.
- Fixed migration refusing densely built chunks at the old 256 block-entity verification cap. The verifier supports up to one retained entity per chunk cell with bounded bytes; unrelated payloads remain byte-for-byte unchanged, and metadata at replacement cells stays protected.
- Added a one-time bounded pass over already-loaded solid terrain after migration activates. Only configured missing IDs change; safe cells can migrate without unloading the base. Inventory recovery and unsupported layers still use the saved-chunk path. Disable/clear/reconfigure cancels the pass.
- Show loaded-terrain progress, skipped cells and reasons separately from skipped saved chunks. Chunk refusals include a retained-block location when available. Land claims do not block Block ID Migration.

- RollbackLogging1: single-block previews report actual numeric ID/code, missing-definition status, event/transfer/snapshot counts and actionable recovery limits.
- Replace typed rollback execution tokens with `/rollback apply`, which confirms the latest preview belonging to the administrator.
- Added explicit `/rollback block TIME --from-live` for existing containers without old snapshots: reverse identity-matching take/put rows from captured current contents, with fixed preview binding, live-change checks and durable receipts. Unlogged transfers and original item attributes cannot be verified.
- Added explicit `--repair-missing` for one genuinely missing block. Uses existing block-change history or one unambiguous recorded identity; validates the installed target and binds the missing ID. Compatible retained block entities use ExchangeBlock to preserve their data; healthy blocks, conflicting identities and incompatible metadata are refused.
- Exact-cell history now returns retained snapshot evidence even when an unlogged remap created no events in the selected window. Existing world/dimension isolation, indexed seeks, deadlines and row caps remain.
- Added live BlockLogger recording/recovery controls: block/container/entity toggles, snapshots and slot-change snapshots, debounce/session limits, queue/flush settings, aggregation/search defaults and ignored prefixes. Shows effective recording/module/storage status plus current-process written/queued/dropped event and snapshot counters. Recovery preset requires an explicit save; failures preserve edits.
- Suite remains **1.2.44**, build `ms11-rollbacklogging1`. NOS and companion versions are unchanged.

- MigrationUI2: rebuilt Block ID Migration around a compact rule editor with the replacement, inventory handling, backup choice and **Save & enable rule** together. Saved rules remain visible; optional scans and diagnostics are collapsed.
- Invalid or incomplete saves now show a specific inline message instead of returning silently. Successful saves and server errors are visible beside the action; Enter submits the rule, failed saves retain the draft, and replacement search is available directly in the editor.
- New webpanel rules do not opt into automatic backup. They require manual-copy acknowledgement unless automatic backup is explicitly selected. Saved backup and inventory settings restore on refresh and edit; in-game replaceheld still requests automatic backup.
- Added pause/resume and retry for saved rules, preserving their stored backup/inventory policy. Added compact activation/session counters and explanations for blocked batch actions. Scanning is available while migration is enabled; reviewing a replacement batch still requires pausing.
- Suite remains **1.2.44**, build `ms11-migrationui2`. NOS and companion versions are unchanged.

- ModpackStability1: automatic 3-second engine profiles now default OFF and have a separate Profiler-page opt-in. Lightweight diagnostics and manual profiles remain available. Disabling automatic captures stops an active automatic burst without stopping a manual capture; obsolete timers cannot terminate newer captures.
- Profiler attribution now normalizes each marker once and reuses normalized catalog tokens, eliminating repeated normalization work while retaining matching and ambiguity rules.
- Fixed automatic updates for PascalCase mod manifests, including Floral Zones. Added an explicit option for releases tagged for earlier stable game patches of the same major/minor version, useful for some Forgotten Armory listings. Exact compatibility remains the default. Identity/version/dependency/archive safety, backups and restart staging remain in place; updates cannot downgrade installed versions.
- Added the separate NOS 0.6.0 AI task-start switch to Suite Optimizations.
- Suite stays **1.2.44**, build `ms11-modpackstability1`; companion versions are unchanged. NOS advances to **0.6.0-preview**.

- PacketStability1: a short packet-budget burst is now advisory. Punishment-grade flood evidence requires sustained excess on one budget and reliable server timing. Existing packet, settings, movement and block-action limits still drop excess traffic immediately.
- Right-click reach, uncertain mining and unverified teleport observations cannot add risk, corroborate incidents or match automatic finding-code punishments. Break-reach punishment requires repeated distinct targets over time with healthy timing; the same target's use/break callbacks no longer count as different targets.
- Reach checks now consider oversized server-defined selection boxes when cube/history reconciliation fails. Client hit coordinates cannot expand reach. Ordinary in-range actions skip the extra geometry work, and unknown/invalid geometry is not treated as proof.
- TPS awareness now notices a current stall before the next tick callback. Reach reporting is coalesced, recent-target bookkeeping is bounded, and timing-sensitive observations during lag remain review-only.
- Server-behavior findings reuse validated client evidence instead of reevaluating the full mod/assembly policy twice per report. Family-risk calculations reuse temporary dictionaries. Full evaluation still runs on client scans and policy changes.
- PacketStability1 behavior and validation are retained.

- Migration rules now enable automatically without a server restart. World backups run in cancellable background page batches, then verified rules switch on for future chunk loads. Inventory rules enable automatically after preparation and backup finish.
- Updated migration controls and status polling for live activation. Changing/disabling rules cancels obsolete activation, and repeated updates reuse a single deserialization hook. Already loaded terrain uses new rules when it reloads.

- `/nseblockmigrate replaceheld` saves an ID-wide rule immediately, even during a running world scan. Optional inventory preparation runs separately; ordinary rules need no world scan.
- Added a visible **Migration Rules** table with each source ID, replacement, inventory mode and active/pending state, plus `/nseblockmigrate rules [page]` in-game. Save, edit, remove or clear explicit rules without scanning.
- Added opt-in **Recover Empty** and **Replace Contents** migration modes using compatible retained container metadata and exact-position BlockLogger snapshots. Existing contents are preserved by default; replacing contents requires a separate acknowledgement.
- Recovery prepares bounded disk-backed evidence before activation. Chunk loads recheck retained data and durable recovery receipts to prevent stale overwrites and repeated restoration. Compatible containers can migrate while unprepared neighbors are skipped.
- Historical snapshots restore logged item codes and quantities only. Written-book text and other missing attributes cannot be recovered; retained inventory bytes preserve surviving attributes.

- Replaced Block ID Repair with **Block ID Migration**: choose missing numeric IDs and a replacement ID/code for each, review once, then migrate existing saved chunks as they load. Removed the old whole-world repair controls and staging routes; old pending jobs are retired without editing the world.
- Added persistent enable/disable controls, automatic live activation, verified automatic backup (or explicit manual-copy acknowledgement), current-session progress and rate-limited skipped-chunk diagnostics. Normal world saves persist migrated chunks; no terrain is generated for migration.
- Missing-block scans now include code-less remapper holes and undefined saved IDs. Healthy registered sources are protected even if an old client submits the legacy override flag.
- Added exact-code preparation for installed original identities, with excluded IDs listed for review. Compatible retained container/block-entity bytes are preserved; missing originals, incompatible metadata and deleted inventories are never guessed or invented.
- Added `/nseblockmigrate replaceheld`: stage an ID-wide migration from the missing block you look at to the registered solid block held in your hotbar. Retains other enabled rules, backs up and activates automatically, and migrates all matching missing IDs as their chunks load. Protects healthy sources, fluid positions and incompatible container data; does not consume held blocks.
- Improved per-ID replacement searches, explicit evidence/confidence suggestions, findings filtering/paging, review invalidation, owner/safe-mode controls and diagnostic downloads. Suite remains **1.2.44**, build `ms11-livemigration1`.

- QueryFix1: fixed recovery reads that walked unrelated world events/snapshots even for a single-block rollback. Queries now drive existing position-index seeks from the affected-cell set, retaining prior inventory baselines, all actors and later conflict evidence. The existing 30-second cancellation/deadline and row limits remain in place; no new database index or migration is required.
- Split player/area query branches so unused scope branches do not force broad reads, and use the existing indexed player-name lookup. Single-block work is covered by large-history and native SQLite regression tests.
- Clarified repair diagnostics: occurrence counts are not confirmed visible question-mark blocks. Findings distinguish unresolved IDs, registered code mismatches and deliberately selected registered IDs, and show when the same saved code has a registered runtime ID elsewhere. Retained block entities are not all inventory containers. Running scans and overlapping per-ID chunk counts are identified.

- Corrected the Suite version to 1.2.44 across mod metadata, assembly/project version, manifest, build identity and current guides. The previous 1.2.49–1.2.52 labels were development build numbering; this build keeps their fixes and uses the requested public version.
- Added `/rollback help` and bare `/rollback` help, available even while recovery is running or its receipt index is initializing. Covers block/container, player, area, duration/UTC syntax, claims, execution, cancellation and inventory limitations.
- Added `/rollback block 2h` to preview the looked-at block and its logged container inventory; explicit absolute coordinates and dimension can target missing blocks. Includes slot changes, fixed target/time, preview confirmations and durable recovery receipts. Neighboring cells are excluded.
- Added in-game Block ID Repair selection: `select`, `select above` for a removed plant, or `select at X Y Z [dimension]`; `results`, `suggest`, `replace CODE confirm-risk`, and `selectedstage ID` provide a reviewed one-location repair workflow. Container destinations route to compatible retained-inventory recovery.
- Added matching Owner webpanel controls to import an in-game selection, inspect retained mismatches, choose suggested destinations, preview one solid position and stage it for restart. Coordinate/target/backup edits invalidate staging. Unrelated occurrences of the source ID are excluded.
- Scans now expose orphaned block-entity class/code mismatches separately from missing IDs, including retained bookshelves whose host became a registered block or air. Also flags saved/current registry code mismatches for review. Up to 256 container examples are shown; incomplete records are counted.
- Removed plants already saved as air cannot be identified from an ID scan alone. The selected-location workflow supports explicit restoration from air, with a separate acknowledgement, exact-coordinate history suggestions and backup protection. Global air remapping remains blocked.
- Retained container restoration now supports empty compatible bookshelves/shelves as well as surviving inventories, preserving the saved payload. Snapshot restoration fills only an empty compatible retained inventory using logged codes/counts; missing historical item attributes cannot be reconstructed.
- Retained and selected repairs revalidate world, registry and saved data on restart, verify automatic world backups by default and consume staged jobs before writing. Disabling backup requires manual-copy acknowledgement. Only one selected/global/inventory repair may be staged.
- Fixed rollback chat markup and oversized replies, including raw help placeholders and diagnostic values. Failed private sends cannot leave recovery stuck. History recommendations recognize the legacy overworld dimension alias.
- Block ID Repair remains experimental and restricted to the inspected official Vintage Story 1.22.7 saved format. Unsupported metadata and destructive generic container replacements are refused. NOS and companion mod versions are unchanged.
