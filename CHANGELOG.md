# Current release changes

## Server Essentials Suite 1.2.48

- NOS Events1: add opt-in delayed-callback snapshot, spawn-search list and chunk-read array reuse controls/counters for NOS 0.13.0. Group controls and add local module name/reason/state filters. Reduce repeated compatibility metadata allocation while preserving conflict detection; build `ms11-nos-events1`.

- NOS Chunks1: add independent controls and counters for NOS 0.12.0 block-update batching, chunk-request staging/coordinates and chunk-unload scratch reuse; build `ms11-nos-chunks1`. Preserve original simulation, queue limits, delivery and save policies. New switches default off.

- NOS Broadcast1: add controls/counters for NOS 0.11.0 empty-exclusion broadcast fast paths and bounded entity cleanup/despawn list reuse; build `ms11-nos-broadcast1`.
- Stream diagnostic ZIP JSON entries instead of creating large intermediate JSON strings. Include NOS version/settings/actual states/counters and the retained native tick-debug snapshot in diagnostic bundles, without starting profiling.
- Keep insufficient execution evidence above weak queue/save correlations; reassess retained incidents once. Accept valid overlapping slow ticks up to five minutes while preserving bounded result counts and invalid-duration rejection.

- NOS Distance1: add independent controls, entity filters, protected radius/intervals and work counters for NOS 0.10.0 distant AI/wander/stationary repulsion policies, waypoint candidate pooling and missing-attribute resync suppression. Preserve drafts and older-NOS compatibility; build `ms11-nos-distance1`.

- MemoryPerf2: stream large Performance/Operations JSON archives into atomic replacement files, reducing intermediate allocation and preserving the previous archive on write failure.
- Send compact incident list rows during overview refreshes and marker-only timeline events; fetch full retained evidence when opening a spike. Preserve filters/comparison and prevent queue waits ranking as workload in the quick-read card.
- Background maintenance skips unused profiler-table copies; compact memory snapshots reuse the latest observation. Reuse unchanged diagnostic summaries briefly.
- Add whole-process allocation rate to same-run memory snapshot comparisons, with restart/legacy/counter-reset guards. Explain current NOS coverage and missing Synergy/Tungsten features in Optimizations.
- Build `ms11-memory-perf2`; retains the timeline, save-marker and animal-generation changes below. The invisible-chunk report was identified by the owner as Better Crates and Wool; no chunk delivery policy is changed here.

- Fix timeline traces disappearing in longer ranges: distinguish raw sampling gaps from graph downsampling, show isolated observations, redraw on panel reveal/resize and ignore outdated range responses.
- Add cyan world-save/autosave markers and a top legend; shade genuine missing-observation intervals without inventing data through stalls.
- Use compact timeline-only snapshots, bounded/coalesced window caches and shared serialized responses. Opening Performance Center reuses the last manual Harmony inspection instead of enumerating patches.
- Prevent busy/error page snapshots from triggering a fallback request burst. Clarify elapsed/queued web timings versus actual callback intervals; clip incident work evidence to overlap and conservatively discount GC.

- AnimalGeneration1: per-population-rule generation protection: inherit global settings, ignore generation protection, or protect generation N and above. Overrides also replace generic farm/bred protection for that rule; pet/name/exclusion/player-distance safeguards remain.
- Add generation settings to the web rule editor and `/animallimit generationrule`, with persisted settings and rule summaries. Existing rules inherit previous behavior.
- Add Create/Edit rule actions to each population-scan entity type, search/filter, height-limited scrolling totals and collapsible scrolling regional detail. Scan actions populate the editor for review before saving.
- Suite advances to 1.2.48, build `ms11-timeline-web1`; optimizer stays 0.9.0 preview.

## Server Essentials Suite 1.2.47

- OptimizerNetwork2: add NOS 0.9.0 despawn packet allocation switch, actual status and packet/entry counters. Preserve Physics1 and Entity1 controls; build `ms11-optimizer-network2`.

- OptimizerPhysics1: add independent NOS 0.8.0 physics packet list reuse controls and pass/buffer counters. Build `ms11-optimizer-physics1`; preserves all Entity1 features.
- Explain when external optimizer controls require an uninstalled mod or an unsupported command; unavailable controls stay disabled.

- OptimizerEntity1: add NOS 0.7.0 entity-search and path-queue switches, observed-head wait/count/soft-budget settings, actual module states and work counters to Optimizations.
- Add Release Stopped Capture with active-capture/read-only/safe-mode/older-NOS guards. Exported reports remain; no forced collection occurs.
- Group AI allocation controls together and explain scheduling and GC limits. Suite advances to 1.2.47; build `ms11-optimizer-entity1`, retaining WebRequest2 and all earlier fixes.

