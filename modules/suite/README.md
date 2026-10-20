# Newfie's Server Essentials Suite 1.2.44


Current companion: Client Integrity **1.1.28 / protocol 13**, OreShield sync **v3**. Older milestone notes below describe historical intermediate protocols.

Current release and validation: see [release notes](../../CHANGELOG.md).

1.2.42 uses terrain-only decoding for startup map exports, releases export-owned pools, and preserves the existing map if a database read fails. Keep `UseDirectSaveDatabaseTerrain` enabled (the default); the explicitly selected legacy loader can still create mod/entity state.

Current build: [1.2.44 ModpackStability1 validation](../../docs/VALIDATION-MODPACK-STABILITY-1.2.44.md). [Packet-stability validation](../../docs/VALIDATION-PACKET-STABILITY-1.2.44.md) is retained. [Migration validation](../../docs/VALIDATION-BLOCK-MIGRATION-1.2.44.md) is retained. Historical [1.2.43 validation](../VALIDATION-1.2.43.md) is retained. Historical [1.2.41 audit repairs](../AUDIT-1.2.41.md) are retained. Retains [SharedLoadFix1 notes](../SHAREDLOADFIX1-NOTES.md) and [validation](../VALIDATION-SHAREDLOADFIX1.md), including [SqliteFix1 notes](../SQLITEFIX1-NOTES.md) and [validation](../VALIDATION-SQLITEFIX1.md), retaining [InteractionFix1](../INTERACTIONFIX1-NOTES.md), [AdminPanel2](../ADMINPANEL2-NOTES.md) and all preceding improvements.

Build ID: `newfies-server-essentials-suite-1.2.44-ms11-migrationlive1`

Ordinary Visual Studio / `dotnet build` and `dotnet publish` output now place native SQLite files beneath `native/runtimes/`. If zipping manually, zip the **contents** of the output directory so `modinfo.json` and managed DLLs are at the ZIP root. See [build/package instructions](../SQLITEFIX1-NOTES.md).

## MS11 Revamp1 (retained)

Server packet bounds and attribution, advisory flight review, read-only offline WebPanel inventories, Shift+L workspaces, WebPanel page navigation/layout improvements, and bounded OreShield/web-cache preparation. Target: Vintage Story 1.22.7 / .NET 10. See [release and installation notes](../REVAMP1-NOTES.md), [packet coverage](guides/PACKETGUARD-INGRESS.md), [WebPanel pages](guides/WEBPANEL-WORKSPACES.md) and [validation](../VALIDATION.md).

## OreShield v3.2.9 Perf1 + MaskHookFix2 (previous pass)


### MaskHookFix2 / Perf1

- Recipient chunk concealment is discovered at runtime across every compatible `ServerSystemSendChunks` method returning `Packet_ServerChunk` with `ServerChunk` + `ConnectedClient` context; the hook no longer depends on one method name/signature.
- Every patched method signature is logged at startup and nested wrappers are deduplicated per packet.
- Masking uses authoritative `ServerChunk` block IDs, so an already-masked packet cannot poison the candidate index or undo a legitimate discovery.
- Unsupported codecs and masking exceptions fail closed by rebuilding the authoritative chunk payload with protected ore concealed rather than leaking the original ore-bearing packet.
- WebPanel separates raw hook calls, unique packets, eligible packets, fail-closed packets/cells, and reports zero leak checks as `Not exercised`.
- OreShield settings reloads retain runtime discoveries and verified sync readiness.
- A configured 180° LOS view cone means the forward hemisphere instead of bypassing cone filtering.
- Perf1 caps aggregate LOS work across populated servers, treats unloaded client chunks as harmless sync skips, removes duplicate recovery loops, and rate-limits genuine failure recovery.
- Client Integrity remains 1.1.27 with OreShield sync protocol v3 (`sync-remesh-v3`, build marker `ac1127-oreshield-v329-perf1`).

### OreShield v3.2.9 BreakFix2 return-visibility path

