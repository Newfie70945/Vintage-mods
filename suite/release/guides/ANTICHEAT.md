# Anti-Cheat / PacketGuard / Ore Shield Guide

## Ore Shield hidden-geology virtualization (1.2.37)

Ore Shield is a server-side X-ray countermeasure. It never edits the real world. Recipient-aware outgoing chunk packets replace protected natural ore with verified host rock, including cave-exposed ore. A player receives the real ore ID only after server-side discovery authorization. Discovery is one-way for the Ore Shield runtime, survives reconnects/chunk reloads, and is retained across an in-process Ore Shield configuration restart. Player-placed protected ore is exempt and its exemption coordinates are persisted with the world save.

Ore Shield only virtualizes conservative candidates: opaque full cubes with no block entity or light emission and a verified conventional `rock-*` host. Unknown modded geology is skipped instead of guessed. Unknown/unloaded neighbors do not establish exposure. The server world, drops, recipes, prospecting systems and saves remain authoritative.

Periodic discovery checks exposed candidates within the configured range and view cone against authoritative server line of sight. A completed mining break authorizes the six face-adjacent protected ore cells for the miner. Whole-vein reveal is optional, disabled by default, bounded by `OreShieldMaxVeinRevealBlocks`, and restricted to the same ore family and host. Correction delivery uses Client Integrity sync protocol 3; the supplied compatible client is required for reveal corrections. Mining/placement corrections supersede stale client stabilization cells so a mined ore block does not reappear.

The concealed baseline cache validates compressed packet fingerprints and targeted revisions. Per-player discovered chunks are rebuilt for that recipient. The performance pass bounds preparation, index construction and boundary refresh work, and resumes chunk/cell sweeps across ticks. Heavy load can delay new discovery; it never authorizes disclosure merely because a work budget is exhausted. See [Ore Shield discovery performance](ORESHIELD-PERFORMANCE.md) for exact ceilings, telemetry and validation limits.

OreShield is in beta and disabled on fresh configurations. Existing saved settings are retained. It is functioning, with bug fixes and performance polishing still in progress.

Baseline when you choose to enable it:

```text
OreShieldEnabled = true
OreShieldProtectModdedOres = true
OreShieldRevealOnUnknownNeighbor = false
OreShieldStrictOreMatching = true
OreShieldRevealRadiusBlocks = 0  # legacy field; no proximity bubble
OreShieldDiscoveryRangeBlocks = 96
OreShieldVisibilityScanIntervalMs = 250
OreShieldMaxLosChecksPerPass = 96
OreShieldRevealWholeVeinOnMine = false
OreShieldChunkCacheEnabled = true
OreShieldChunkCacheMiB = 96
OreShieldMaxCachedChunks = 4096
OreShieldDecoysEnabled = false
OreShieldDecoyPercent = 0
```

Legacy decoy settings remain for configuration compatibility and cannot enable fake ore output. Use `/anticheat oreshield` and the WebPanel to inspect resolved variants, masking, discoveries, sync delivery and cache activity. Required internal hooks remain pinned to Vintage Story 1.22.7; an incompatible runtime disables Ore Shield while the rest of Anti-Cheat continues. Exceptions or unsupported compression during an active mask use fail-closed substitution.

## Client Integrity 1.1.26 rollout and protocol-13 self-integrity hardening

Client Integrity **1.1.26** uses internal protocol **13**. It retains the hardened report hash, challenge-bound heartbeat, challenge queueing, scan-time heartbeat, session-lifetime package/file fingerprint caching, and player-authorized **ModTransferConsent** flow. Protocol 13 requires **ServerBaselineSelfIntegrity** and adds fresh **RandomizedSelfAttestation** method/type probes selected from the exact companion installed on the server.

