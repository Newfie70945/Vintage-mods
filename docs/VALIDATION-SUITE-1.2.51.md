# Suite 1.2.51 runtime validation

Suite advances from released 1.2.50 to 1.2.51. Build suffix: `ms11-verification-worker1`. NOS remains the same 0.20.9-preview binary; no optimizer implementation changes were made in this version/validation pass.

## Completed checks

- Official Vintage Story 1.22.7, .NET 10; Suite build: zero warnings/errors.
- Production-method verification checks: failed atomic write and retry, concurrent updates, one coalesced worker, final shutdown flush, 40-session staggered scheduling, arrivals during refresh, failed callback registration and shutdown no-ops.
- Consequence regression checks: kick deduplication, stronger ban, expiry, failed action and retry.
- Disposable web-panel server: unauthenticated access rejected, owner login, dashboard/status JSON, 32 header requests over 16 workers, shared snapshot caching. All expected responses passed; maximum header request 6.10 ms on the otherwise empty test server. Clean exit and save integrity passed.
- The web test runner now sends `/stop` and verifies exit status/save integrity; its old bare `stop` was interpreted as chat.

## Heavy-mod comparison

Two separate disposable runs loaded the same saved template and 23 selected mod ZIPs plus Suite, NOS and the workload fixture (29 enabled mods including the base game modules). Steep & Brew 0.7.1 was included. Each run spawned 3,000 native animals with AI/physics, 500 mismatch-input barrels and 500 firepits (250 burning, 250 cold), then measured a 180-second window. Other optimization settings were identical. NOS capture was stopped; native profile checks ran outside the steady window. No compilation or other server test ran alongside those windows.

| Metric | Barrel guard off | Barrel guard on |
| --- | ---: | ---: |
| Average tick | 45.89 ms | 46.66 ms |
| p95 tick | 58 ms | 59 ms |
| p99 tick | 66 ms | 69 ms |
| Worst tick | 80 ms | 96 ms |
| Allocations | 52.74 MiB/s | 48.02 MiB/s |
| Process CPU (100% = one core) | 115.70% | 115.06% |
| Peak RSS in steady window | 2037.91 MiB | 1994.70 MiB |
| End managed heap | 1388.67 MiB | 1390.94 MiB |
| Median explicit save preparation | 65.03 ms | 76.97 ms |

The candidate reported the barrel guard active alongside Steep & Brew and executed 28,387,500 count rejections during the recorded post-startup workload, including phases outside the steady window. Both runs completed five measured explicit saves, profiler cancellation checks, forced chunk unloading, clean shutdown and SQLite integrity checks, with no logged errors.

**Interpretation:** allocation fell about 9%, but measured tick latency did not improve. One sequential pair cannot distinguish small differences from run variance. This is not an overall TPS improvement result; the barrel guard remains opt-in.

## Limits

No authenticated clients or bots; no live server or production world. This does not reproduce crafting XP, successful fermentation, the owner's full modpack, live-player interactions, or long-term memory retention. The cached XSkills fixture is 1.1.9, whereas the uploaded production listing was 1.1.8. Lab limits: 2 vCPU and 8 GiB; no live deployment recommendation follows from this short synthetic test alone. The Anti-Cheat verification changes are covered by production-method tests; the NPC workload does not simulate client attestation traffic.

[Sanitized metrics and exact artifact SHA-256 hashes](research/suite-1.2.51-runtime-tests.json). Private logs, worlds, credentials, game binaries and third-party mod DLLs are excluded from release source.