- **BreakFix2:** recipient chunk re-sends now use a stable ClientId-to-PlayerUID bridge and mask directly by PlayerUID, so already-discovered ore remains real after chunk unload/reload. Legitimate discovery state is also retained across reconnects for the lifetime of the current OreShield runtime.
- **BreakFix1:** protected ore breaks now supersede any stale client reveal/conceal stabilization cell with the server's current block state in apply-once mode, preventing mined ore from reappearing and requiring a second break.
- Primary reveal routing now uses public `IServerPlayer` + Client Integrity sync with **no ConnectedClient or DidSendChunk gate**.
- LOS scanning no longer rejects candidates because an internal client/chunk-sent lookup failed.
- Exposure, bulk protected updates, and all-player discovery iterate `api.World.AllOnlinePlayers` instead of `server.Clients.Values`.
- Recipient chunk masking resolves the public player by UID/ClientId when possible and **fails closed** (fully concealed) if mapping is unavailable.
- Native `SetBlock` reveal delivery is retired; Client Integrity sync is the sole reveal transport.
- OreShield's dedicated sync sub-protocol is now **v2** with exact `sync-remesh-v2` / `ac1126-oreshield-v329-breakfix1` hello markers, so stale experimental Client Integrity 1.1.26 DLLs are rejected instead of appearing sync-ready.
- Final ACK telemetry separates successfully applied blocks from failed client applies; failed final ACKs trigger one authoritative chunk re-sync rather than being counted as successful delivery.
- Client sync timeout is 9 seconds, beyond the full 5-second deferred-load window plus the 2.5-second stabilization window; an unacknowledged sequence is retried once on the same channel.
- Final ACKs retire the pending server sequence; missing final ACKs are surfaced as sync timeouts and retried once.
- Client pending-cell replacement retires superseded sync sequences so overlapping corrections cannot strand ACK state.
- Chunk-local coordinate reconstruction is dimension-safe on both server and client.
- Removed the unused legacy custom `SetBlocks` bulk-sender path.
- Reveal dispatch now resolves `IServerPlayer` from `api.World.AllOnlinePlayers` instead of requiring `ConnectedClient.Player` to implement `IServerPlayer`.
- Client Integrity sync is dispatched independently of internal `ConnectedClient` resolution, so a valid sync-ready client cannot lose reveals because of an engine-internal player wrapper.
- Internal client matching now falls back to `ConnectedClient.Entityplayer.PlayerUID` and public `ClientId`.
- OreShield loops that previously depended on `ConnectedClient.Player is IServerPlayer` now use the same stable player bridge.
- This directly addresses runtime telemetry showing reveal requests and sync hellos with 100% recipient-resolution misses and zero LOS ray checks.

OreShield now uses real server-side selection geometry for LOS occlusion, an exposed-ore candidate index, movement/view-driven visibility scans, a configurable view cone and idle safety rescan, chunk-local per-player discovery state, stricter same-family whole-vein traversal, and outbound leak-invariant telemetry. All protected ore remains concealed by default until the server authorizes discovery. Whole-vein reveal remains disabled by default.


Current consolidated **1.2.41 / MS11** release. Retains the complete 1.2.38 functionality, interaction fix, ingress toggle, native SQLite build fixes and ChatMarkdown2 parser/guide. See `PATCH-NOTES-1.2.41.txt` for this release and `PATCH-NOTES-1.2.38.txt` for the baseline feature record.

## Inherited MS11 highlights (historical notes)

### OreShield v3 LOS + reveal fix
- All protected ore, including cave-exposed ore, is concealed by default until server-authorized discovery.
- Recipient-aware chunk masking and server-side LOS determine each player's legitimate discoveries.
- The miner gets newly exposed six-face-adjacent ore authorized immediately after a real block break.
- Recipient reveal corrections use Client Integrity sync exclusively. There is no OreShield native `SetBlock`, `forceSendChunks`, or `ExchangeBlock` reveal path.
- Whole-vein reveal defaults off and uses six-face connectivity if enabled.

### Web Map top-bar Leaderboard + offline XSkills history
- **The public Web Map Leaderboard button is in the Web Map top header bar**, not the vertical right-side map toolbar.
- Opening it updates the browser URL to **`/leaderboard`** without reloading the map; closing it or pressing Back restores the prior map URL/hash.
- Rankings include known offline players by merging Suite player history with persisted XSkills/XLib disconnected-player skill sets.
- The authenticated WebPanel leaderboard remains available separately.

### Standalone Leaderboard URLs
- Public Web Map: **`/leaderboard`**, **`/leaderboard/`**, and **`/leaderboard.html`** directly open the privacy-sanitized public leaderboard view.
- The Web Map History API now keeps the displayed URL synchronized with the leaderboard overlay.
- Authenticated WebPanel routes remain available for the richer administrative leaderboard.
- Public privacy controls, including `/webmap live off`, remain enforced.

### T2P/T2M consent
- `/t2p <player>` now requires the destination player's `/tpaccept`, matching the consent model used by `/t2m`.
- Requests expire after 60 seconds and accepted transfers use a 10-second warmup.

### Server-authoritative AFK spin
- AFK player yaw is rotated by Suite server-side logic, so the visible spin does not depend on a client Suite install.
- The optional Suite-client camera orbit remains only a visual enhancement.