## Server Essentials Suite 1.2.46

- WebRequest2: the global header now reads a compact cached endpoint without copying health history or running Smart Diagnosis. The health score shows when it was last calculated.
- Reuse serialized JSON for unchanged Dashboard, header and Server Status snapshots. Preparation stays on HTTP workers; byte retention is capped at 256 KiB per snapshot and larger bodies bypass it.
- Spike details and copied summaries show overlapping web routes, status, response bytes, JSON preparation time/cache reuse, worker queue, main-thread wait and measured callback intervals with coincident GC pauses.
- Match completed requests by interval and enrich recent incidents after requests finish. Queue waits and older aggregate-only timings remain correlation; overlapping callbacks are discounted for coincident GC. Existing archived assessments are refreshed from available evidence.
- Previous build: `ms11-web-request2`; retains WebRequest1 and earlier 1.2.46 changes. Production improvements require measurement under the same player workload.


- WebRequest1: stagger background navigation checks after login instead of requesting nine pages concurrently; overlapping badge refreshes share one run, and logout stops remaining checks.
- Navigation reads a lightweight Tick Debugging status flag instead of building its full profiler/log snapshot.
- Cache and coalesce Server Status reads for 15 seconds, serving the last successful snapshot while refreshing. Different requested history windows keep separate bounded cache entries.
- Previous build: `ms11-web-request1`; retains previous 1.2.46 fixes. These changes reduce request pressure; production stall improvements still require measurement.


- NetworkEvidence1: identify slow custom-packet dispatcher intervals by channel, message type/ID, actual handler method and owning assembly. Show wall time, coincident GC pause, payload byte count and exception type in clicked spikes and copied summaries.
- Resolve the engine deserialization wrapper to its registered mod delegate, avoiding attribution by ambiguous handlecustom1/handlecustom4 markers alone. Rank wall time outside coincident GC conservatively; it is not CPU time or proof of a mod defect.
- Collect only while existing Performance/Profiler diagnostics are enabled. Retain intervals >=25ms, bounded to 256 records / 120 seconds, 512 cached identities and eight records per incident. No packet contents or player identities are stored; no additional sampling timer or forced GC is added.
- Preserve packet behavior and original handler exceptions. Unavailable hooks fail open and report status; disabling diagnostics clears live records and identity references. Existing archives preserve attached evidence.
- Previous build: `ms11-network-evidence1`; retains all prior 1.2.46 changes, companion versions unchanged.

- MigrationPause1: pausing now cancels migration-owned inventory preparation as well as backup activation, stops the loaded-terrain sweep, detaches the chunk hook's owner and releases runtime codecs, pools, maps and reconnection catalogs. Paused chunk hooks return before world access, decoding or the migration lock.
- Preserve saved rules, completed recovery manifests, migration receipts and previously changed blocks. Independent administrator scans are not cancelled by pausing migration. Resume waits safely for a cancelling worker; stale continuations cannot restart paused rules.
- Add cancellation checks to preparation page reads, integrity verification and manifest hashing. Status distinguishes a background operation still stopping from fully idle migration.
- Timeline gaps longer than 2.5 seconds are shown as gaps, rather than diagonal interpolation across a stall; player counts use steps. The bottom timeline band is heap.
- MigrationPause1 foundation retained, including all earlier 1.2.46 changes; companion versions unchanged.

- EvidenceQuality2: distinguish measured contributors, multiple measured contributors, correlation-only assessments and insufficient evidence in clicked spikes.
- Label each ranked explanation by evidence type. Queue growth, event drops, entity/chunk counts, CPU and save overlap no longer rank as direct measurements of the blocking code.
- Scale captured and web main-thread work against stall duration, so a small measured segment does not dominate a much larger unexplained stall. Suppress weak owner candidates.
- Show explicit evidence gaps and repeated marker counts/maxima across retained ticks. Give specific guidance for measured GC, web work and multiple contributors; copied summaries include assessment/gaps.
- Reassess archived verdicts once from retained data. EvidenceQuality2 foundation retained, including all prior 1.2.46 improvements; companion versions unchanged.

- Filter Performance Center spikes by severity and whether matching tick evidence was captured. Show evidence coverage and the three most frequent retained assessments; patterns do not prove a common cause.
- Navigate directly to older/newer retained spikes from the detail drawer. Prevent late responses from overwriting a newer selection or a closed drawer.
- Include capture state, measured GC pause and structured contributor timings/call counts in copied spike summaries.
- Correct release identity to 1.2.46 because 1.2.45 is public. PerformanceCenter1 foundation retained; companion versions unchanged.

