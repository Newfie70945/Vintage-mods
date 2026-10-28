# Suite 1.2.54 / NOS 0.20.20-preview

Changes since Suite 1.2.53 and NOS 0.20.19-preview. This is an opt-in client
compatibility preview; the feature is disabled by default.

## Server Essentials Suite

- Add restart-only seed privacy configuration independent of anti-cheat toggles.
- Replace the outgoing native identification seed, native weather RNG seed fields
  and map-region `weatherState` RNG seed fields using a stable public seed.
- Preserve the real server seed, canonical packet objects, saved weather data and
  unrelated protobuf fields. No saved world migration is required.
- Bound protobuf parsing and reject malformed sensitive packets before sending;
  never fall back to raw seed data or an old reusable send buffer.
- Add privileged `/seedprivacy` status and counters without displaying real seeds.
- Refuse activation on unsupported native versions, missing weather mappings,
  equal public/real seed or an existing foreign serializer patch. A refusal warns
  and leaves the server running without privacy; administrators must check status.
- Add packet, transport and native-server regression fixtures and setup guidance.

## Newfie's Server Optimizer

- Extend the exact-binary OreShield patch compatibility rule to Suite 1.2.54.
  Retain the previously audited Suite 1.2.53 rule and existing foreign-hook guards.
- No new optimization defaults, module behavior or seed-packet processing.

Other companion mods are unchanged. This update does not fix the native profiler's
long-marker overflow, add Scaffolding compatibility, or claim a solution to the
large production stalls identified in the separate log investigation.

Read [seed privacy setup and limits](../modules/suite/guides/SEED-PRIVACY.md).
Already disclosed seeds and terrain-based inference remain outside this feature's
protection. Real-client weather/calendar compatibility still requires field testing.