### Paired module versions
- Community MOTD **2.1.2**: open-only refresh behavior and reduced recurring console traffic.
- Discord Integration **1.0.8**: corrected player counts and Suite moderation command execution.
- Client Integrity / Anti-Cheat Client **1.1.28 / protocol 13**: now includes the optional OreShield client-sync/render assistant; protocol 13 is unchanged.

### 1.2.34 Leaderboard, Anti-Cheat, and Chat Display polish
- Polished Web Map leaderboards with top-three podiums, live status chips, medal/rank emphasis, skill search, skill-card sorting, Most Skills Progressed, improved public player profiles, and stronger responsive layout.
- Anti-Cheat Players now supports click-to-sort columns for Player, Risk, Mods, Evidence, and Last Scan. Evidence can be toggled high-to-low or low-to-high and uses total/strong/server-authoritative findings for ordering.
- Added an Administrator/Owner-only **Approve Mod ZIP Fingerprint** workflow under Anti-Cheat Client Mods. ZIPs are uploaded in bounded chunks, inspected and hashed only, then the exact ModID/version/content SHA-256 can be approved through the existing custom-fingerprint store. The uploader never installs, loads, extracts, or executes the uploaded mod.
- Moved shared **Role Name Colors** WebPanel controls from Player List to Chat Display while preserving the Player List config storage and both chat/roster rendering behavior.


### 1.2.34 Web Map community leaderboards
- Added a public **Leaderboards** surface directly inside the Web Map, styled as compact dark ranking cards with a dedicated Skills view inspired by the server community leaderboard layout.
- Added optional runtime **XSkills/XLib compatibility** without a hard XLib project reference. Registered skills are discovered dynamically; each skill can show the configured Top N players by level and current XSkills XP, with derived cumulative XP used for overall totals.
- Added Suite-backed public rankings for tracked total playtime, current session time, and earliest first-seen server veterans.
- Added overall XSkills rankings for total skill levels and cumulative skill XP, plus click-through public player progression profiles.
- Added WebPanel controls for enabling leaderboards, playtime, XSkills integration, Top N size, and cache refresh cadence.
- Leaderboard HTTP responses are cache-backed; XSkills reflection is queued to the game/server thread rather than performed by public HTTP request threads.
- Public leaderboard payloads intentionally exclude player UIDs, IP addresses, Anti-Cheat/Client Integrity state, moderation records, notes, and other staff-only fields.
- Existing `/webmap live off` privacy is honored by the leaderboard service: hidden players are omitted from public rankings/profiles, and online/session badges only come from the privacy-filtered public player cache.

### 1.2.34 Discord/MOTD and Client Integrity reliability
- Fixed the WebPanel Discord MOTD Announcements, Updates, and Events channel IDs reverting to `0` in the refreshed control snapshot after save.
- Client Integrity now avoids overlapping duplicate challenge work and treats already-consumed duplicate reports as non-risk-bearing duplicate traffic.
- Manual rescans no longer replace a healthy scan already in progress.
- A valid challenge-bound heartbeat can recover a temporary heartbeat-timeout coverage state.
- Retains the 1.2.33 OreShield movement-sensitive proximity reveal correction and Shift+L Community MOTD configuration tab.
- At that historical 1.2.33 baseline, Community MOTD was **2.1.1**, Discord was **1.0.7**, and Client Integrity was **1.1.25 / protocol 13**.

### 1.2.33 OreShield proximity reveal correction
- Fixed nearby protected ore sometimes remaining visually concealed until a block update occurred.
- Reveal-radius checks now use the server-authoritative player position and update while the player moves into range.
- Added a periodic repair pulse for late chunk/update ordering.

### 1.2.33 Community MOTD dashboard and branding support
- Shift+L Discord Integration now includes a dedicated Community MOTD configuration tab with General, Pages & Feeds, Home VTML, and Rules VTML views.
- MOTD edits are permission-checked server-side, save-gated, and use the existing event-bus companion bridge; Discord does not own or store the MOTD configuration.
- Updated the Suite WebPanel integration for Community MOTD **2.1.1**.
- Updated Community MOTD branding controls/help for the dedicated `NewfiesCommunityMOTD/branding/` server-data folder.
- Retains independent Suite/MOTD builds and runtime companion discovery.
- No hard project reference was introduced between the Suite and Community MOTD.

### 1.2.32 Administration customization, Anti-Cheat enforcement, and role cooldown polish

