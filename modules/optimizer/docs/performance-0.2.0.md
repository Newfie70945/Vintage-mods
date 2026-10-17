# 0.2.0 component performance evidence

The official 1.22.7 methods were called before/after Harmony injection, with a stock run, prefix-disabled run and configured candidate run. Five repetitions were taken. These are **component microbenchmarks**, not authenticated players, a running game workload, server tick-time gains or a comparison with Stratum. No player-capacity claim is supported.

Raw measurements are in `research/components-0.2.0/`. Numbers below are medians across the five repetitions; ranges/individual trials remain available. Background runtime/JIT effects and fixtures limit generality.

## Player queries at 2,000 fixture clients, none pending

| Method | Stock ns/call | Candidate ns/call | Stock bytes/call | Candidate bytes/call |
| --- | ---: | ---: | ---: | ---: |
| online-snapshot | 13275.3 | 9896.5 | 16168 | 16056 |
| nearby-all | 39680.9 | 38497.1 | 49064 | 16056 |
| nearby-half | 57078.7 | 54125.4 | 24656 | 8056 |

Snapshot output arrays still allocate. The reduction is iterator/intermediate overhead, not elimination of all allocations. Nearby reuse removes temporary-list churn but must still return caller-owned arrays.

Nearby-query CPU results were mixed: for example, the 200-client all-results case regressed even though allocation fell. Consequently **nearby-query reuse is OFF by default**. If explicitly enabled, population gating keeps the original method unpatched below 128 connected clients and hysteresis limits activation churn. This is a deliberate evidence-based exclusion from the default optimization set.

## Bulk stream reads

| Read size | Stock ns/call | Candidate ns/call |
| --- | ---: | ---: |
| 256 bytes | 237.76 | 17.76 |
| 1,024 bytes | 885.01 | 27.60 |
| 4,096 bytes | 3527.31 | 61.28 |
| 65,536 bytes | 54964.44 | 1199.30 |

This compares the inspected byte loop with a bulk memory copy on warm, valid, non-aliased buffers. Both paths allocate zero per read. It does not imply the same ratio for compression, networking, chunk delivery or overall server work. Reads below 256 bytes and unusual/invalid/aliased layouts retain the original body; retained Harmony dispatch overhead is represented by prefix-disabled measurements.

## Correctness and runtime scope

Differential tests compare actual original and patched results, ordering, mutation effects, callbacks, exceptions and stream state. Module tests cover switches, population activation/deactivation, foreign patches, monitor failure rollback and disposal. Headless server checks cover activation, profile/export, clean stop and world save integrity, not sustained multiplayer performance.

The next production comparison should use a stopped clone of a representative world, the same official server/hardware, real authorised clients or a validated load tool, workload phases for travel/combat/building, repeated matched trials, warmup, MSPT P95/P99, CPU, allocations, GC pause, retained-memory floor, queue pressure and correctness evidence. Test each default module individually before a combined run.