- SpikeDetails2: clicking a spike now shows a **What explains this spike?** summary with supporting evidence, scheduler delay, GC pause timing and world-save overlap.
- Retain up to six structured contributors for each captured slow tick, including full marker paths, milliseconds, call counts and owner candidates. Show leaf-marker coverage and uninstrumented time separately; overlapping measurements are not added into a causal percentage.
- Prevent tiny captured markers from automatically outranking substantial measured pauses. Reassess existing archived rankings once while preserving retained evidence.
- SpikeDetails2 foundation retained, including IncidentEvidence1 and earlier 1.2.45 changes. Old captures cannot acquire missing contributor details retroactively; exact attribution still needs an overlapping engine capture.

- IncidentEvidence1: Performance Center now compares measured GC pause time with each stall; collection counts and heap drops alone no longer produce a strong GC diagnosis.
- Attach overlapping slow-tick profiler contributors to lag incidents, with marker timings and likely ownership. Clearly distinguish after-spike captures from measurements of the original stall.
- Refresh archived incidents when a matching capture finishes. Reassess older unsupported labels while retaining their previous assessment; missing historical timing cannot be recreated.
- Stop treating rolling database latency and web requests waiting for the main thread as proof of a spike's cause. Show insufficient evidence when attribution is unavailable.
- Incidental Gen2 collections no longer suppress the existing optional automatic incident capture. Profiling remains opt-in with existing bounds and cooldowns.
- Fix the stale Block ID Migration version badge. IncidentEvidence1 foundation retained; all earlier 1.2.45 fixes retained, companion versions unchanged. [Validation and limits](docs/VALIDATION-INCIDENT-EVIDENCE-1.2.45.md).

## Server Essentials Suite 1.2.45

- MigrationPosFix1: remove a direct Entity.Pos field dependency from migration targeting that could throw MissingFieldException even for looked-at blocks on a different game API build. Use the existing runtime-compatible position resolver for the coordinate fallback, honor explicit dimensions first, and refuse ambiguous targets when the player's dimension cannot be read.
- Inventory acknowledgement errors now name the exact `confirm-inventory-risk` / `confirm-overwrite` flags instead of leaving the required command option unclear.
- MigrationPosFix1 retained; includes TickUI1, StorageSave1 and Performance1. Companion versions remain unchanged.

- Tick Debugging now shows the top 12 sampled contributors during an engine capture, including likely owner/confidence, total time, maximum segment and call count. Retained results are clearly labelled after capture completion; viewing the page never starts continuous profiling.
- Add a quick investigation guide and links between Performance, Profiler, Memory and Database Storage. Make the grouped diagnosis's recent five-minute window explicit.
- Disable capture/native-action controls for read-only users so unavailable actions are clear before clicking.
- Rename individual Player Commands switches and status filters to **Enable/Disable command** and **Command enabled/disabled**. External-provider/fallback explanations remain.
- TickUI1 retained, including the storage/save and Performance1 changes below. [Tick UI validation](docs/VALIDATION-TICK-UI-1.2.45.md).

- Add a compact Database Storage guide with exact settings, example retention/debounce values, backup advice and inventory-recovery tradeoffs. Explain reusable free pages, WAL and optional compaction.
- Make automatic retention use existing timestamp indexes to avoid walking retained history when expired records were imported later; retain the existing expiry policy and batch limits.
- Correlate lag samples with measured world-save windows, including when Operations Center and Web Dashboard are disabled. Show save-overlap evidence, selected-range counts and save-correlated recent groups/smart findings. Counts alone never establish a cause.
- StorageSave1 foundation retained, including Performance1 improvements below. [Storage/save audit and tests](docs/VALIDATION-STORAGE-SAVES-1.2.45.md).

- Faster BlockLogger startup: use a lightweight read-only database probe instead of scanning the entire logging database on every start.
- Add an optional **Full integrity scan on startup** setting in BlockLogger maintenance. Manual full checks remain available in Suite Health.
- Show separate startup timings for database validation, legacy migration, schema/index work and backup retention.
- Defer automatic logger backups until the server is running and storage has warmed up for two minutes; existing quiet-server checks still apply.
- Apply logger schema/index updates in one transaction and prevent overlapping manual integrity scans. Interrupt scans on shutdown or their work deadline.
- Preserve database files on startup errors instead of automatically replacing them from an older backup. Report unavailable logger storage while allowing other Suite services to start.
- Preserve previously quarantined database originals for manual review; healthy backup rotation continues normally.
- Performance1 foundation retained. Companion versions are unchanged. [Audit, tests and measurement limits](docs/VALIDATION-PERFORMANCE-1.2.45.md).

## Server Essentials Suite 1.2.44