- Expanded **Administration → Suite Customization** with safe presentation controls for Suite/panel name, header and login text, Dashboard title/subtitle, search placeholder, sidebar footer, accent color, build-ID visibility, header-logo visibility, compact layout, sidebar width, card rounding, global/module status visibility, and Dashboard Needs Attention/Operations card visibility. The editor is presentation-only and does not accept arbitrary HTML/JavaScript or change permissions/routes.
- Added **Anti-Cheat → Quick Enforcement Setup** above the advanced consequence editor. Common actions and risk thresholds can be generated as a staged rule set, reviewed, and then saved through the existing server-authoritative enforcement pipeline. The advanced editor remains available.
- Added **role-specific teleport access and cooldowns** under Player Commands for `/spawn`, `/home`, `/back`, `/warp`, and `/rtp`. Each role can be allowed/denied and can inherit the normal cooldown (`-1`), have no cooldown (`0`), or use a custom cooldown. Missing overrides preserve existing behavior.
- `/warp` role access now gates both named warps and the no-argument warp-list path, while existing per-warp role/privilege restrictions remain an additional finer-grained layer.


### 1.2.32 Patch Studio, OreShield, and profiler polish

- Added **Server Tools → Patch Studio** for persistent, constrained Harmony patch definitions. Staff can create/edit patch definitions, generate disabled templates, enable/disable/delete them, view runtime timing/skip telemetry, and review profiler-driven investigation suggestions. Patch Studio intentionally does not compile or execute arbitrary WebPanel C#; its supported recipes are Observe, ThrottleVoid, and SkipVoid.
- Expanded **Patch Studio** with a separate JSON & Recipe Patches editor. It generates a managed `NewfiesSuiteContentPatches` content mod using Vintage Story JSON Patch operations (`add`, `addMerge`, `addEach`, `remove`, `replace`, `move`, `copy`) and includes helpers for recipe output quantity, ingredient quantity/code, and recipe enable/disable fixes. Content patch changes are validated and explicitly marked as requiring a server restart.
- Hardened **OreShield** against modified waypoint/X-ray clients that scan real block IDs across loaded chunks. Shared chunk packets conceal every protected ore candidate, including cave-exposed ore far from the player. All protected ore inside the configured local reveal radius is restored to its real ID only for that player, and mining one protected ore can reveal the entire touching connected vein (bounded by a configurable maximum) so legitimate mining does not continue through fake host-rock IDs. Revision races fail closed instead of falling back to vanilla ore payloads.
- Reduced automatic engine-profiler overhead: unexplained-stall captures are now 3 seconds, trigger at 750ms+, use a five-minute cooldown, sample every 100ms, cap automatic leaf traversal at 512 entries per sampled tick, skip automatic off-thread profiling, defer mod attribution until finalization, and use bounded top-cause selection. Manual 10s/30s profiles retain the deeper diagnostic path.

### 1.2.32 WebPanel polish pass

- Added Clear All for Log Intelligence Issue Signatures (retained signatures only; source logs and bad-block-entity tracker records are preserved).
- Added custom one-off restart countdowns with an operator-supplied reason.
- Added individual persistent enable/disable buttons for each Player Commands entry.
- Added Tick Debugging ON/OFF control, sidebar state, and a horizontal metric strip.
- Capped the Web Endpoint Safety Matrix into a shorter scrollable window.
- Added a dedicated ChatGuard Banned Words editor while retaining ChatGuard 2.1.1.
- Supporter Lamp now uses a more visible dynamic-light level with **Player List 1.1.1 BuildFix5**. Lamp state is delivered both through the Suite packet channel and synchronized player watched attributes, removing the custom-channel handshake as a single point of failure while keeping `/lamp` Suite-owned.

### 1.2.32 Community MOTD companion integration

- Added **Server Tools → Community MOTD** for the optional Newfie's Community MOTD 2.1.0 companion.
- The Suite can inspect, edit, save/apply, reload, and reset seen-state through a small event-bus control bridge without taking ownership of the MOTD module.
- The page exposes presentation, join/show behavior, players-tab settings, hub tabs/content, link/logo options, and companion feed toggles.
- Community MOTD remains independently installable and continues operating normally when Server Suite is absent.
- Community MOTD GUI composition no longer depends on `AddDialogTitleBar`; its title/close row uses stable lower-level GUI primitives for Vintage Story 1.22.7 runtime compatibility.

### 1.2.32 Dashboard + ChatGuard integration polish

### 1.2.32 ChatGuard 2.1.1 WebPanel polish

