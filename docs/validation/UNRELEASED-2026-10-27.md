# Unreleased changelog — October 26–27, 2026

Target: official Vintage Story 1.22.7. This is the cumulative release note for Suite 1.2.53 and the current NOS release candidate. Older development previews are consolidated into their final behavior.

## Server Essentials Suite 1.2.53

### Performance Center

- Fixed Refresh Overview needing multiple clicks to display fresh readings. Expired overview, timeline and page snapshots now wait for their shared replacement; fresh cache entries keep their existing intervals.
- Prevented delayed page, overview and timeline responses from overwriting newer readings or the selected range.
- Refresh Overview now uses the lightweight overview/timeline path instead of also fetching the full page and historical block activity.
- Added pending states, retained the previous graph on failure, and fixed stale error messages after recovery.
- Added hover readouts, click-to-pin inspection, arrow-key navigation, trace visibility controls, local zoom and reset. Existing lag markers still open incident details; missing samples remain gaps.
- Added actual sample/capture timestamps and honest panel freshness labels. A newly received response is not presented as a newly captured sample.
- Kept timeline, bottleneck and incident information prominent; secondary diagnostic panels can start collapsed and retain presentation preferences.
- Chart inspection and layout controls operate locally. Existing polling intervals, cached reads, request limits, hidden-tab and Safe Diagnostics safeguards remain.

### Webpanel usability and reliability

- Added consistent draft, review/save and discard controls to Login Protection, recurring Restarts, Map Settings, Export Schedule, Packet Protection, BlockLogger Recording and NOS settings.
- Background refreshes preserve edits, including edits made during an in-flight read. Failed saves retain drafts; previews show proposed changes and when they apply. Leaving the page warns about unsaved work.
- Drafts stay in page memory. Sensitive-looking fields are redacted in previews; password/file inputs are excluded from generic draft capture. Duplicate saves and role/capture locks are respected.
- Added styled keyboard-accessible confirmations for common delete actions, clearing anti-cheat history, skipping restarts, disabling OreShield, full anti-cheat saves and module changes.
- Added labelled mobile cards for anti-cheat history, performance incidents and profiler results.
- Frequently refreshed panels skip unchanged markup and reuse unchanged rows, preserving sorting, scroll positions and selections.
- Sidebar indicators show waiting/unavailable states and recover through existing staggered retries. Added a 30-second JSON-read timeout and proper malformed-JSON errors without changing write timeouts.
- Removed the OreShield Beta banner and added the requested recovery/contact note to Block ID Migration.

### Server-side anti-cheat

These functional changes are part of Suite; the separate Anti-Cheat Client receives the icon-only release listed below.

- Fixed verified vanilla gliding being checked against walking/falling limits. A real equipped vanilla glider plus active gliding selects a bounded 120 blocks/second horizontal allowance, with two seconds of equipped landing grace. Movement-mode transitions reset speed/acceleration windows; other protection remains active.
- Fixed speed findings marked ineligible for escalation still triggering generic risk punishment. Ineligible/degraded-timing speed findings are review-only across risk, correlation and consequences; healthy repeated confirmed-speed enforcement remains.
- Flood confirmation now requires observations spread across time rather than one queued burst. Findings include traffic/budget details and remain bound to the original session.
- Hardened reach-history resets and advisory flight evidence around entity/dimension changes, stale positions and movement transitions.
- Noclip correction requires repeated, spaced observations through known loaded simple geometry and a verified clear destination. Doors, chiselling, custom/complex geometry and unreliable timing remain conservative review cases.
- Added bounded terrain evidence, verified-arrival correction strikes, expiry/decay, configurable repeat escalation and failed-recovery loop protection. Correction-disabled observations cannot add noclip risk or trigger punishment.
- Added bounded Block Nuker detection for distinct client break targets, configurable burst denial and confirmation before repeat escalation. Delayed enforcement rechecks session and movement context.
- Validated inventory packet envelopes, identifiers, negative quantities and creative destinations before dispatch while retaining native transactions and crafting batches.
- Added inventory availability/open-state observations, attack-rate observations, optional exact-weapon timing rules and conservative loaded-terrain wall checks. New combat/inventory observations start in shadow mode; optional denial is separate.
- Hardened entity interaction indices/hit coordinates; added server-observed knockback allowances and improved mining evidence using the actual loaded block. Custom tools/foreign hooks can downgrade estimates to review.
- Added expiring, reasoned administrator exemptions with scoped permissions and audit records.
- Added per-player recent event timelines, bounded occurrence totals, session-average events/hour and authenticated incident exports with evidence, settings and build information.
- Grouped repeated history rows by player/finding/subject, with loaded-result counts, first/latest dates and expandable individual evidence/consequences. Grouping does not change enforcement.
- Added rate-limited warnings and bounded denied-action resynchronization using OreShield-safe transport. Missing placeholders are excluded from OreShield host selection.
- Fixed Packet Protection saves appearing reverted after a stale settings response by returning the applied snapshot.

