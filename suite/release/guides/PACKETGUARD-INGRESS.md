# PacketGuard ingress hardening (config schema 61)

This pass validates ordinary Vintage Story packets on the server. It works without a client mod report and does not assume that a modified game will describe itself honestly. The internal packet hooks are verified against the official **Vintage Story 1.22.7** server. Other versions show ingress protection as unavailable; the existing movement/reach/mining sampler continues independently.

Check **Anti-Cheat → Packet Protection** in Shift+L or the WebPanel snapshot for the live `IngressActive` / `IngressStatus` values. Enabled configuration alone does not prove that the required hooks installed. Startup reports the installed hook count (five targets).

## What is guarded

- **TCP before deserialization:** frames larger than `PacketGuardIngressMaxPacketKiB` (default 2048 KiB) are dropped before generated protobuf parsing and the main-thread packet queue. A token bucket bounds parsing/queueing work (300 packets/second, burst 900 by default).
- **TCP and native UDP byte-array allocation:** the vanilla byte reader allocates the declared array length before discovering a truncated payload. The scoped guard checks that declaration against both the configured cap and the actual remaining input before allocation. This also covers UDP parsing before the engine authenticates the endpoint. Invalid TCP arrays follow the engine's normal invalid-packet disconnect path.
- **View distance before dispatch:** both initial identification and later mode/settings requests are capped at the smaller of 1024 blocks and the server's configured chunk radius converted to blocks. A million-block request is normalized before view state or chunk work changes. Normal client preferences above a server's lower cap are clamped without a cheating finding; pathological values above 4096 or below zero create evidence.
- **Settings identity:** an ordinary player cannot target another player's settings. This prevents the vanilla handler's early view-distance assignment from modifying another player before privilege checks. Vanilla privileges still govern game-mode, fly, noclip, speed and picking-range changes.
- **World actions:** packet modes, registered block IDs, faces, selection IDs/hits, world coordinates, dimensions, loaded terrain and a generous hard interaction bound are checked before block/decor/block-entity handlers. The server's held inventory remains the authority for placement. Unloaded targets and stale dimension packets during a verified transition are discarded without cheating findings.
- **Hand interactions:** block-use routing is checked using the actual network interaction enum, even if a forged `UseType` claims a held-item action. Actual inventory slot bounds and the hand-use catch-up delta are checked before vanilla indexing or loops. A catch-up delta above 512 is rejected; long valid held actions retain their absolute counter.
- **Movement:** native UDP and TCP-fallback player positions must identify the authenticated player's entity and contain coordinates within the addressable range. Position updates have a separate bounded budget before position application/broadcast. Existing movement, speed, acceleration, reach and mining detectors retain their server transition and TPS protections.

Block/action and settings traffic use separate token buckets (60/second with burst 180, and 2/second with burst 8). No per-packet history is retained by these buckets. Findings are coalesced per connection/code (ten seconds by default) before they enter the main-thread history/consequence service. Raw client payloads, authentication tokens and claimed block names are not logged.

## Attribution and compatibility

Vanilla decor removal (`Mode=2`) bypasses ordinary `BreakBlock` / `DidBreakBlock` callbacks. Accepted ingress requests now produce attributed `[PacketGuard/Decor]` audit records with player UID, target, face and `pending-vanilla-claim-check`. These are **request audit records**, not confirmation that a decor was removed, and are not fabricated full-block rollback rows.

Schema 61 turns off the old default `PacketGuardUnverifiedTeleportReconciliationEnabled`. That default accepted arbitrary jumps of at least 128 blocks without correction. Verified server/mod teleport hooks remain active. Operators who explicitly opt back into reconciliation after migration accept that weaker protection for their compatibility needs.

Admins and creative players remain subject to resource and malformed-protocol bounds. Movement enforcement retains its existing exemptions. Mods that intentionally send distant block-entity actions, very large custom packets, large hand-use catch-up batches or high packet rates may need operator review and documented configuration changes. Never interpret a failed hook installation as active ingress coverage.

## Limits and validation

### Advisory flight review

`PacketGuardFlightReviewEnabled` defaults to true, with a six-second confirmation window and a 250 ms sample interval. Sustained unsupported, non-falling hover/ascent can create `PACKET_FLIGHT_REVIEW`. The server inspects at most 72 nearby loaded block cells per player sample and never loads terrain for this check. Nearby collision support, liquids/climbable blocks, ordinary falling, mounts, authorized free-movement/noclip, trusted movement transitions, stale position updates and degraded/unknown TPS reset or exclude the observation. Oversized custom collision volumes and failed collision queries are excluded.

This is staff review context. Flight review has **zero risk points**, contributes no incident evidence, cannot match a finding-code consequence, and does not invoke automatic consequences or player warnings. Duration, rise, sampled position, dimension and exclusions accompany the finding. Custom physics or flying entities can still require manual interpretation. Review settings are available in the JSON configuration; this pass does not add flight toggles to the GUI.

### Remaining limits

The supplied exploit client and an attack capture were not available. A vanilla block-place packet cannot register a new block name: placement uses the server's registered block and held inventory. The reported invisible/slur-named changes therefore require investigation of block entities, item attributes, custom mod handlers, or server content. This pass bounds and attributes applicable ingress paths; it does not claim to identify every malicious custom handler or every flight technique.

The frame cap applies after the network library has allocated its socket buffer. It protects downstream parsing/queueing, not the entire network stack. Native UDP's existing transport frame cap remains the engine's responsibility; the new scoped reader prevents declared-array allocation amplification inside that frame.

Run the production policy tests with `dotnet run --project tests/packetguard/PacketIngressPolicyTests.csproj`. The runtime tests under `tests/runtime/ingress` load the official assemblies to verify real Harmony targets/field access and safe valid/corrupt byte-array fixtures without launching a server. Complete a staged server check with normal building/mining, inventory interactions, initial/revised view distance, decor removal, teleports, mounted movement and the server's installed mod set before production deployment.
