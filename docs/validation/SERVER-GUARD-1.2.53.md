# Suite 1.2.53 / ms11-serverguard1 — server-side evidence safeguards

Follow-up to main `8eb44fa`. This is a Suite-only change; client protocol, optimizer and companion binaries are unchanged. No packet caps, reach tolerances or feature enablement defaults are changed.

## Changes

- Flood enforcement evidence counts observations spaced by at least 100 ms, requiring at least 10 observations over at least three seconds. Thousands of packets in one drained burst cannot supply all the evidence for a later sparse tail. Quiet gaps, clock regressions and unreliable timing still reset the window. Excess packets are still dropped at the existing token-bucket limits regardless of punishment eligibility.
- Added constant-size per-budget accepted/drop counters and rate/burst/token details to rate-limited findings. Formatting runs only after the finding cooldown, not for every discarded packet. Health/queue-delay checks still determine whether findings are review-only.
- Queued ingress findings retain the observed player reference. Replaced sessions are discarded; pre-authentication observations remain anonymous instead of being assigned to a subsequently authenticated player.
- Reach reconciliation accepts only non-future samples within the configured rewind window, matching both current entity and dimension. This preserves existing selection-box checks, latency allowances and right-click review-only behavior.
- Unmounted unsupported-flight review remains advisory-only: no risk score or automatic punishment. Confirmation now resets on entity/dimension changes. Future/stale position timestamps and invalid/oversized geometry cannot provide eligibility.
- Noclip checks require healthy/ready timing and a recent position. Unknown terrain, invalid/oversized/throwing collision geometry and oversized body probes are insufficient evidence. Probes never request/load terrain.
- Path checks require known clear endpoints, so exiting an existing overlap is not a new wall crossing. The whole sampled path must remain known, even after enough collisions were seen earlier in the segment.
- Endpoint overlap still requires configured penetration, independent samples and net movement, plus at least 500 ms elapsed. Same-time samples, stale gaps and dimension changes reset the confirmation window.
- Noclip with correction disabled is now explicitly review-only: no added noclip movement risk or automatic consequence eligibility. Legacy collision findings lacking fresh eligibility metadata remain review-only after upgrade.
- Collision finding text no longer claims a successful rollback when corrections may be disabled; evidence records the configured correction policy.

## Use and limits

Existing PacketGuard settings apply. No new automatic detector or consequence is enabled. Unsupported flight is `PACKET_FLIGHT_REVIEW`, deliberately review-only because legitimate modded physics can resemble flight. Noclip remains opt-in; during modpack validation, use correction disabled and review observations first. Geometry intersections and elapsed observations do not prove intent.

This does not prove a client is clean, enumerate hidden mods, implement a new inventory transaction engine, or guarantee detection of all slow/oscillating flight or noclip. Native movement and mod compatibility remain relevant. The reach sampler still reconciles server-observed positions rather than a fully authoritative physics simulation.

## Validation

Official VS 1.22.7 / .NET 10. Production-policy tests cover burst/sparse-tail isolation, continued sustained flood detection, unchanged budget decisions, invariant evidence formatting, rewind clock/entity/dimension boundaries, flight identity resets and fresh/healthy movement eligibility. Compiled ingress tests exercise native inventory/channel checks and delayed callbacks with replaced/anonymous sessions. Existing settings/browser tests cover unchanged configuration round-trips and read-only controls.

Sixteen compiled collision/flight fixtures cover loaded wall detection, exit from overlap, unknown later path terrain, unavailable chunks without block reads, throwing/non-finite geometry, oversized body bounds, unsupported airborne space, nearby support, unknown support, and endpoint clock/gap/dimension resets. The fixture exposes no chunk loader. These are correctness tests; there is no production false-positive-rate or TPS claim and no authenticated multiplayer cheat test.
