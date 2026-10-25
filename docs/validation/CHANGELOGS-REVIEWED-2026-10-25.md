# Newfie's mods — changes since the current public releases

## Server Essentials Suite 1.2.52

Changes since public **1.2.51**, identified from the exact source commit embedded in its DLL.

### Chat and administration

- Added optional proximity chat with its own native in-game group and WebPanel controls under **Chat Display → Local / proximity chat**.
- Configure the group name, 3D hearing radius (1–1,000 blocks) and automatic joining. Defaults are disabled, `Local`, 100 blocks and auto-join enabled. With auto-join off, players can use the native `/group join Local` command.
- Local chat uses existing Markdown, timestamps, nicknames and role/name colors. Native moderation, sender echo and server logging are preserved; receivers must be group members in the same dimension and within range.
- General and other groups retain their delivery behavior. The existing General-only Discord relay does not forward the local group. Unknown senders or delivery-patch conflicts do not fall back to broadcasting local messages globally.
- Added no separate local-chat history database, periodic player-position scan or world-save history handler. Existing groups/history are not adopted; renaming the Suite-owned group preserves its identity. Leaving an auto-join group lasts until reconnect.
- Mod Management now shows missing, disabled and outdated dependencies, required-by relationships and declared game-version requirements.
- Added ignore/restore controls for update notices, with an **Ignored Mods** table showing who ignored each entry and when. Ignored entries no longer inflate the outdated count; they remain available to restore.
- Ignore decisions persist across restart and expire when the installed version, offered release, game version or result status changes. Failed saves leave both the saved and displayed ignore state unchanged.
- Dependency chips distinguish missing, disabled, too-old and uncomparable versions, and show which installed mods require a dependency. Game dependencies are interpreted as declared minimums, not a guarantee that every newer game version is compatible.

### Performance pages

- Added consistent navigation, a jump-to-panel menu, refresh controls and reading-age indicators across Performance Center, Tick Debugging, Profiler, Memory, Optimizations and Suite Health.
- Moved the Unified Performance Timeline above Suite Timing Hotspots and made long workspace tables scroll within their panels.
- Added page-specific **Auto refresh**, independent of the global Live button, with 5/10/15/30/60-second choices. NOS status has a 30-second minimum; browser preferences and the selected timeline range are retained.
- Automatic Performance Center updates refresh both the overview and timeline without querying BlockLogger history, scanning entities or inspecting Harmony. NOS status refresh preserves unsaved setting drafts.
- Updates avoid overlap and pause in hidden tabs, while editing, when paused or under Safe Diagnostics mode. Failed refreshes retain the previous readings and show an age/error indicator.
- **Refresh readings** does not start profiling, entity scans, forced GC or heap capture. Suite Health remains manual because it runs subsystem/database checks.
- Added cross-incident **Recurring patterns** summaries for approximately regular intervals, repeated world-save overlap, recurring assessments, repeatedly measured mod contributors and worsening spike severity/frequency.
- Findings show supporting counts, confidence, evidence and suggested next steps. They use retained incident archives, so missing or evicted incidents limit the result.
- Recurring mod suggestions require matching retained profiler measurements with meaningful measured work; historical confidence labels alone are insufficient. Repeated autosave overlap is reported as correlation, not proof that saving caused the stall.
- Added Suite controls for NOS callback-registration indexing and native despawn-cleanup diagnostics when supported by the installed optimizer.

### WebMap

- Added optional bounded incremental terrain updates under **Web Map → Export Schedule → Live explored terrain**. Defaults are off; the starting settings are 120 seconds and four tiles per pass.
- Configure 30–3,600 seconds and 1–16 tiles per pass. A compatible initial export and the direct save-database reader are required.
- New exploration appears after it reaches the normal world save and the scheduled map update; newly explored columns within an existing tile are detected too.
- Automatic passes never generate terrain, load live world chunks or escalate into a full rebuild. Exports do not overlap, and queued startup exports are preserved.
- Changed tiles and their parent images refresh while unrelated cached images and the current map camera remain intact. Clients recover safely if they miss a publication revision.
- If saved terrain cannot be decoded, the published map stays available and later intervals can retry. Turning automatic updates off stops new passes; turning WebMap off cancels a running export.
- Automatic exploration leaves possibly unsaved block edits in the dirty journal for **Update Changed Terrain**; it does not claim to stream every block edit immediately.
- Improved dirty-chunk acknowledgement so an edit elsewhere does not prevent completed chunks being acknowledged; newer edits remain pending.
- Improved tile selection fairness and added clearer vanilla rock colors, including pale chalk/limestone and dark basalt. Rebuild existing terrain tiles to recolor cached terrain.

### Efficiency and safeguards