- Upgraded the existing ChatGuard WebPanel page for ChatGuard 2.1.1 instead of adding a duplicate page.
- Added Protection Posture, per-rule session hit telemetry, online-player context, a safe dry-run Rule Tester, and recent-activity filtering.
- The Rule Tester uses ChatGuard's server-side rule engine and does not mutate moderation state, counters, or audit history.
- ChatGuard remains a standalone module; Suite integration continues through the event bus without a hard assembly dependency.

- Brought the Suite WebPanel up to full ChatGuard 2.1.1 moderation parity: temporary/permanent mutes, active-warning inspection, warning clearing, reasoned unmute, and the 30-day temporary-mute ceiling.
- Added an Active Warnings table and player moderation inspector to the ChatGuard page.
- Improved the main Dashboard with a Needs Attention card and an Operations Snapshot that combines Action Center, lag, BlockLogger, ChatGuard moderation state, players, and WebPanel state into an actionable overview.
- Dashboard attention items link directly to the owning investigation/control page.
- ChatGuard remains standalone; the Suite continues to use only the event-bus integration surface.

### Suite Health naming polish

- Renamed the WebPanel **System Health** navigation/page label to **Suite Health** so its scope is immediately clear.
- Clarified that Suite Health covers Suite modules, Suite-owned databases/queues, WebPanel request diagnostics, circuit breakers, retention/export policy, and other Suite backing services.
- Kept the internal `health` section/API identifiers unchanged for compatibility with existing links and code.
- Updated status-bar healthy-state navigation, keyboard-shortcut help, backup guidance, and Safe Diagnostics messaging to use the new Suite Health name.

### WebPanel nickname identity polish

- Player Administration now shows supporter nicknames as **Nickname (RealName)** while continuing to target the permanent PlayerUID/real account identity for every staff action.
- Player search and name sorting include supporter nicknames.
- Player detail adds an Identity card with nickname and authoritative real account name.
- Player Administration metrics include the number of saved supporter nicknames.
- Server Status online-player rows and Player Commands permission rows use the same nickname + real-name presentation.
- Audit logs, permissions, moderation, BlockLogger, bans, and other authoritative systems remain keyed to the real player identity.

### Supporter nicknames

- Added supporter-only `/nick` and `/nickname` commands with the dedicated `supporternickname` privilege.
- Added persistent presentation-only nicknames with a configurable 24-hour default cooldown and validation/impersonation safeguards.
- Suite Chat Display and the Suite-backed Player List use supporter nicknames without changing authoritative account names.

### Diagnostic clear controls

- Added compact Clear controls to Top Attributed Mods and Worst Profiled Ticks.
- Added Clear controls to Grouped Lag Incidents, Correlated Lag Samples, and the Performance Center Automatic Lag Incident Center.
- Clearing lag history removes only retained Performance/Profiler diagnostic incident archives and graph markers; BlockLogger, admin audit, and player history are not touched.
- Added Clear controls for Tick Debugger native-command output, slow-tick excerpts, and latest mod-attribution results.
- Clear actions are server-backed, audited, and persist after WebPanel refresh instead of merely hiding rows in the browser.

### Global status strip polish

- Reworked the top WebPanel status strip into a compact control surface with an overall Suite health state and current-page context.
- Overall Suite state aggregates monitored module health, Performance Center health, and active Action Center alerts; clicking it jumps to Suite Health when healthy or Action Center when attention is needed.
- Added live module chips for ChatGuard, Discord Integration, and Login Protection alongside Server, Anti-Cheat, PacketGuard, Memory / GC, Web Map, and BlockLogger.
- Module chips are now clickable shortcuts to their relevant pages.
- Current-page context mirrors each page's live status indicator so staff can keep page health visible while scrolling.
- Responsive priority hides low-value raw metrics first on narrower displays while preserving overall state, page context, alerts, delay, players, and module health.

### Lag-spike investigation drawer

- Yellow/orange and red lag markers in the Performance Center timeline and Profiler main-thread-delay graph are now clickable.
- Clicking a spike opens a right-side investigation drawer modeled after the player detail drawer.
- The drawer consolidates archived before/during/after health samples, cause scoring, profiler/mod attribution, SQLite/BlockLogger state, WebPanel pressure, nearby admin actions, entity-growth evidence, mod-environment changes, latest manual entity scan context, and all raw retained samples.
- Incident table rows now open the same drawer for a consistent investigation workflow.

### Chat display defaults

- Fresh `NewfiesChatDisplay.json` configurations now default to **bold player names**.
- Chat timestamps remain **disabled by default**.
- Existing server chat-display configurations are preserved; this changes only the defaults used by fresh/regenerated configuration files.
- The paired Client Integrity release for this bundle is **1.1.28 / protocol 13**.