- **Report hash v2** binds scan timing/control metadata to the existing challenge nonce and report payload digest.
- **Challenge-bound heartbeats** include a monotonic sequence and a digest tied to the currently active server challenge.
- **ChallengeQueueing** retains one latest pending challenge while a deep scan is busy instead of silently dropping overlapping requests.
- **ModTransferConsent** provides an explicit player decision packet and server acknowledgement before the client may prepare or upload a requested mod package.
- **ServerBaselineSelfIntegrity** binds the Client Integrity DLL SHA-256, MVID, DLL size and normalized package content into the challenge-bound report; the server cross-checks those values against its own installed companion, the player's ModLoader record and the independent loaded-assembly scan.
- **RandomizedSelfAttestation** selects a new bounded set of managed method-IL tokens, type-metadata tokens and assembly-reference measurements for each deep challenge. Results are bound to the challenge ID/nonce and compared with the server-installed Client Integrity assembly.
- A single randomized attestation failure does **not** disconnect the player. The default is three consecutive independently randomized failures before kick; heartbeat changes only request a new deep scan.
- Missing or unreadable self-measurements are retried on three fresh challenges before a verification-unavailable disconnect; successfully measured values that differ from the server baseline remain deterministic fail-closed mismatches.
- **Signed build manifests** are optional until the server owner creates an offline RSA key, signs the compiled build, configures the public key, and enables `RequireSignedClientBuildManifest`. Never store the private signing key in GitHub or a mod package.

The WebPanel exposes separate rollout controls for hardened report/heartbeat capabilities and semantic client-version matching. The semantic version requirement is derived dynamically from the Client Integrity package installed on the server. For this package it resolves to **1.1.26**. Protocol compatibility, hardened capability requirements, and semantic-version matching remain separate compatibility checks.

These controls are defense-in-depth. Because the client is user-controlled, the server still relies on server-authoritative PacketGuard checks, one-time nonces, ModDB/server fingerprints, and evidence correlation rather than assuming a client-side secret cannot be extracted.

Server Essentials: **1.2.37**
Anti-Cheat config schema: **v61**

This guide explains the server-side Anti-Cheat surfaces and how to tune them without turning ordinary mod behavior into punishment.

## What the Anti-Cheat contains

### PacketGuard

Server-authoritative detection for movement, reach, mining timing/bursts, packet behavior, mount movement, trusted transitions, and related server-observable behavior.

Schema 61 adds verified vanilla ingress and allocation guards. See [PacketGuard ingress hardening](PACKETGUARD-INGRESS.md) for resource caps, live hook status, decor request attribution and compatibility limits.

### Client Integrity

The separate **Newfie's AntiCheat Client** companion provides client-mod/runtime integrity evidence when installed and required by policy. Client Integrity is the Anti-Cheat client component; it is not a separate unrelated system.


## Client Mods / ModDB verification (1.2.27)

The WebPanel **Client Mods** tab shows the complete third-party mod inventory reported by currently online Client Integrity sessions. For each client mod it now exposes the reported package/content SHA-256 values, exact ModID/version ModDB resolution state, the number of official same-version upload candidates found, the selected official package/content hashes for display, comparison results, comparison basis, and the last resolution message/time.

ModDB resolution runs **asynchronously on the server**. With `StrictClientModDbEnforcementEnabled=true` (the default), startup admission remains pending until every reported third-party client mod has an exact trusted fingerprint. The resolver can spend up to 90 seconds on an individual HTTP operation and fingerprints up to eight official uploads that declare the same ModID and semantic version. Old schema-1 single-upload cache results are marked stale and rebuilt automatically.

By default:

```text
ClientModDbVerificationEnabled = true
StrictClientModDbEnforcementEnabled = true
ModDbAutomaticConsequencesEnabled = false
```

`ClientModDbVerificationEnabled=true` is the important integrity check: it compares a reported exact ModID + version with official release fingerprints, which is how the server can identify a modified copy of an otherwise allowed mod. The WebPanel exposes this as **Verify reported client mods against the official Vintage Story ModDB**. Turning it off stops automatic official ModDB lookup/comparison, but it does not disable the local client inventory or blacklist checks.

`StrictClientModDbEnforcementEnabled=true` is a separate admission rule from risk scoring/consequence rules. A reported third-party mod is accepted only when it exactly matches the server-loaded copy, an administrator-approved custom fingerprint, or an official same-version ModDB content/package fingerprint. `NotFound`, `Unavailable`, or exact hash mismatch results fail closed and disconnect the player. Private/client-only mods therefore need an explicit exact custom approval before players can use them.

`ModDbAutomaticConsequencesEnabled=false` still keeps ordinary ModDB findings out of risk scoring and configurable consequence rules. The strict database kick is an admission-policy decision, not a cheat verdict, and does not add cheat risk by itself.

