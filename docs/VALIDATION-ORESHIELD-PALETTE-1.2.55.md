# Validation — OreShield palette audit, 2026-10-28

## Reproduced defect and fix

The new concealed-only mineral fixture failed against Suite 1.2.54's compiled DLL:
`Concealed-only ore variant survived in the outgoing wire palette.`
The decoded block was already host rock; the failure inspected the native
decompressed palette. This establishes a chunk-level mineral-variant disclosure,
not a recovery of exact coordinates or a reproduced client-report bypass.

Against Suite 1.2.55, the full OreShield packet runtime project passes, including
palette exclusion, preservation of legitimate ore, identical block bytes for two
concealed minerals with the same host, recipient/cache isolation, real patched
native collectChunk, canonical immutability, dimensions, persistence, native break
hooks, bounded reveal jobs and uniform masked denied-action corrections.

An intermediate new fixture changed the shared cache before an existing
reference-reuse assertion. Moving that fixture after the cache assertion resolved
the test interference; the full focused project then passed without another
product change. The validation records retain that initial outcome and rerun.

## Other checks passed

- Suite build: zero warnings/errors; source consistency and whitespace checks.
- All 20 final Suite/companion regression project outcomes, including compiled
  ingress, action checks, collision evidence, packet settings and seed privacy.
- Additional OreShield work/index tests, including 100,000 randomized discovery
  edits, bounded visibility work and nearest-first sweep continuation.
- All 39 optimizer suites with actual external compatibility fixtures supplied.
- Disposable official 1.22.7 server with all six Suite/companion mods, NOS,
  ARL 3.2.0, Walkable Entities 1.0.44 and GeneLib 3.2.1, plus a test-only seed
  privacy probe. All five requested compatibility modules active, OreShield hooks
  installed, 11 seed privacy wire checks passed, HTTP/performance checks passed,
  explicit save completed, clean exit 0, SQLite integrity `ok`, zero server errors.

The seed privacy integration verifies that the existing serializer hook coexists
with the updated modpack; it is separate from the focused chunk-palette regression.

Suite DLL SHA-256:
`50ab5f35dca8e120334016061a188a5431284cabbe9e38c39ba2e789c7d7d3f8`.
The paired NOS build manifest records its exact binary hash and executed suites.

## Limits

No real player clients or production TPS benchmark. Source defaults were reviewed;
the live server's configuration was not accessed or changed. No arbitrary mod
channel/ModData audit or guarantee that all modded ores can be concealed. No
unbypassable client attestation claim, automated punishment policy changes or
production-stall fix. Private logs/worlds and test-only probe are excluded from
release mod ZIPs.