### Compile maintenance

- Qualified the Chat Display role-color callback delegates as `global::System.Func<...>` to avoid the Vintage Story `Func<,>` namespace collision.
- Removed the duplicate `SafeFileLength` declaration from `WebDashboardService`.
- The paired Client Integrity release for this bundle is **1.1.28 / protocol 13**.

## Overview

Server Essentials provides a shared suite for:

- Block, container, entity, and administration logging
- Player administration, moderation, teleport, inventory, reports, and audit tools
- Player commands, warps, `/spawn`, `/msg`, `/r`, and starter kits
- WebPanel administration and diagnostics
- Public WebMap support, community leaderboards, and export tooling
- Automated server restart scheduling
- AFK protection and optional AFK camera support
- Discord integration hooks used by the separate Discord module
- Server health, diagnostics, memory, and storage analysis
- PacketGuard and Newfie's Client Integrity / Anti-Cheat server systems
- Ore Shield hidden-geology concealment
- Optional WebPanel integration for the standalone Newfie's ChatGuard moderation module

The main suite remains `requiredOnClient: false`. Newfie's Client Integrity / Anti-Cheat and Player List are separate companion projects.

## Anti-Cheat

### PacketGuard

PacketGuard provides server-authoritative movement, reach, mining, packet-abuse, speed, and noclip protection.

- Borderline impossible movement uses confirmation samples while extreme movement can be handled immediately.
- Reach validation uses recent server-authoritative eye-position history bounded by RTT and a maximum rewind window.
- Mining burst evidence collapses duplicate callbacks and counts distinct target blocks.
- Trusted teleport handling covers Suite teleports, `/spawn`, vanilla static translocators, and administrative moves without weakening normal movement thresholds.
- Detector trust state is scoped so unrelated movement/reach exemptions are not shared unsafely.
- TPS-aware timing relaxes timing-derived checks during server lag without weakening hard reach, extreme movement, or integrity evidence.
- Diagnostic suppression counters are available for movement false-positive tuning without adding risk by themselves.

### Risk and incident handling

Live risk is separated into Movement, Reach, Mining, Packet Abuse, and Client Integrity families. Each family has independent decay and thresholds; the overall live posture follows the strongest current family instead of summing unrelated detector domains.

Persistent reports include detector category, confidence, corroborating signals, explanation, recommended staff action, and risk-before/risk-after context.

### Client Integrity integration

Server Essentials 1.2.41 pairs with **Newfie's Client Integrity / Anti-Cheat 1.1.27**, protocol **13**.

- The optional semantic-version requirement dynamically matches the Client Integrity version installed on the server.
- Shared server/client mods are compared against the exact server-loaded build before external ModDB references are considered.
- Official ModDB comparisons can validate same-ID/version packages and provide advisory evidence.
- `ModDbAutomaticConsequencesEnabled` defaults to `false`, so ModDB-only findings do not independently trigger automatic punishment unless an operator enables that behavior.
- Startup and deep-scan handling use bounded challenge timing and liveness heartbeats to remain usable on slower systems and large modpacks.
- Repacked or renamed forbidden-mod detection can correlate normalized package content, assembly structure, managed-IL behavior, runtime assemblies, Harmony activity, and configured protected assets.

The WebPanel exposes client build state, scan/cache metrics, ModDB comparison details, runtime evidence, blacklist controls, reports, and per-player investigation data.

### Ore Shield

Ore Shield is a server-authoritative concealment-only anti-Xray layer. It changes outgoing client block data without rewriting the authoritative world, ore drops, or prospecting data.

- Only **fully enclosed** protected ore is replaced with its verified host rock in shared chunk packets. Ore with a genuinely exposed non-opaque face stays real.
- Break/place changes re-evaluate only the changed cell and its six neighbors. There is no periodic player-radius scanner.
- Exposure and re-conceal corrections are sent only to clients that already have the affected chunk.
- The public `BreakBlock` + `DidBreakBlock` path provides a delayed fallback for modded blocks that reorder or skip the normal post-break callback.
- When mining creates the first exposed ore face, optional whole-vein reveal follows the connected 3D protected-ore component for the discovering player, bounded by `OreShieldMaxVeinRevealBlocks`.
- Whole-vein corrections are throttled to 64 recipient corrections per batch with short spacing instead of dumping a pathological mega-vein into one server tick.
- Unknown or unloaded neighbor states fail closed, so chunk-edge uncertainty never leaks buried ore. When a neighboring column later becomes authoritative, only cached protected-ore candidates on the touching boundary are re-evaluated and corrected for clients that already received that chunk.
- Masked-chunk caching uses packet fingerprints plus targeted world revisions; ordinary unrelated block/container traffic stays on Vintage Story's vanilla path.
- The WebPanel **Anti-Cheat → Ore Shield** page reports real exposure-sync, vein-discovery, correction, cache, and compatibility counters instead of the old proximity-loop telemetry.