The **ModDB Verification Health** card separates database/cache health from player integrity. `Unavailable` means the external lookup could not be completed and is not a modified-mod verdict. The card shows fresh/stale cache counts, resolved/not-found/unavailable results, pending work, and the most recent successful resolution.

The **Client Connection Diagnostics** table retains the latest handshake/startup events after a player leaves. Use it to distinguish `HELLO_GRACE_EXPIRED`, a recovered late hello, protocol rejection, heartbeat loss, startup scan extensions/retries, hard-limit scan timeout, and the exact disconnect path. This is the first place to check when a player reports a false "missing Anti-Cheat" kick.

For DLL integrity, **SHA-256 is authoritative**. `MODDB_ASSEMBLY_MISMATCH` is emitted only when a client DLL has one unambiguous owning mod, both sides have valid SHA-256 values, and the client hash differs from every same-name DLL in every successfully fingerprinted official upload for that exact version. File size, DLL layout, structural similarity, and behavior fingerprints are supporting diagnostics only.

A raw package SHA mismatch is also **not sufficient by itself** to label a client mod Modified when normalized content is unavailable. Repacking the same files can change ZIP-level SHA-256. The WebPanel shows that case as **Diagnostic difference** unless normalized content differs or an unambiguously associated DLL SHA-256 differs from all exact-version official candidates.

**Fingerprint Clusters** group online players by ModID + version + reported content fingerprint. This is useful for spotting an outlier quickly: for example, a large official cluster used by most players and one different fingerprint used by a single player.

## Startup scan timing (1.2.27)

The startup/connection timing is now configurable from the WebPanel and `NewfiesAntiCheat.json`. The 1.2.27 defaults are:

```text
ChallengeTimeoutSeconds = 30
StartupChallengeTimeoutSeconds = 300
StartupChallengeHeartbeatExtensionSeconds = 120
StartupChallengeHardLimitSeconds = 900
ClientHelloGraceSeconds = 90
ClientHelloDisconnectDelaySeconds = 60
HeartbeatTimeoutSeconds = 180
```

The first deep Client Integrity scan gets **300 seconds** initially. If protocol-13 heartbeats remain healthy while mod/package hashing is still running, the server extends the **same** challenge in 120-second windows, never beyond the **900-second total startup hard limit**. A client that does not establish its hello handshake gets 90 seconds plus a 60-second recovery window before the required-client disconnect path can run. Once connected, heartbeats can be stale for 180 seconds before coverage is marked timed out.

Routine rescans keep their separate 30-second timeout so increasing startup tolerance does not make every later integrity check unnecessarily slow. The 1.1.25 client build also uses a lightweight cached heartbeat while a deep scan is running, avoiding extra runtime/self-integrity/Harmony scans that would compete with package hashing on slower PCs.

Client Integrity 1.1.26 also reduces recurring telemetry cost: unchanged managed assembly sets reuse the last full runtime census, signed-build-manifest reads are source-signature cached, hashing uses pooled/reused I/O buffers, and large assembly/Harmony sweeps yield cooperatively. Harmony and randomized self-attestation telemetry still refresh independently; protocol 13 and the deep attestation requirements are unchanged.

Client semantic version labels remain informational when the server-version-match policy is OFF. When that policy is ON, the client must report the same semantic Client Integrity version detected from the server-installed companion. This is a compatibility gate only; it does not add cheat risk.

## Finding vs proof

A finding means the server observed evidence that matched a detector. It does **not** automatically mean the player cheated.

When reviewing a report, read:

- detector/finding code;
- confidence;
- risk before/after;
- position;
- raw evidence;
- nearby/corroborating signals;
- suppression/diagnostic context;
- consequence that actually ran.

High confidence can mean the server is highly confident that an abnormal transition occurred, not necessarily that it knows the cause was malicious.

## Per-family risk (schema v40)

Live risk is separated into:

```text
Movement
Reach
Mining
Packet Abuse
Client Integrity
```

Each family decays independently and has its own review/suspicious thresholds. The overall live risk is based on the strongest active family instead of adding unrelated families together.

This prevents chains such as:

```text
old mount-sync movement noise
+ later unrelated teleport-like event
= automatic cross-family punishment
```

Movement detectors also use strongest-signal isolation so several movement detectors do not blindly stack the same physical event multiple times.

## Packet Protection in Shift+L

Open:

