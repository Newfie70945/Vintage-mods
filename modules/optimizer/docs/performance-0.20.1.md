# NOS 0.20.1 preview — compatibility fix and measured results

The barrel ingredient pairing fast path now follows the official final count check when a collectible hook replaces the recipe ingredient array during matching. A native-versus-patched regression test reproduced a different result before the fix. No scheduling defaults changed.

## Official 1.22.7 server comparisons

Tests used NOS 0.20.0 with switches matching the uploaded 0.18.1 capture, Suite 1.2.49 and 25 local mod ZIPs. Stock means NOS engine switches disabled with the same Suite and probe. These measurements precede the narrow 0.20.1 correctness fix.

| Workload | Stock | NOS |
|---|---:|---:|
| 128 active animals, allocation MiB/s | 3.16 | 2.46 |
| 128 animals, mean tick ms | 1.39 | 1.44 |
| 128 animals, P95/P99 ms | 2 / 4 | 2 / 4 |
| 1,024 active animals, allocation MiB/s | 21.24 | 15.32 |
| 1,024 animals, mean tick ms | 13.13 | 13.63 |
| 1,024 animals, P95/P99 ms | 18 / 23 | 20 / 24 |
| 1,024 animals, CPU percent of one core | 43.52 | 45.27 |
| 1,024 animals, peak RSS MiB | 1507 | 1504 |

Four 128-animal arms ran stock/NOS/NOS/stock, each with a 120-second steady window. The 1,024-animal comparison used one arm per mode and is exploratory. All six arms passed their save and SQLite integrity checks. NPC movement and AI execution were checked; these are native NPCs, not authenticated players. The selected 25-mod pack does not reproduce the 222-mod production environment, crafting/XP traffic or player networking.

Allocation improved, but tick latency and CPU did not. Generation in the four-arm test was slower with NOS (10.87 versus 10.16 seconds); the single stress pair reversed that (10.05 versus 10.22 seconds). No general world-generation speedup is established. Dirty-save times were approximately unchanged. GC pause rate was also approximately unchanged.

After forced unload and test-only compact GC, the fixture retained 512 tracked chunks with stock save snapshots versus 6–8 with NOS snapshot release. This demonstrates reduced references in this fixture, not elimination of production memory growth. Forced collections are test operations, not enabled optimizer behavior.

Paired profiler windows increased measured tick time and allocations in this low-load fixture. Profiling is not free; these tests do not establish that it caused the production multi-second stalls.

The production capture reports foreign Harmony conflicts for idle firepit and asset compression. Those modules must remain in fallback; do not bypass conflict checks. Zero failure counters do not prove semantic compatibility or explain the largest production stalls.