Ore Shield settings are stored in `NewfiesAntiCheat.json`. The legacy `OreShieldRevealRadiusBlocks` key is retained for config compatibility but normalized to `0`; v2 visibility is exposure/event driven.

## AFK mode

The built-in AFK subsystem provides `/afk` and `/nse afk`, a locked spectator AFK state, combat/ground checks, damage exit, hunger/thirst pause compatibility, AFK mob-spawn suppression, optional auto-kick, and XSkills Survival XP suppression.

Server-side AFK protection works without a Suite client install. A matching Suite-capable client can provide the optional orbit camera through a capability handshake.

Do not run the old standalone `newfiesafk` mod beside the integrated subsystem.

## Configuration

Suite and Discord configuration JSON is stored under:

```text
VintagestoryData/ModConfig/NewfiesServerEssentialsSuite/
```

Suite-owned configuration uses schema-aware migration with timestamped pre-migration backups. Migration aborts instead of rewriting the source if the required backup cannot be created, and newer/future schemas are left untouched by older builds.

World-specific databases, Anti-Cheat history, reports, diagnostics, terrain exports, and other generated runtime data remain under ModData/world storage rather than the configuration directory.

## WebPanel and web-server isolation

The WebPanel uses a bounded dedicated worker pool with a bounded request queue. Routine dashboard/live reads use cached stale-while-refresh snapshots so browser polling does not normally block on the Vintage Story main thread. Live game mutations still use the explicit main-thread bridge.

Performance Center exposes worker, queue, cache, and server-health telemetry. Overload returns HTTP 503 instead of allowing unbounded queued work. Major main-thread stalls can trigger a short, cooldown-limited engine-profile burst so an automatic incident can include a likely mod when the profiler provides direct ownership evidence.

## Player administration

Shift+L provides player administration, moderation, teleport, inventory, reports, server operations, WebPanel/WebMap controls, and Anti-Cheat investigation surfaces subject to normal Suite privileges.

Administrative inventory operations use server-authoritative slot data and support quantity changes, removal, give/replace actions, snapshots, comparison, restore, and audit history.

Full-map administrative teleport actions resolve a safe landing height, update `/back`, record audit information, and mark the movement trusted for Anti-Cheat.

## Automated restarts

The restart scheduler supports one or more daily UTC restart times, configurable warning thresholds, `/restarttime`, skip-next/reload controls, in-game warnings, and optional Discord restart notices.

At the scheduled time, the Suite performs a graceful Vintage Story shutdown. Process relaunch is handled by the external host/service/container supervisor or the included `scripts/restart-loop-*` wrappers.

## Optional ChatGuard integration

Newfie's ChatGuard is a separate standalone server-side module. It does not require Server Essentials and keeps its own configuration, moderation state, commands, and audit log.

When both mods are installed, Server Essentials discovers ChatGuard through the Vintage Story event bus and adds a dedicated **ChatGuard** WebPanel page for status, rules, active mutes, recent actions, and manual warn/mute controls. There is no hard DLL dependency between the two projects.

The Suite Chat Display service also checks ChatGuard before it consumes and re-emits a formatted chat line, preventing chat formatting from bypassing ChatGuard moderation.

## Chat display and languages

Chat Display formats Vintage Story General chat by default while leaving other chat groups untouched unless explicitly configured. Additional group IDs can be enabled from WebPanel or Shift+L.

Role-based player-name colors can be configured from the WebPanel Player List page. Exact role name/code mappings are shared by Player List and Chat Display; unmapped chat names retain their existing Vintage Story/mod-provided sender color.

Reserved slots can also be configured from the Player List page. They are disabled by default. When enabled, the configured number of `MaxClients` slots is kept available for exact allowed role names/codes; non-reserved players are disconnected with the configured message once public capacity is full, while reserved roles can use the remaining physical slots up to the normal Vintage Story server limit. Existing online players are never displaced when the setting is changed.

The WebPanel includes English, Russian, French, and German UI support. The in-game GUI uses the active Vintage Story client language with English fallback. Translation assets are under `assets/newfiesserveressentialssuite/lang/`.

## Diagnostics

The Memory Center exposes process private memory, managed heap, estimated native/unmanaged memory, GC committed/fragmented memory, loaded chunks/entities, BlockLogger pressure, and Animal Limiter tracking.