```text
Shift+L → Anti-Cheat → Packet Protection
```

The in-game Packet Protection page now mirrors the operator-facing PacketGuard controls through five sub-tabs:

- **General** — PacketGuard master switch, player chat warnings/cooldown, noclip/block-collision controls, acceleration sampling/smoothing, mount acceleration support ratio, diagnostic context, suppression telemetry, and speed-history depth.
- **Movement** — movement sampling, hard movement distance, confirmation samples/window, extreme-movement multiplier, correction grace, enforcement toggle, admin/mounted exemptions, mount grace, engine teleport grace, TP Runes compatibility, and Th3Essentials `/spawn` compatibility.
- **Reach** — base tolerance, latency compensation, position history/rewind, use/break extra tolerance, near-threshold strikes, strike window/decay, hard excess, and out-of-range rejection.
- **Mining / TPS** — connected-vein window, connected-break limit, duplicate target window, burst rejection, cadence validation/timing/confirmations, plus TPS-aware relaxation thresholds and live TPS health status.
- **Risk** — per-family risk enablement and independent Movement, Reach, Mining, Packet Abuse, and Client Integrity decay/review/suspicious thresholds, plus incident correlation and report-history retention.

All PacketGuard-specific controls now live on this page instead of being split between **Settings** and **Packet Protection**. The general Anti-Cheat **Settings** page is reserved for client-scanner/integrity options and will not overwrite PacketGuard values.

All changes are staged locally until **Save Packet** is pressed. Saving uses the live PacketGuard refresh path, so a full server restart is not required. Reopen or reload the page after saving if you want to confirm the persisted values.

## WebPanel Packet Protection

The WebPanel exposes the same PacketGuard tuning plus broader Anti-Cheat administration such as advanced rule editing, consequence rules, detailed reports, history review, and diagnostics.

## Noclip / block collision detection

This detector is intentionally conservative on modded servers. Complex vanilla/modded collision shapes can cause a player to briefly overlap geometry without cheating.

Recommended approach:

1. Leave it disabled until you know you need it.
2. Enable it during a controlled test period.
3. Watch suppression counters and reports.
4. Test doors, trapdoors, kilns, mounts, custom seats, carts, and complex blocks from your modpack.
5. Only keep enforcement enabled if the evidence is consistently clean.

Stationary overlap by itself should not be treated like active noclip movement.

## Mount false positives

PacketGuard contains hardening for mount transitions and unknown/modded mounts. Still review any report containing evidence such as:

```text
profile=unknown-modded-mount
very low horizontal speed
very high vertical speed
```

That pattern can indicate rider/seat synchronization rather than cheat movement. Known mount profiles can enforce real movement envelopes; unknown mounts should be treated more cautiously.

## Teleport-like movement

A multi-hundred or multi-thousand block position change over a fraction of a second is teleport-like telemetry. PacketGuard attempts to match trusted server transitions and re-baseline legitimate/unverified transitions conservatively.

If a report still appears:

1. Identify what moved the player: admin command, portal, mod teleport, respawn, mount, scripted event, etc.
2. Check whether that source is registered/recognized as trusted.
3. Compare the movement event with other findings from the same time window.
4. Do not lower global movement limits just to accommodate one teleport source.

## TPS and timing

Timing-derived checks are relaxed/deferred when server tick health is poor. Hard evidence such as extreme reach or integrity findings is not automatically weakened just because TPS dipped.

When investigating a timing report, compare it with:

- server tick health;
- packet/sample age;
- ping/jitter;
- recent speed history;
- suppression reason;
- mount/teleport state.

## Chat warnings

PacketGuard can send player-facing chat warnings for eligible findings. These are separate from staff reports and consequence rules.

If the warning toggle does not persist, use 1.2.15 or later and confirm the page reports the saved state after **Save Packet Protection**.

## Consequence rules

Use consequence rules only after the detector behavior is understood on your modpack. Family-specific conditions such as `FamilyRiskAtLeast` can enforce based on the relevant risk family rather than unrelated total noise.

Good policy pattern:

```text
telemetry / review first
→ repeated clean evidence
→ warning or staff review
→ enforcement only for high-confidence/repeated policy violations
```

## Commands

The built-in root currently exposes:

```text
/anticheat status
/anticheat status <player>
/anticheat rescan <player>
```

