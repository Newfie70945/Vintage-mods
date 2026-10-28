# Seed privacy preview validation — 2026-10-28

Suite 1.2.54 (`ms11-seed-privacy1`), NOS 0.20.20-preview, official Vintage Story
1.22.7 / .NET 10. Native tests use isolated copied worlds with no player clients.

## Passed

- Suite build and native probe build: zero warnings/errors.
- All 20 Suite/companion regression projects, including 125 dedicated seed privacy
  checks: signed seeds/region coordinates, all three weather RNG fields, immutable
  identification/weather/region copies, unrelated/unknown protobuf fields,
  malformed payload bounds and native serializer transport rejection without
  raw data or stale-buffer reuse.
- All 39 optimizer build suites, with external ARL 3.2.0, Walkable Entities 1.0.44,
  GeneLib 3.2.1 and the current Suite DLL supplied to compatibility checks.
- Native enabled run: 11 identification/weather/region wire and immutability
  assertions. All six Suite/companion mods, NOS, the three external mods and the
  test-only probe loaded. All five requested compatibility modules active with
  OreShield hooks installed. Explicit save and clean exit, SQLite integrity `ok`,
  zero server errors. HTTP performance refresh, bounded header request concurrency
  and optimizer phase/lifecycle captures also passed.
- Native default-off run: normal configuration remained disabled, no privacy
  activation, all ten normal mods loaded, all five compatibility modules active,
  explicit save and clean exit, SQLite integrity `ok`, zero server errors.
- Source consistency and whitespace checks.

Suite DLL SHA-256:
`2917fb3b2955e528904a886bb4d2926fb6c3e3632ef25d5caccf6fc22fe961b3`.
NOS DLL SHA-256:
`8b25a5b0a6358a6376be5afbdf4749906347dd37bdc5220d9b75c4ec51bb233a`.
Documentation packaging was refreshed after tests without changing these binaries.

The first native attempt activated privacy but stopped at the game's intact
400 MB free-space guard before completing the probe. After freeing a stopped
disposable test copy, both native runs above completed. No guard was disabled.

## Not established

Real-client weather/calendar/visual compatibility, multiplayer overhead and
protection against arbitrary mod leaks, already learned seeds or terrain-based
seed inference. The default-off preview must be trialled with actual clients on a
copied world before broader deployment. This work does not fix the production
stalls or the native profiler's long-marker overflow.

Release downloads include sanitized JSON results. Private console logs and world
files are excluded; no real seed values or test credentials are published.
