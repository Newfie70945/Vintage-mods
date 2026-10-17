# Newfies AntiCheat Client 1.1.28 — standalone source folder

Current patch: transfer consent dialogs release textures and unregister after closing. Protocol 13 and networkVersion 1.1.21 are unchanged. See [changelog](CHANGELOG-1.1.28.txt).

This folder is a **separate Anti-Cheat add-on for server owners using Newfies Server Essentials Suite**. Installing it on the server makes the same package mandatory on clients through Vintage Story's mod dependency handshake. The Suite owns the server-side Anti-Cheat protocol, PacketGuard, reports and consequences.

- Mod ID: `newfiesanticheatclient`
- Assembly: `NewfiesAntiCheatClient.dll`
- Target: Vintage Story 1.22.7 / .NET 10
- Server protocol: `newfiesanticheat`, protocol 13
- Protocol 13 adds fail-closed **server-baseline self-integrity**: the DLL SHA-256, MVID, DLL size and normalized Client Integrity package content must match the exact companion installed on the server, and those values are cross-checked against both ModLoader inventory and the independently scanned loaded assembly.
- Network compatibility version: `1.1.21` (intentional wire-compatible rollout pin)
- `requiredOnServer: true`
- `requiredOnClient: true`

## Server-owner optional deployment

The add-on itself is optional for a server owner: a Suite server may choose not to install it. If it **is** installed, clients are not allowed to opt out. New Anti-Cheat configuration defaults enabled when the server add-on is present. Live enforcement can be toggled from the web Anti-Cheat page or Shift+L → Anti-Cheat → Overview, but disabling enforcement does not remove the installed client dependency.



## 1.1.25 client performance pass

- The 15-second heartbeat remains lightweight and continues to send the last stable telemetry immediately.
- Periodic heartbeat telemetry now compares a cheap identity-only managed-assembly marker first. If the assembly set has not changed, it reuses the last full runtime census instead of re-hashing/rebuilding every loaded DLL record.
- Harmony patch telemetry and randomized self-attestation still refresh independently, so runtime-census reuse does not freeze patch/tamper monitoring.
- Signed build-manifest reads are cached by source size/mtime metadata, avoiding repeated ZIP opens and JSON parsing when the Client Integrity package is unchanged.
- SHA-256 streaming rents pooled buffers, and loose-directory package hashing reuses one I/O buffer across files to reduce allocation/GC pressure.
- Large assembly and Harmony reflection sweeps yield cooperatively, and Harmony reflection entry points are cached after first discovery.
- Protocol remains **13**; no wire-format change was required.

## Fingerprint caching

- Unchanged mod-package fingerprints and file SHA-256 values now remain cached for the lifetime of the Client Integrity process instead of expiring after 60 minutes.
- Every cache lookup still compares the current source size/mtime signature, so ordinary package/assembly changes invalidate cached evidence on the next use.
- Runtime, Harmony, protected-asset and policy evaluation are **not** frozen by this cache; only immutable file/package evidence is reused.
- Cache sizes are bounded (2,048 package fingerprints and 4,096 file hashes).
- Vintage Story `networkVersion` remains **1.1.21** for the existing mod-dependency compatibility label. The internal Client Integrity transport is **protocol 13**. It retains the authorized mod-package transfer handshake and adds mandatory server-baseline self-integrity plus randomized server-selected IL/type measurements for the Client Integrity DLL/package.

## Challenge handling and release identity

- Client Integrity uses semantic version **1.1.28**, internal protocol **13**, and a Vintage Story `networkVersion` pinned to **1.1.21** for the existing mod-dependency compatibility label.
- A deep scan no longer silently drops a newer challenge that arrives while hashing is in progress. The client retains only the latest pending challenge and runs it after the active report is sent, preventing unbounded queued work while reducing false timeout/retry failures.
- The client advertises **ChallengeQueueing**, **ModTransferConsent**, **ServerBaselineSelfIntegrity**, **RandomizedSelfAttestation**, and **SignedBuildManifest** capability markers. `ModTransferConsent` covers the per-request player authorization flow; `ServerBaselineSelfIntegrity` marks the exact DLL/package baseline evidence; `RandomizedSelfAttestation` answers fresh method/type probes selected by the server; `SignedBuildManifest` reports publisher-manifest identity when one is present.
- Server owners can optionally require clients to match the Client Integrity semantic version installed on the server. The Server Suite derives that requirement dynamically.

