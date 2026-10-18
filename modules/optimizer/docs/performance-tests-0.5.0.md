# NOS 0.5.0 generation/save performance test

Official Vintage Story 1.22.7, Suite 1.2.48, shipping NOS 0.5.0 preview. Three runs per arm, identical stopped-world copies and seed, fixed 64-column generation, five dirty saves of 512 target chunks, explicit unload and snapshot-refresh checks. Values below are per-run medians with the observed run range; repeated-save numbers first take the median of saves 2–5 in each run.

| Measurement | Stock NOS target methods | NOS defaults | Unit |
| --- | ---: | ---: | --- |
| Peak incidental loaded entities | 27.00 (24.00–30.00) | 39.00 (36.00–41.00) | entities |
| Generation, 64 columns | 9.88 (9.82–10.29) | 9.79 (9.52–10.05) | s |
| Generation CPU work | 11.34 (11.05–11.83) | 11.06 (10.88–11.27) | CPU s |
| Generation allocations | 903.52 (902.37–903.67) | 903.13 (902.69–903.22) | MiB |
| First dirty save | 269.27 (257.23–275.04) | 308.61 (275.58–312.23) | ms |
| Repeated dirty saves | 181.32 (181.07–189.17) | 184.85 (179.58–185.88) | ms |
| Save main-thread preparation | 154.84 (154.24–157.56) | 155.30 (147.75–165.36) | ms |
| Tick work p95 | 1.00 (1.00–1.00) | 1.00 (1.00–1.00) | ms |
| Tick work p99 | 8.00 (7.00–9.00) | 12.00 (9.00–12.00) | ms |
| Worst tick work | 225.00 (217.00–237.00) | 256.00 (233.00–259.00) | ms |
| Effective TPS | 29.11 (29.10–29.13) | 29.06 (29.04–29.09) | ticks/s |
| Peak measured RSS | 1249.08 (1241.51–1252.46) | 1233.18 (1208.83–1240.39) | MiB |
| Peak measured managed heap | 879.86 (877.95–880.71) | 880.18 (880.08–880.78) | MiB |
| Workload allocations | 1005.99 (1002.84–1006.12) | 1006.53 (1006.14–1006.64) | MiB |
| GC pause during workload | 230.42 (209.29–233.11) | 234.35 (214.85–259.91) | ms |
| Snapshot entries after completed save | 552.00 (552.00–552.00) | 0.00 (0.00–0.00) | references |
| Tracked chunks after unload + lab GC | 512.00 (512.00–512.00) | 4.00 (4.00–4.00) | chunks |
| Tracked chunks after next save + lab GC | 4.00 (3.00–4.00) | 4.00 (4.00–4.00) | chunks |
| Live heap after unload + lab GC | 770.44 (769.13–770.49) | 766.69 (766.38–767.01) | MiB |
| Heap released by next snapshot + lab GC | 11.68 (1.89–12.00) | -0.78 (-0.84–-0.76) | MiB |

## Findings

The reference-retention fix is reproducible: every baseline run kept all 512 tracked chunks alive after unload and the test GC; every NOS/defaults or storage-only run retained four. Taking a new baseline save snapshot then released nearly all remaining tracked references. Peak managed heap during the workload stayed around 880 MiB in the main comparison, so the tests do not demonstrate a large reduction in peak RAM.

The primary generation medians differed by less than 1%, with overlapping ranges. Repeated dirty saves also overlapped. The default-NOS sample had slower first saves (309 vs 269 ms), p99 tick work (12 vs 8 ms) and worst-tick median (256 vs 225 ms). These observations must be retained rather than described as a speedup. Incidental NPC counts also differed: 24–30 for the three baseline runs, 36–41 for NOS defaults. The tests do not isolate a causal timing regression.

The clearest remaining stall is autosave preparation: roughly 155 ms median across dirty-save cycles, with first-save ticks around 217–259 ms. That synchronous preparation is a useful next profiling target. A larger throughput claim requires controlled entity populations and longer real-player captures on the target modpack.

## Interpretation boundaries

The stock arm retains the same Suite, NOS observer and test fixture, with NOS engine switches disabled. The NOS arm uses shipping default engine switches. This workload exercises terrain generation, saving and unloading; it does not establish entity, network or real-player performance.

Natural entity spawning remained enabled, so incidental entity counts varied even with the same world copy and fixed chunk requests. This can affect serialization and tick timings; the table reports those counts. Generation and save timing ranges must be assessed alongside individual trials. Three repetitions, shared host scheduling, file caches and container storage cannot establish a small production speedup. Tick work is the official integer-millisecond pre-sleep sample. Save wall times are observed from console events; main-thread preparation and worker disk writes overlap.

Explicit compacting GCs occur before capture and after forced unloads to measure live retention. They are test-only controls, not NOS behavior or production recommendations. Retained-object counts are not retained bytes; RSS includes native memory, committed heap and reusable pools. Memory after the next snapshot also includes unrelated engine/cache activity.

Environment: CPU quota/period 200000 100000 microseconds; memory cgroup limit 8 GiB. The shared host was not reserved exclusively. World-generation and physics thread counts match the supplied stopped template. All recorded saves passed SQLite integrity checks: True. No authenticated clients or bots were used.

The raw JSON contains per-run observations and exact world/artifact hashes. The runner and fixture are included in source for reproduction. Production TPS and player-capacity claims require long real-player captures on the target modpack.

## Follow-up: new storage modules only

Three additional fresh-world runs used order storage-only, baseline, storage-only. Only `WorldgenRequestReuse` and `SaveSnapshotRelease` were enabled in storage-only mode. The fixture and production binaries were unchanged. Each run passed its save-integrity check.

| Measurement | Extra baseline run | Two storage-only runs, median (range) |
| --- | ---: | ---: |
| Generation seconds | 9.93 | 10.33 (10.27–10.40) |
| First dirty save, ms | 283.46 | 283.15 (270.12–296.17) |
| Repeated dirty saves, ms | 194.18 | 182.06 (173.26–190.86) |
| Tick work p99, ms | 10.00 | 9.00 (6.00–12.00) |
| Peak incidental entities | 31.00 | 50.50 (49.00–52.00) |
| Tracked chunks after unload + lab GC | 512.00 | 4.00 (4.00–4.00) |

The first-save timings were similar in this follow-up, but entity counts again varied substantially (31 baseline, 49–52 storage-only). It confirms reference release and does not establish a generation/save speedup or identify the cause of the primary timing difference. All nine measured runs passed integrity checks. The preliminary instrumentation pilot is excluded from these summaries.

[Primary per-run data](research/storage-performance-0.5.0/aggregate.json) · [Isolation data](research/storage-performance-0.5.0/isolation.json).
