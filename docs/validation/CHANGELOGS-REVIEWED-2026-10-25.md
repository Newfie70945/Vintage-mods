# Newfie's mods — changes since the current public releases

## Server Essentials Suite 1.2.52

Changes since public **1.2.51**, identified from the exact source commit embedded in its DLL.

### Chat and administration

- Added optional proximity chat with its own in-game group, automatic joining, configurable range and WebPanel controls. Delivery respects distance and dimension; the Suite's local group does not retain chat history.
- Mod Management now shows missing, disabled and outdated dependencies, required-by relationships and declared game-version requirements.
- Added ignore/restore controls for update notices. Ignores expire when the installed version, offered release or game version changes. Failed saves leave the previous state intact.

### Performance pages

- Added consistent navigation, refresh controls and reading-age indicators across Performance Center, Tick Debugging, Profiler, Memory, Optimizations and Suite Health.
- Added controlled live overview/timeline refresh without repeatedly running heavy scans. Refreshes avoid overlap and respect paused updates, active editing and Safe Diagnostics mode.
- Improved timeline recovery after hidden panels/resizing and protected against older time-range responses replacing newer results.
- Improved incident filtering, navigation between nearby spikes, contributor copying and recurring-pattern summaries. Recurring mod suggestions require retained measured profiler evidence; historical confidence alone is insufficient.
- Added Suite controls for NOS callback-registration indexing and native despawn-cleanup diagnostics when supported by the installed optimizer.

### WebMap

- Added optional bounded incremental terrain updates as new saved terrain is explored, without loading that terrain into the live world.
- Changed tiles and their parent images refresh while unrelated tiles and the map camera remain intact.
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

- Fixed `/players` undercounting by using the live world roster and deduplicating connected/playing sessions.
- Added `/allowcharselonce`, retaining the existing character-selection alias and normal staff permission checks.
- Improved offline player command result reporting and limited staff command inputs; kick reasons cannot inject extra command lines.
- Improved MOTD feed conversion for Discord Markdown, embeds, links, quotations, lists, code, emoji, mentions and timestamps.
- Fixed first-line rich content being stripped or losing its destination. Use with MOTD 2.1.7 for the complete display improvements.

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

The website, Website Connector and Mod DB HTML descriptions are excluded from this source/distribution. The optimizer remains optional. The translocator layer and unsafe profiler/audit-reader proposals were withheld; see the audit report for reasons.
