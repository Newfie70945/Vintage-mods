# NOS 0.20.5 preview — inventory compatibility and intensive laboratory

The barrel ingredient pairing replacement now falls back before reading custom inventory-slot accessors. A custom Empty getter can replace the recipe ingredient array during native occupancy counting; the previous replacement captured ingredients earlier and could return a different match. A native-versus-patched regression reproduced that discrepancy before the fix. Exact native ItemSlot, ItemSlotLiquidOnly and ItemSlotBarrelInput retain the fast path. Native getter inheritance is verified, and Empty/Itemstack getters now participate in foreign-Harmony conflict checks. No recipe results are cached and no inventory contents are invented or overwritten.

All 29 automated validation suites passed, including 100 block callback checks. Suite remains 1.2.50. Existing switches and defaults remain unchanged.

## Intensive test setup

Official 1.22.7, Suite 1.2.50 and NOS 0.20.5 run on independent copies of the same stopped world. The selected heavy pack contains 22 cached mod ZIPs plus Suite, NOS and a disposable laboratory fixture. The owner PC ModsByServer folder was not attached: this is not a reproduction of the full production pack.

The workload creates 3,000 native animals, equally split across pig, sheep, goat and deer groups; 500 barrels with unmatched rock input; and 500 firepits initialized half burning and half cold. NPCs are fixture-tagged and forced active/tracked to run normal AI and physics without connected clients. No authenticated bots, player networking or real-player capacity are simulated.

The barrel pair compares only BarrelMatchCountGuard off/on, with 180-second steady windows. The cached-collision pair separately compares CachedCollisionGenerationReuse off/on, with 120-second steady windows and reversed arm order. Capture windows are outside steady timing. The lab has a 2-vCPU quota and 8-GiB limit; the copied magic numbers use one native physics and one worldgen thread. These are controlled experiments, not production tuning recommendations.

The populated world is backed up consistently through SQLite after a normal save and before forced-unload retention checks. The local launcher reloads existing tagged animals rather than spawning replacements. The fixture is development-only and must not be installed on a production world. Official game binaries are not redistributed.

Successful fermentation, active cooking recipes, 25 real clients, client rendering and the exact production modpack require separate tests. World/NPC randomness and shared-host scheduling remain confounders. No general production speedup is inferred from a single pair.

Two initial fixture attempts are excluded from comparisons: one completed timing but hit the native spawn setter's null default-spawn value during reusable-world preparation; the next caught a fixture field/property mismatch in an early check. Neither is counted as a passing performance result.

## Actual absent-versus-installed result

The owner reports the live server seems better without NOS, Synergy or Tungsten. The lab therefore added a full NOS-absent comparison, not just disabled switches. Both arms kept NOS telemetry stopped, verified actual mod presence/absence and used the same population/setup. Both passed activity/callback checks, normal saves and SQLite integrity checks.

| Metric | NOS absent | NOS installed, reference settings |
|---|---:|---:|
| Mean tick | 49.38 ms | 49.46 ms |
| P95 / P99 | 62 / 73 ms | 62 / 73 ms |
| CPU, percent of one core | 114.59 | 115.67 |
| Allocation | 61.72 MiB/s | 49.85 MiB/s |
| GC pause rate | 17.61 ms/s | 16.43 ms/s |
| Peak RSS | 1759 MiB | 1749 MiB |

This demonstrates allocation reduction in the fixture, not a meaningful tick-time benefit. CPU was slightly higher with NOS; the difference is not established as causal by one pair. The lab cannot dismiss or explain the owner's reported regression: exact mod files/configuration, real-player networking and comparable production workloads are still needed. Keep NOS disabled on that live server while investigating; these results do not justify recommending re-enabling it.

## Separate option comparisons

These pairs ran lightweight NOS capture in both arms; do not compare their absolute numbers with the capture-stopped presence pair. Each toggled one option, without changing other settings. No production defaults changed.

| Option | Mean tick off/on | P95 off/on | P99 off/on | Allocation off/on | CPU off/on, one-core % |
|---|---:|---:|---:|---:|---:|
| Barrel count guard | 50.03 / 48.01 ms | 63 / 61 ms | 74 / 73 ms | 49.18 / 46.50 MiB/s | 116.31 / 115.66 |
| Cached collision-generation reuse | 49.63 / 48.59 ms | 62 / 61 ms | 72 / 72 ms | 50.62 / 44.56 MiB/s | 116.33 / 116.97 |

The barrel guard recorded 19,891,500 rejections and no failures/fallbacks. Cached collision reuse recorded 14,114,377 generation calls, 4,437,928 cache hits, one scratch context and no failures/fallbacks. Counts confirm exercised paths, not a guaranteed speedup. The option pairs show workload-specific allocation savings and mixed CPU results, not universal smoother ticks.

All six final comparison arms passed. Hotspot captures consistently highlighted native entity physics, animation and repulsion in this NPC-heavy workload. These are inclusive markers; nested totals must not be added together or used to blame a particular mod without further evidence. No invasive autosave, chunk-I/O or worldgen algorithm changes were made.

[Sanitized comparison data](research/intensive-0.20.5/summary.json) · [Cached heavy-mod inventory](research/intensive-0.20.5/mod-inventory.json).

## Populated-world restart check

The final saved-world check ran without NOS. It resumed all 3,000 saved tagged entities without spawning new animals, observed 3,000 active (2,991 alive), 65,643 movement observations, 5,000 barrel callbacks and 111,229 firepit callbacks in the short check, then exited with code 0 and SQLite integrity `ok`. These callback totals include loading/settling and are activity evidence, not a performance rate.

The initial restart check correctly rejected an inactive herd: saved animals initialized asleep without players. The test-only resume command now sets tagged entities active and invokes their normal state-change callback. This does not modify optimizer gameplay behavior or production entities. The packaged launcher defaults to optimizer off; optional recorded profiles are explicit choices. The verified server is left stopped, ready to run locally. [Restart evidence](research/intensive-0.20.5/restart-check.json).

The live performance regression remains unresolved for the exact owner setup. The 0.20.5 runtime change fixes custom-slot recipe compatibility; no claim is made that it fixes the reported live slowdown.