## Scan-time heartbeat

During a deep mod/package scan, the client sends a lightweight cached liveness heartbeat rather than re-running runtime/self-integrity/Harmony scanners on the heartbeat tick. This reduces contention on slower systems while preserving the deep scan itself.

## Player-authorized suspicious mod transfer

- Protocol 13 supports a bounded, administrator-requested transfer for one specific suspicious mod already present in the client's reported ModLoader inventory.
- Before any package bytes are read, archived, hashed for transfer, or uploaded, the player receives a popup naming the ModID/version, loaded package name, maximum size, scope, and server refusal policy.
- The authorization popup is created only after a WebPanel administrator manually clicks **Collect ZIP** for that player/mod; automatic scans and mismatches do not open it.
- **Agree & Transfer** sends a one-request authorization packet. The client then waits for the server to acknowledge that authorization before preparing or sending the mod package.
- **Decline**, closing the popup, or pressing Escape sends an explicit decline. In this server bundle, an explicit decline applies the server-configured policy: no automatic punishment, disconnect, or a configurable temporary ban. A technical popup failure or unanswered/expired request is not treated as an explicit decline.
- The server requests by ModID only; no arbitrary filesystem path can be supplied. Existing ZIP mods are transferred byte-for-byte. Directory/loose mods are copied into a temporary ZIP snapshot with symlink/junction traversal blocked.
- Uploads use bounded chunks, a server-provided maximum size and upload cap, timeout/cancellation, and final SHA-256 verification. Only one package upload is active at a time and temporary archives are deleted after completion or failure.
- This mechanism is limited to the requested ModLoader-reported mod source. It is not a general client-file browser. Server operators are responsible for publishing appropriate privacy/access terms and obtaining jurisdiction-specific legal advice; the popup by itself does not guarantee legally valid consent.

## Blacklist runtime signatures and DLL byte integrity

- Protocol 8 adds challenge-driven inspection of registered client hotkeys and client chat commands for configurable suspicious terminology. These name matches are low-confidence supporting evidence only.
- Blacklisted mod IDs, assembly names, stored release assemblies, and known namespace roots are sent separately as runtime aliases, producing dedicated blacklist-match findings instead of being mixed with generic hack-name matches.
- Client mod reports now include package byte size, DLL count, total uncompressed DLL bytes, and each loaded DLL's byte length; these values are covered by the report hash.
- Official ModDB blacklist resolutions store the downloaded release ZIP size and each contained DLL's exact uncompressed byte length alongside SHA-256, structural, and behavior fingerprints.
- Adds protected final-loaded asset hashing for configured texture/shader paths so altered visibility assets can be correlated with other findings.
- Adds same-ID/version canonical content comparison for server-shared code mods, using normalized content hashes so harmless ZIP recompression does not trigger a mismatch.
- Adds runtime-surface change tracking between deep scans and reports changes back to the server for correlation.
- Snapshotting of client API collections happens on the client main thread; hashing/matching runs off-thread to minimize frame stalls.
- Challenge nonces remain part of signed report hashing; the server still treats PacketGuard/server-authoritative behavior as stronger evidence because a fully hostile client process can forge user-space attestation.

## Integrity and runtime correlation

