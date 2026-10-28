# Validation — Suite performance audit, 2026-10-28

Final Suite 1.2.56 DLL SHA-256:
`7543c94595f4a1826e1e8a6db00dacb77a327ceee16e5373af2d71c1719f350c`.
Paired optimizer: NOS 0.20.22-preview; its manifest records the exact DLL/package
hashes and executed suites.

## Passed

- Final Suite build: zero warnings/errors; source consistency and whitespace checks.
- All 23 final Suite/companion regression projects. This includes the original
  regression set plus ModuleAuditProbes, PerformanceAudit and OreShieldWorkTests.
- Log parser: 256 KiB per-pass bound with pooled buffers, UTF-8 code points split
  across reads, oversized-line rejection, exception-context carry and same-path
  larger file replacement. Before/after benchmark output hashes match.
- OreShield: native wire palette privacy, recipient/cache isolation, canonical
  packet immutability and the existing discovery/persistence tests. New send tests
  cover 64/129-cell same-chunk input, 200-cell mixed chunks, nonzero dimensions,
  128-entry caps, cell order, masked IDs and synchronous ACK-before-send-return.
- Additional discovery work tests include 100,000 randomized edits.
- All 39 final optimizer suites with real external compatibility fixtures supplied.
- Disposable official 1.22.7 server with all six Suite/companion mods, NOS,
  ARL 3.2.0, Walkable Entities 1.0.44, GeneLib 3.2.1 and a test-only seed privacy
  fixture. All five requested compatibility modules active; OreShield hooks
  installed; 11 existing seed privacy wire checks passed. HTTP/performance refresh
  and optimizer capture checks passed. Explicit save, clean exit 0, SQLite
  integrity `ok`, zero server errors.

## Measurement and reproduction

The release includes before/after measurements against Suite 1.2.55, raw results,
and the audit report. Measurements use the compiled production functions, warm
local files and synthetic native world/channel/ACK fixtures. They include fixture
reflection/proxy overhead and do not establish production TPS or lower retained RAM.

Set `VINTAGE_STORY` to the official 1.22.7 install and `NSS_ASSEMBLY` to either
compiled Suite DLL. Run:

```sh
DOTNET_TieredCompilation=0 NSS_PERF_ITERATIONS=200 dotnet run --project modules/suite/tests/runtime/performance-audit/PerformanceAudit.csproj -c Release
DOTNET_TieredCompilation=0 NSS_PERF_ITERATIONS=200 NSS_PERF_SYNC=1 dotnet run --project modules/suite/tests/runtime/oreshield/OreShieldPacketRuntimeSmoke.csproj -c Release
```

Run each version separately without competing build jobs when comparing timings.
Final correctness/optimizer tests were rerun after the binary and compatibility
pin were finalized. Preliminary measurements/build runs are not final validation.

No actual player clients, live configuration changes or production stall
reproduction. Private server logs/world files and the test-only native probe are
excluded from release mod ZIPs.