Packet Protection toggles are managed through Shift+L, WebPanel, or config rather than separate chat toggle commands.

## Config

Main file:

```text
NewfiesAntiCheat.json
```

Keep a backup before large tuning changes.

## What to collect for a false-positive report

Provide:

```text
player name
exact timestamp
finding code
full Evidence / guidance text
position
affected mount/block/mod mechanic
server TPS/tick-health context
whether the player was teleported/mounted/respawning
relevant config section
```

Do not tune from the summary line alone.

## Network hardening

The server now reuses an already-registered Client Integrity network channel when one is present instead of blindly trying to register a duplicate. If no channel exists, the Suite creates it using the original lifecycle. The Anti-Cheat runtime also bounds pathological hello, scan-report, and heartbeat bursts before expensive validation work, without adding cheat risk merely for dropped transport spam.

The WebPanel/self-test reports the exact runtime availability reason (disabled, companion missing, version mismatch, or channel activation error) instead of the older generic duplicate-channel warning.


## Player-authorized suspicious mod transfer

With Client Integrity **1.1.26 / protocol 13**, an administrator can request a copy of one **specific loaded mod package** that Anti-Cheat already associates with suspicious evidence. This is a scoped package-transfer feature, not a general remote file browser.

- The request contains a ModID from the player's accepted ModLoader inventory; the server cannot request an arbitrary client filesystem path.
- The player receives a popup before any package bytes are read or prepared for transfer. It identifies the requested mod/version, package name, maximum transfer size, transfer scope, and the server's refusal policy.
- The authorization popup is created only after a WebPanel administrator manually clicks **Collect ZIP** for that player/mod; automatic scans and mismatches do not open it.
- **Agree & Transfer** sends explicit authorization. The server records/audits that decision and returns an acknowledgement; only then does the client read/hash/archive the package for transfer. The server rejects transfer-start packets without recorded authorization.
- **Decline**, closing the popup, or pressing Escape sends an explicit decline. The WebPanel-configured decline policy is applied on explicit decline: **None**, **Disconnect**, or **Temporary Ban** with a configurable duration (default 24 hours). Technical dialog failure or a request that simply expires without a player decision is not treated as an explicit decline.
- Existing ZIP mods are preserved byte-for-byte. Folder or loose-file mods are wrapped into a temporary ZIP snapshot; symlink/junction traversal is skipped.
- Transfers are bounded by `SuspectPackageCollectionMaxMb`, bandwidth-limited by `SuspectPackageUploadKbps` (384 KB/s by default), timeout-protected, chunked, streamed to disk, and verified by SHA-256 before acceptance.
- Stored packages remain under `ModConfig/NewfiesServerEssentialsSuite/SuspectedHacks/`. Evidence ZIPs are automatically deleted 15 days after collection. The `.zip.json` authorization/provenance sidecar is retained for audit history and records UID, mod/version, original filename, hash, size, collection time, and transfer provenance.
- The server never extracts a stored suspicious ZIP. Review it only in an isolated analysis environment. Authorization/decline events and collection actions are written to the administration audit trail.

The popup and audit trail improve transparency, but they are not a guarantee of legal consent. A refusal penalty such as a disconnect or temporary ban can matter to whether consent is considered freely given under some laws. Server operators should publish this access condition clearly before players join and obtain legal advice for their jurisdiction.

The Anti-Cheat player view exposes eligible suspicious packages and already-stored evidence. A package is eligible only when it is in the player's current reported mod inventory and tied to current suspicious/blacklist evidence.

## ModDB startup baseline warming

When Anti-Cheat and official client-mod ModDB verification are enabled, Server Essentials snapshots client-relevant third-party mods installed on the server and pre-resolves their exact ModID + version fingerprints during startup. Fresh cache entries are reused and only missing or stale entries are queued. The queue remains capped at two concurrent ModDB requests.

The WebPanel Client Mods page shows startup-baseline readiness separately from live player verification. `Unavailable` means the external lookup failed; it is not a modified-mod verdict.

## Player investigation drawer

Opening an Anti-Cheat player now shows a quick posture strip for status/risk, server-version match, heartbeat freshness, mod-verification state, and findings. The drawer includes quick jumps to Timeline, Findings, Mods, Evidence, Runtime, and Scanner Health, plus player-specific recent connection diagnostics and scan/cache performance context.
