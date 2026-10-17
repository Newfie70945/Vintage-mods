# Runtime compatibility checks

`packet-settings/PacketSettingsSmoke.csproj` loads the compiled Suite and checks
28 PacketPage1/Perf4 settings through production save/config/snapshot methods using API
doubles, including legacy/Shift+L preservation, bounds and failed persistence.
It creates no game world or server.

The Perf3 follow-up audit runner at `audit-followup/AuditFollowup.csproj` now checks
repairs: native hotbar/backpack indices (including an occupied special slot),
custom-channel index rejection and stable confidence for duplicate reports.
The archived audit documents the earlier reproduction results.

These tests load the official Vintage Story 1.22.7 libraries and a compiled Suite
assembly. They create no game world or server instance. The web-hardening
runner opens temporary loopback listeners; the other runners open no listeners. Game binaries are supplied by the developer's installation; none are
included here.

Build the Suite first, then set `VINTAGE_STORY` to the installation directory and
`NSS_ASSEMBLY` to the full path of the resulting `NewfiesServerEssentialsSuite.dll`.
Run these commands from the `ServerEssentials-1.2.43` directory:

```sh
export VINTAGE_STORY=/path/to/vintagestory
dotnet build -c Release
export NSS_ASSEMBLY="$PWD/bin/Release/net10.0/NewfiesServerEssentialsSuite.dll"
dotnet run --project tests/runtime/ingress/IngressRuntimeSmoke.csproj -c Release
dotnet run --project tests/runtime/cache-timeout/CacheTimeoutRuntimeSmoke.csproj -c Release
dotnet run --project tests/runtime/advisory/AdvisoryRuntimeSmoke.csproj -c Release
dotnet run --project tests/runtime/oreshield/OreShieldPacketRuntimeSmoke.csproj -c Release
dotnet run --project tests/runtime/package/PackageRuntimeSmoke.csproj -c Release -- /path/to/NativeFix1-Suite.zip
```

PowerShell uses `$env:VINTAGE_STORY` and `$env:NSS_ASSEMBLY` for the same paths.

The package runner takes a Suite ZIP directly (no `NSS_ASSEMBLY` required). It
uses the official mod container to unpack/validate the ZIP, then loads the
extracted Suite and performs an in-memory SQLite create/insert/read using its
packaged native runtime. An optional second ZIP argument must be the original
Revamp1 archive: its `runtimes/` DLL layout must reproduce the loader rejection.

The ingress runner verifies the runtime's private field accessors and builds all
five Harmony hooks, including TCP/UDP parser finalizers. It checks valid byte
arrays and valid incoming UDP frames, rejects impossible small length declarations
before the vanilla allocator, confirms parser context cleanup, and removes its
patches before exiting. Malformed test declarations remain small even if a guard
regresses.

The OreShield runner patches the real `ServerSystemSendChunks.collectChunk` with
the production hook, builds a real compressed chunk fixture, and round-trips its
wire packet. It checks independent recipient masking, discovered and placed ore,
canonical immutability, cache reuse, both recipient orders, repeated/nested sends
and cache-disabled behavior. It uses API doubles and no running server/world.
Point `NSS_ASSEMBLY` at the original NativeFix1 DLL to reproduce the leak. The
provided working 1.2.36 DLL uses a legacy reference branch checking core ore
concealment; its older shared-packet implementation does not claim the new
recipient isolation guarantees.

The UDP serializer test also rejects an absent custom-channel body and verifies
context cleanup. The advisory runner exercises compiled risk, incident and
finding-code consequence paths: flight-review findings are excluded, while a
strong movement finding still participates normally.

The cache timeout runner uses API dispatch proxies to hold the production web
service's queued callbacks. It verifies that an expired cached read does no work,
its replacement executes once, and ordinary mutation callbacks preserve their
completion behavior after a request timeout. It takes approximately 16 seconds
because it exercises the actual eight-second timeouts.

The `webmap/WebMapRuntimeSmoke.csproj` runner checks config defaults/bounds, cached public player privacy gates and game-thread listener replacement/coalescing with API doubles. `node tests/web/test_map_player_refresh.cjs` runs the real embedded map in Chromium with HTTP fixtures.