- Cached AFK reflection metadata, removed per-ore position allocations in OreShield masking, and reused log parsing helpers.
- Bounded ZIP-entry reads during anti-cheat mod fingerprinting.
- Kept optional optimizer settings unchanged. This release does not claim that every lag spike has a proven cause.

## Community MOTD 2.1.7

Changes since public **2.1.6**.

- Improved Discord-fed News, Updates and Events formatting when paired with Discord Link 1.0.15: headings, emphasis, lists, quotations, code, links and embed content are easier to read.
- Replaced unsupported emoji with readable fallbacks instead of empty squares; Discord mention/timestamp markup is normalized for display.
- Preserved clickable first-line links and posts beginning with code fences, lists or quotations.
- Improved plain-text/markup escaping so special characters display correctly without becoming unintended formatting.

## Server Essentials Discord Link 1.0.15

Changes since public **1.0.14**.

- Fixed live player counts used by `/players` and `/serverinfo`, using the world roster and deduplicating connected/playing sessions without counting known offline records.
- Added `/allowcharselonce`, retained the existing character-selection alias and staff permission checks, and updated the slash-command registration signature so Discord receives the new command.
- Improved offline player command result reporting. Staff command inputs now have limits of 64 characters for player names, 32 for durations and 240 for reasons; kick reasons are restricted to one line.
- Improved MOTD feed conversion for Discord Markdown, embeds, links, quotations, lists, code, emoji, mentions and timestamps.
- Preserved first-line links, opening code fences, lists and quotations instead of stripping formatting during title extraction. Plain headings still become titles without unnecessary duplication.
- Improved display of message/embed titles, descriptions, fields, authors and linked content in the MOTD feeds. Use with MOTD 2.1.7 for the complete display improvements.

## Anti-Cheat Client 1.1.28

Changes since public **1.1.27**.

- Improved consent-dialog cleanup and disposal so GUI resources are released consistently.
- Closing an unanswered consent dialog now resolves it as declined instead of leaving a pending decision.
- Kept the existing client evidence protocol compatible; this is not a new anti-cheat detection system.

## Newfie's Server Optimizer 0.20.14-preview

Unpublished engineering preview; no verified public optimizer release exists to compare against. This pass improves the previous development preview, 0.20.13.

- Hardened Harmony teardown/reconfiguration so only installed hooks are removed and profiler observers are preserved.
- Improved official Survival assembly checks and deferred native binding for safer compatibility fallback.
- Corrected optimizer version reporting and shutdown/native-thread-ID handling.
- Added optional every-call timing for main-thread chunk completion, chunk unloading and entity-despawn sending. Enable `Profiling.PhaseTimings` for an explicit capture; these measurements do not change gameplay scheduling.
- Allowed known Suite/NOS observer combinations while retaining unknown-owner conflict diagnostics.
- Added offline capture findings and A/B comparison tools, with checks for short captures, mismatched/missing workload data, overwritten history and differing sample intervals.
- Retained independent module switches and existing defaults. No forced garbage collection or new automatic world-save/world-generation changes were introduced by this review.

## Unchanged compiled releases

- **ChatGuard 2.1.4** — unchanged official release included.
- **Player List 1.1.2** — unchanged official release included.

## Reviewed but NOT included in these builds

These were present on Claude's branches but were deliberately excluded. They are not features of the compiled downloads.

- **WebMap translocator layer:** proposed markers/links for discovered or used translocators. Withheld because reading a discovered pad's target could publish the coordinates of an undiscovered destination; source/destination matching could be wrong; player-area scans and JSON writes also added substantial game-thread work. **There is no translocator layer in Suite 1.2.52.**
- **Open-ended live slow-tick logging:** withheld because the proposed status request reads/parses logs synchronously on the game thread.
- **Confirmed GC cause attached to each hot tick:** withheld because process-wide pause deltas were not reliably aligned with the recorded tick interval.
- **Automatic player-activity SQL enrichment:** withheld because timing out the wait did not stop the underlying database query, allowing background work to accumulate.
- **Offline OreShield discovery eviction:** withheld because cleanup could sort/delete an unbounded set of records during a game tick.
- **Suite per-second burst timer:** withheld because the final burst could remain unreported and later calls could combine separate windows.

## Baseline clarification

Discord Link **1.0.14** and MOTD **2.1.6** were already public. Their existing features are not all new in this update: for example, temporal-storm/death relay was already present in Discord Link 1.0.14, and existing MOTD scrolling/layout is not a new 2.1.7 feature. The shared feed-formatting improvements above span both updated mods.

Website, Website Connector and Mod DB HTML descriptions remain excluded from this source/distribution. The optimizer remains optional. These expanded notes describe the same compiled binaries previously supplied; this is a documentation correction, not another mod update.
