# Server Essentials Suite 1.2.53 — server protection additions

- Added combat attack-rate and solid-wall observations, with conservative handling of
  doors, chiseling, custom geometry, mounts, unloaded terrain and server stalls.
- Added optional exact-weapon timing rules and separate combat/inventory request budgets.
- Added inventory availability/open-state/ordinary-slot checks while preserving native
  item transactions, special inventories and normal crafting batches.
- New combat/inventory checks start in observation-only mode; optional action denial
  is separate from existing noclip and Block Nuker kick policies.
- Added short server-observed knockback allowances and visible hook status.
- Improved mining diagnostics and review-only handling of custom tools/foreign patches.
- Added rate-limited correction warnings, failed-recovery loop protection and targeted
  denied-action resynchronization with OreShield-safe transport.
- Added temporary scoped administrator exemptions with reasons, expiry and audit records.
- Added recent per-player event timelines, session-average occurrence rates and incident
  JSON exports containing evidence, settings and mod/build versions.
- Excluded missing placeholders from OreShield host selection.
- Fixed the panel showing stale PacketGuard settings immediately after a successful save.

Existing new noclip correction/decay, Block Nuker confirmation and occurrence-count
improvements from earlier 1.2.53 builds remain included. No other mod was changed.