Run `dotnet run --project tests/runtime/profiler-layout/ProfilerLayoutRuntimeSmoke.csproj -c Release` with the same environment variables to render profiler text cells through official GUI/Cairo libraries offscreen and verify clipping at four GUI scales. No GL context or game client is created.


The 1.2.38 `web-hardening/WebHardeningRuntimeSmoke.csproj` runner uses temporary
loopback TCP sockets to exercise the compiled HTTP parser. It checks fixed-length
requests, malformed/duplicate headers, Transfer-Encoding rejection and header/body
limits. It also checks early rejection of oversized credentials and consistent
Suite DLL/browser/backend release identity. No game world or public listener is
created. Run with the same `VINTAGE_STORY` and `NSS_ASSEMBLY` variables:

```sh
dotnet run --project tests/runtime/web-hardening/WebHardeningRuntimeSmoke.csproj -c Release
```


Perf3 adds compact discovery/index/revision tests to the OreShield runtime runner:
16,386 changed chunks, targeted invalidation among 1,001 retained players and
reconnect/index cleanup. Browser fixtures assemble the same embedded MOTD module
and release placeholders as the compiled page.

Additional production-source checks (no live world):

```sh
dotnet run --project tests/commands/RecoveryCommandTests.csproj -c Release
dotnet run --project tests/recovery-performance/RecoveryPerformanceTests.csproj -c Release
dotnet run --project tests/oreshield/OreShieldWorkTests.csproj -c Release -- --benchmark
python3 tests/test_recovery_sql.py
python3 tests/test_recovery_sql_equivalence.py
python3 tests/benchmark_recovery_sql.py --output benchmark.json
```

The native recovery reader tests use packaged SQLite 3.53.3 and exercise admission,
deadlines, cancellation (including the pre-statement race), retry and shutdown.
Command doubles pump queued game-thread callbacks while real journal files are
written; they check durable receipt presence, occupancy, live edits, write failures
and cancellation. Benchmark output is synthetic allocation/query evidence only.
For a before/after query benchmark, pass `--baseline /path/to/pre-Perf3/LogStorageService.cs`.


Perf4 adds `perf4/Perf4Smoke.csproj`: production cache invalidation/128-key LRU,
bounded audit strings, live speed risk and real disconnect callback, bounded
worker evidence I/O and immutable packet ownership, and disk-indexed receipt
append/restart/corruption checks. It creates no game world or public listener.

The OreShield runner additionally builds the native 1.22.7 BreakBlock Harmony
hooks and checks changed-versus-unchanged mod break completion, exception
preservation, cancellation coalescing, compact reload ownership, placed-ore
shard migration/removal/retry and the bounded geometry queue. The timeout runner
also checks direct GET abandonment and the exclusive lease's actual work lifetime.

Generator1 adds `patch-generator/PatchGeneratorSmoke.csproj` for production loaded-asset inspection, array recipes, previews, generated files/ZIPs and failed-save rollback. Run `node tests/web/test_patch_generator.cjs` for the guided UI and daily-average chart. The OreShield runner also exercises diagonal/mixed-grade traversal, full mineral isolation and configured caps. API doubles only; no live world.

## Player Commands panel/settings

`player-command-settings/PlayerCommandSettingsSmoke.csproj` exercises production saves with an empty API fixture, checks omitted versus explicitly supplied warp defaults and exports the actual compiled dashboard HTML when given an output path. Use `tests/web/player-commands/browser.cjs` with that HTML to test desktop/mobile draft, validation, ownership, role-policy and permissions interactions. See `VALIDATION-PLAYERPANEL1.md` at the source root.

### MapMemory1 startup terrain export

`map-memory/MapMemoryChecks.csproj` uses an actual temporary save DB and the official game chunk decoder to verify terrain-only reads, exact block/fluid preservation, bounded private pools, disposal, successful parallel export and incremental reuse, and preservation of existing tiles/dirty work on failure. Pass the compiled Suite DLL and set `VINTAGE_STORY` to the 1.22.7 install directory.