- MigrationBuildFix1: replace two repair-failure shutdown calls that reference `EnumExitMode` with the public `IServerAPI.ShutDown()` API. Preserve logged failure reasons and the existing condition that shutdown is requested only after writes may have occurred.
- Suite remains **1.2.44**, build `ms11-migrationbuildfix1`; includes the migration backup-bypass and earlier fixes.


- MigrationBypass1: add **Enable saved rules without automatic backup** to Block ID Migration, with separate manual-world-copy acknowledgement. It changes the existing saved rule set's backup policy and persists enabled/manual-backup settings across restarts without re-entering mappings.
- Cancel obsolete backup activation and enable compatible rules directly rather than queueing behind the old backup worker. Late backup completion cannot overwrite the new policy. Inventory preparation and block/metadata compatibility checks remain required.
- Add `/nseblockmigrate enable-no-backup manual-copy-confirmed` for the same operation in-game/console. Missing acknowledgement is refused without modifying saved rules.
- Suite remains **1.2.44**, build `ms11-migrationbypass1`; companion versions are unchanged.


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

## Newfies Server Optimizer 0.7.0 preview

- Independently controlled nearest/interactable entity search state reuse reduces temporary closures/delegates without changing the stock walker or predicate order. Recursion, exceptions and thread isolation are tested; idle state holds no input references.
- Fix worker dequeue-before-limit request loss. Add optional bounded main-thread assistance using official A*, creature types and an observed-head grace period. The soft budget cannot interrupt a single search; scheduling changes default off.
- Add stopped-capture release without forced GC or deleting exported files, Suite configuration and execution counters. Preserve strict engine hashes, foreign-patch detection and independent disable controls.

## Newfies Server Optimizer 0.8.0 preview

- Reuse bounded temporary lists in physics packet construction without changing packet data, batching, ordering or send frequency.
- Clear references after successful and failed sends; isolate nested/parallel calls and discard oversized buffers.
- Add independent configuration, actual activation/conflict reporting and Suite controls.
- Allocation measurements and real-server integration scope are documented in performance-0.8.0.md; no production TPS claim.

## Newfies Server Optimizer 0.9.0 preview

- Advance optimizer release identity from 0.8.0 to 0.9.0 as requested, retaining previous physics list improvements.
- Remove temporary LINQ projection iterators from despawn packet construction. Preserve IDs, reason/source defaults, order and independent packet array ownership.
- Add independent Suite controls, compatibility/conflict checks and packet/entry counters. No entity despawn policy, simulation or network frequency changes.
- See performance-0.9.0.md for component allocation results and validation scope.

## Newfies Server Optimizer 0.9.1 preview

- Reduce allocation in the once-per-second compatibility monitor while retaining independent switches, exact-engine guards, foreign-patch detection and failure fallback. Cache asset field metadata and avoid unrelated failure-counter reads.
- Publish the Synergy/Tungsten replacement coverage audit. Existing runtime optimizations remain; distance AI/sleeping, path-node pooling, batching and other listed features are not yet implemented. No automatic GC or chunk-budget changes.

## Newfies Server Optimizer 0.10.0 preview

- Add optional distant new-task evaluation, wander checks and stationary repulsion scheduling with independent intervals, protected player radius and entity-code include/exclude rules. Full entity ticks and running tasks continue. Pets, named/bred/owned animals, mounts, combat, damage grace and custom entity/task/behavior implementations retain stock behavior.
- Add optional waypoint candidate-node reuse with a per-solver 4,096-slot pool. Restrict reuse to official waypoint-only scopes, reset all node fields and preserve raw node ownership outside those scopes. Nested/busy/custom solver scopes and overflow use stock allocations.
- Add optional suppression of redundant full attribute resyncs when removing a nonexistent attribute. Actual removals and subclasses retain stock behavior.
- Keep same-method modules independent: remove only each module's own patch methods and verify its exact installed patches. Preserve the allocation-only repulsion patch when distance scheduling stands down. Avoid repeated assembly resolution during the compatibility monitor.
- All new switches default off. Add exact-engine contract/conflict checks, safety observer dependencies, visible counters and immediate Suite configuration. No forced GC, chunk delivery restriction or production performance claim.

## Newfies Server Optimizer 0.11.0 preview

- Add optional empty-exclusion fast paths to official raw, packet and UDP broadcasts. Run original send bodies, preserve nonempty filter callbacks, array ownership, compression, packet preparation and ordering. No transport batching or client protocol change.
- Add optional bounded, nested/thread-isolated entity cleanup/despawn temporary lists. Preserve full entity ticks, per-client tracked despawns, independently owned packet arrays and original failure/queue behavior. Clear retained entity references and discard oversized buffers.
- Add independent target statuses, exact-engine/foreign-patch checks and Suite controls/counters. Both new switches default off.