### Web Map

- Translocator markers start hidden on every map open, including previously enabled browsers. Players can enable them for the current visit; other preferences remain intact.
- Reduced zoom-tile decoding allocations and improved hillshading at tile boundaries. Cached images receive the shading change when rendered again.
- Stream PNG output in bounded chunks instead of retaining a complete compressed tile per worker; decoding supports both old and streamed tiles.
- Added export-private releasable buffer pools with a 16 MiB cache cap per export. Completion, cancellation and failure release cached references; active working buffers are separate from that cap.
- Applied cleanup consistently to forced/live exports and exposed cleanup/read-versus-loader metrics.
- Direct-save-database mode no longer falls back to loading terrain. Resident dirty columns can use current terrain; unloaded work stays queued. Failed reads preserve the published map and dirty journal.
- Retained 1.2.52's loaded-only automatic terrain updates. Explicit legacy world-loader mode can still load terrain.

## Newfie's Server Optimizer 0.20.18-preview

- Prepared first-release installation/compatibility guidance and an observed-settings overlay, with automated activation/default checks. Existing independent switches and conservative defaults remain.
- Added optional barrel recipe memoization for unchanged storage barrels after failed searches. Position-phased forced rescans reduce synchronized searches; native contents changes/crafting remain. Default off; default rescan is about 30 seconds. Attribute/world-state-only recipe changes can therefore be recognized later.
- Added optional dropped-item clumping using native merges and stack limits. Work is bounded to loaded entities and compatible settled drops with matching ownership, attributes, cell and dimension. Default off.
- Clumping preserves quantities and partial remainders, removes only emptied sources, and keeps the older survivor's position/age. Younger merged items inherit its remaining lifetime.
- Fixed unnecessary item updates after refused merges; expanded collectible/slot patch compatibility checks and failure/disposal cleanup. Partial exceptional operations are not replayed.
- Added optional slow block-update diagnostics with queue details, separate handling/sync timings and failure counters. Default off; default warning threshold 50 ms with rate limiting. This measures stalls rather than optimizing simulation.
- Fixed asset-cache initialization rollback leaving installed hooks behind after failure. Cleanup now removes only the affected module's hooks.
- Added late detection/stand-down for known incompatible optimizer runtimes, with subscription cleanup and no recurring full assembly scan. Existing foreign-patch guards remain.
- Improved NOS web controls with actual-state summaries, module filters, clearer groups, pending-edit counts and collapsed execution details. Requested switches remain distinct from actual activation.
- The first-release candidate also includes the previously developed packet/query/crafting/container/block/chunk allocation modules, save-snapshot reference release, guarded asset compression, and optional scheduling policies. These are existing capabilities, not all new work from these two days; see the optimizer guide for individual defaults and tradeoffs.

## Companion icon releases

All seven packages include the supplied static feature-card icons. Animated assets remain separate upload files. These five companions have icon/packaging and release-identity changes only:

| Mod | New version |
| --- | --- |
| Community MOTD | 2.1.8 |
| Anti-Cheat Client | 1.1.29 |
| ChatGuard | 2.1.6 |
| Discord Link | 1.0.16 |
| PlayerList | 1.1.3 |

## Validation and defaults

The accompanying validation report records the checks actually rerun for these packages. Coverage includes browser refresh/draft/dialog/mobile behavior, compiled protection paths, map export memory, optimizer compatibility/item conservation and an official-server save/shutdown test with all seven mods.

No forced garbage collection or extra background polling was added by the workspace changes. Clumping, barrel memoization and slow block-update diagnostics remain opt-in; new combat/inventory checks start in shadow mode. Real-player glider sessions and representative full-modpack workloads remain field checks; no universal TPS gain or player-capacity claim is made.
