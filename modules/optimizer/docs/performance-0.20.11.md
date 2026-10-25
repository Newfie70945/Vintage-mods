# NOS 0.20.11 / Suite 1.2.52 entity-behavior pass

Two independent experimental allocation options were added. Both default to **off**.

- `EmotionSearchReuse`: reuse an emotion state's captured nearby-entity predicate through a weak-key cache. Searches, range, live code filters, movement and hostility decisions still execute natively. Cached values do not retain discarded states. Fresh position-vector ownership is preserved. A native-entry delegate keeps the query composable with nearest-search patches enabled later at runtime.
- `TemperatureAnimationChecks`: replace two temporary animation-name arrays in native body-temperature animation checks. Temperature calculations, heat-source scans, damage, timing and animation start/stop logic remain in the original method. Custom animation managers receive fresh argument arrays and their virtual calls as before.

Suite 1.2.52 provides separate toggles and execution counters. Unsupported controls stay disabled for older NOS versions. Unknown foreign patches and unsupported method layouts retain native behavior. Failure diagnostics are per module; a throwing query is never replayed.

## Validation

All 31 required automated suites passed on official Vintage Story 1.22.7. Suite and NOS builds completed without warnings/errors. Browser tests pass for save/draft handling, Safe Mode, read-only access, capture locks, older-NOS compatibility and mobile layout.

The emotion tests compare the actual native search on a populated synthetic partition, observe changed filters/ranges immediately, verify weak-key collection, and exercise nearest-search activation after the emotion path is already warm. The composition check verifies every one of 130,000 warmup/measured calls passes through the later-enabled nearest-search module.

Observed allocation per emotion query fell **408 → 320 bytes** alone, and **296 → 208 bytes** when composed with nearest-search reuse: 88 bytes saved per query in either fixture. Controlled timings did not establish a general CPU improvement. Native warm body-temperature animation checks fell **64 → 0 bytes per update**. These component fixtures are not proof that heat scans or production player behavior became faster.

The traversal allocation test now allows the JIT to have already elided a native iterator allocation; it still requires the observed allocation reduction and prevents an allocation regression. It no longer demands saving 24 bytes when the native path allocated fewer than 24 bytes.

## Populated server comparison

Two separate disposable runs used the same saved template, Suite/NOS versions and 23 selected mod ZIPs. Each spawned 3,000 active native animals with AI/physics, 500 barrels and 500 firepits, then measured 180 seconds. Only the two new flags differed. NOS profiling was stopped; test-only forced GC, explicit saves and unloading ran outside the steady windows.

| Metric | New options off | New options on |
| --- | ---: | ---: |
| Average tick | 44.66 ms | 47.73 ms |
| p95 tick | 56 ms | 61 ms |
| p99 tick | 64 ms | 73 ms |
| Worst tick | 93 ms | 134 ms |
| Allocation rate | 53.33 MiB/s | 51.63 MiB/s |
| CPU (100% = one core) | 116.08% | 115.74% |
| End managed heap | 1389.15 MiB | 1387.69 MiB |
| GC pause total in steady window | 2812.25 ms | 3080.47 ms |
| Median explicit save preparation | 78.03 ms | 66.97 ms |

Both runs passed five explicit-save checks, unloading, clean exit and SQLite integrity with zero logged errors. The candidate executed 487,228 emotion searches with 810 callback creations and zero module failures across the recorded workload (including phases outside the steady window). Temperature checks recorded zero executions because no authenticated players were present; that path is covered by the separate native-method fixture.

**Conclusion:** allocation rate decreased about 3.2%, but tick latency worsened and the candidate completed fewer ticks. Lower bytes per second alone is not evidence of less allocation per unit of work; measured allocation per tick increased in this pair. This is **not a demonstrated lag fix** or a recommendation to enable the animal option for latency relief. Both remain experimental and off by default. One sequential pair cannot establish causation or distinguish every source of workload variation.

## Use and limits

Suite > Optimizations exposes **Reuse animal emotion-search callbacks (experimental)** and **Allocation-free temperature animation checks (experimental)**. Save/apply, inspect actual module state and counters, and compare comparable workloads. The emotion option was tested in composition with nearest-entity search. No full crafting batches, AI/breeding/movement intervals or world data are modified.

No authenticated clients, player crafting or production world were used. The lab had 2 vCPU/8 GiB; its cached XSkills version is 1.1.9, different from the uploaded production listing's 1.1.8. This does not resolve attribution of the older multi-second stalls, prove long-term memory behavior, or reproduce the complete live modpack.

[Emotion component data](research/behaviors-0.20.11/emotion-component.json) · [Temperature component data](research/behaviors-0.20.11/temperature-component.json) · [Populated comparison](research/behaviors-0.20.11/server-comparison.json).