- Stable normalized ZIP/folder content hashing so harmless ZIP repacking does not evade a known content fingerprint.
- Structural assembly fingerprints: referenced assemblies, type/method/field shape, ModSystem type names, structure SHA-256 and 64-value MinHash similarity signature.
- Identifier-independent managed-IL behavior fingerprints: opcode-shape SHA-256 plus MinHash similarity, ignoring names, MVIDs, metadata-token operands, branch targets, and literal values.
- Server correlation can combine structural and IL-behavior matches to the same forbidden release as high-confidence repack evidence.
- Assembly-to-loaded-mod correlation from contained ModSystems and exact packaged DLL hashes.
- Runtime new-assembly correlation now marks assemblies that belong to loaded mods instead of treating every new assembly as unassociated.
- Runtime Harmony patch census using reflection only (no direct Harmony compile reference).
- Known `com.br3zzly.modmenu` Harmony owner detection.
- ModMenu-like patch-cluster detection across movement + reach + mining targets.
- Harmony patch-set digest in the 15-second heartbeat; server requests a fresh deep scan when the patch set changes after login.
- Existing self-integrity assembly/package fingerprint and challenge/nonce report hash remain in place.

The scanner only inspects Vintage Story runtime surfaces exposed through the game API: loaded mods/assemblies, Harmony patches, registered hotkeys/client commands, and configured loaded game assets. It does not inspect unrelated applications, arbitrary user files, or global keyboard activity.

## Build

Set `VINTAGE_STORY` to the **same Vintage Story 1.22.7 install directory that the client actually runs**, then:

```text
dotnet build NewfiesAntiCheatClient.csproj -c Release
```

Server Essentials from this same bundle and AntiCheat Client 1.1.28 must be deployed together.

## Startup integrity gate
The first protocol-7+ challenge is treated as the startup attestation by Server Essentials. It includes the already-loaded mod/assembly census, Client Integrity self fingerprint, runtime assembly state, Harmony patch census, registered hotkeys/client commands, and protected asset state before the server releases gated world interactions.
## Protocol-13 self-integrity, randomized attestation and authorization hardening

Client Integrity 1.1.28 uses protocol 13. It retains **ReportHashV2**, **BoundHeartbeat**, **ChallengeQueueing**, and **ModTransferConsent**, and adds **ServerBaselineSelfIntegrity**, **RandomizedSelfAttestation**, and signed-build-manifest support. Protocol 13 remains required because the server expects the extra self-integrity measurements in every accepted deep attestation and compares them against the exact companion loaded on the server; the 1.1.28 semantic bump does not change that wire protocol. Servers can also require every client to match the semantic Client Integrity version installed on that server.

### False-kick safeguards

- Exact DLL SHA-256, MVID, DLL-size and normalized package mismatches remain deterministic fail-closed checks.
- Missing/unreadable self-measurements are treated as operational failures and get three independent attempts before the server disconnects for verification-unavailable; they are not labeled as direct cheat evidence.
- Random IL/type probe failures are treated differently: the default policy requires **3 consecutive fresh randomized challenge failures** before disconnecting. A single failed probe set only causes a new independent challenge.
- Periodic heartbeat attestation changes request a fresh deep scan; the heartbeat mismatch itself does not kick the player.
- Server-side probe-catalog/reflection failures disable that randomized probe set for the challenge instead of penalizing clients.

### Optional signed build manifest

The `tools/` folder contains PowerShell 7 scripts to generate an offline RSA signing key and sign a compiled Client Integrity build. Keep the private key outside GitHub and outside all release/server mod packages. Put only the generated public-key Base64 into `ClientBuildManifestPublicKeyBase64`, then enable `RequireSignedClientBuildManifest` after the server startup log reports a verified manifest.

See the repository-level `SECURITY-NOTICE.md` for the defensive-use / AI-assistance request. That notice is documentation only and is not treated as a technical security boundary.



## OreShield client sync (1.1.28)

Client Integrity 1.1.28 provides the optional `OreShieldClientSync` capability on protocol 13. Server Suite remains authoritative for ore discovery. Network callbacks only enqueue already-authorized block IDs from the separate `newfiesoreshieldsync` channel; the client game-tick pump applies them through Vintage Story's `BulkBlockAccessor` + `Commit` path, verifies the resulting solid block ID, and then acknowledges stabilized delivery. If a chunk is not ready, the client defers application and may request a resync of already-authorized discoveries for that chunk. It cannot request undiscovered ore data. Current OreShield servers use this dedicated sync channel as the sole reveal transport; an unacknowledged batch is retried once and surfaced as a sync timeout.