Database Storage and Performance Center provide additional operational visibility for long-running servers. Performance Center includes selectable 5/15/30-minute windows, a current bottleneck summary, rolling P95/peak diagnostics, Suite timing rates, and confidence-labelled likely-mod attribution for lag incidents. Automatic attribution uses only short 3-second profiler bursts after major stalls that do not already have a strong storage/GC/entity/chunk correlation, has a 300-second cooldown, and is disabled by Safe Diagnostics Mode; medium-confidence matches remain advisory rather than replacing the primary incident cause.

Tick Debugging is a separate manual diagnostics page for short investigations. It wraps Vintage Story's native slow-tick profiler state, tick-handler summary/dumps, main-thread state command, live network byte rates, and a combined incident capture that runs native slow-tick logging alongside the existing bounded engine profile. The page also surfaces the strongest non-core mod attribution and the worst individual profiled tick with its top engine-profiler causes. Native slow-tick settings are restored to their previous values when a Suite capture ends.

## Build

Set `VINTAGE_STORY` to the Vintage Story installation directory used by the production server, then run:

```text
dotnet build NewfiesServerEssentialsSuite.csproj -c Release
```

`modicon.png` is part of the project and is copied automatically by the project file. The build intentionally fails if the source icon is missing.

## Documentation

- `guides/README.md` — documentation index and starting point
- [Block ID Migration](guides/BLOCK-ID-MIGRATION.md) — missing-ID rules applied as chunks load and held-block ID selection
- `guides/QUICKSTART.md` — production setup checklist
- `guides/SETUP.md` — installation, first boot, feature gates, privileges, and subsystem setup
- `guides/UPGRADING.md` — backups, migrations, verification, and rollback
- `guides/ANTICHEAT.md` — PacketGuard, Client Integrity, risk handling, Ore Shield, and tuning
- `guides/WEBMAP.md` — public map, privacy, terrain layers, exports, and export behavior
- `guides/COMMANDS.md` — command reference
- `guides/GUIDES.md` — staff workflows
- `guides/TROUBLESHOOTING.md` — common failures and evidence collection

### Supporter lamp perk

Server Essentials registers the custom `supporterlamp` privilege and the player command `/lamp`.
Players with that privilege can toggle a persistent, low-intensity warm light centered on their player entity. The current Player List BuildFix4 renderer uses Suite light brightness 16 and reapplies active state every 100ms for a clearly visible moving dynamic light. Player state defaults off and is automatically disabled if the privilege is later removed.

The visual renderer is provided by the universal Newfie's Player List 1.1.1 companion. This keeps Server Essentials server-only while allowing the moving dynamic light to be rendered correctly on connected clients.

Example privilege grant:

`/player <name> privilege grant supporterlamp`

### Supporter nickname perk

Server Essentials also registers the custom `supporternickname` privilege with `/nick` and `/nickname`. Supporter nicknames are presentation-only: the real Vintage Story account name remains authoritative for permissions, moderation, bans, logs, and compatibility with other mods.

Fresh installs default to a 24-hour nickname cooldown, 3–20 character names, duplicate/real-name impersonation protection, and reserved staff/system-name protection. `/nick reset` clears the nickname and uses the same cooldown. The Suite Chat Display and Suite-backed Newfie's Player List use the nickname when present.

Example privilege grant:

`/player <name> privilege grant supporternickname`

Configuration/state: `NewfiesServerEssentialsSuite/NewfiesSupporterNicknames.json`.

### 1.2.34 T2M consent flow
- `/t2m <player>` now sends a request rather than immediately moving the target.
- The target accepts with `/tpaccept` within 60 seconds and then waits 10 seconds before teleporting.
- Existing WebPanel global/per-role `/t2m` cooldowns remain in effect and are charged only after a completed teleport.


### 1.2.34 OreShield close-range reveal hotfix
- Proximity reveal discovery now uses an independent authoritative live-chunk ore index rather than the packet-masking index.
- Player-local reveal updates no longer depend on Vintage Story's internal `DidSendChunk` bookkeeping, avoiding false negatives that could leave nearby ore rendered as host rock.
- The movement and repair passes continue to reveal only inside the configured local radius; shared/distant chunk payloads remain concealed.

The [current release notes](../../CHANGELOG.md) includes 1.2.38 audit fixes and the 1.2.37 release history.

Migration UI 2: [workflow and validation](../../docs/VALIDATION-MIGRATION-UI-1.2.44.md).

Rollback/logging update: [validation and limitations](../../docs/VALIDATION-ROLLBACK-LOGGING-1.2.44.md).
