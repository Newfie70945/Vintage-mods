# NOS 0.6.0 + Suite 1.2.44 ModpackStability1

## Narrow component results

Official Vintage Story 1.22.7, .NET 10; tiered compilation disabled for these isolated comparisons. These measurements are separate from the server workload.

| Component | Previous behavior | Current behavior | Scope |
| --- | ---: | ---: | --- |
| Explicit AI task request, median ns/call | 153.29 | 19.29 | Five trials, actual official `ExecuteTask`, synthetic veto subscribers |
| Explicit AI task request, bytes/call | 120 | 0 | Stock state transitions and callbacks preserved |
| Profiler attribution, ms/marker | 0.51864 | 0.01854 | Built old/new Suite matcher; 104 paths, 510 catalog tokens, 20 repetitions |
| Profiler attribution, bytes/marker | 319531.38 | 138.54 | Preallocated reflection arguments; output signatures identical |

The AI patch affects explicit `AiTaskManager.ExecuteTask` requests made by commands/mods. The normal autonomous `StartNewTasks` loop does not call that method, so this result is not a blanket AI speedup. Existing AI-filter and repulsion modules remain separately controlled and are exercised by the NPC laboratory workload.

The profiler matcher previously normalized the marker and token again for every eligible comparison. The new matcher normalizes each marker once and keeps normalized catalog tokens. Character filtering/lowercasing, boundaries, scores, ambiguous matches and core/unknown handling are preserved. The matching output hash is identical for the test workload; a separate runtime regression test covers concrete handler ownership and ambiguous/shared markers. This does not imply that the whole profiler is 28 times faster.

Raw component data: [AI task requests](research/modpack-0.6.0/task-start-measurements.json), [attribution before](research/modpack-0.6.0/profiler-attribution-before.json), [attribution after](research/modpack-0.6.0/profiler-attribution-after.json).

## Server workload and reproducibility

The runner uses the same stopped test-world copy and seed in each process, official server 1.22.7, all six Suite modules, NOS, and 17 additional mods. The [pack manifest](research/modpack-0.6.0/pack-manifest.json) lists names, versions, dependencies and SHA-256 hashes. Enabled state is verified at runtime before measurements.

Third-party pack: all eight Floral Zones regions, Primitive Survival, xSkills Fork/XLib Fork, Draconis/Jaunt, Forgotten Armory Core/Gothic, Attribute Rendering Library and the required VSImGui dependency. These were selected for additional assets, world generation, entities and modded behaviors. A loaded mod is not proof that every feature has been exercised; Discord has no bot token, no authenticated clients connect, and no webpanel poll traffic is generated.

Each measured process warms up, generates 64 columns, creates 128 stock pig NPCs with their real AI/physics and mod behaviors, measures 120 seconds of steady activity, runs five paired four-second off/on windows (on includes a three-second engine capture), performs five dirty saves, unloads terrain, and checks retention plus SQLite integrity. Movement and AI task activity must increase; an idle setup fails. Explicit full GCs are lab retention controls outside steady measurements, not an optimizer feature.

Because official physics skips untracked NPCs without clients, the fixture forces only its tagged animals active and locally tracked while leaving the normal physics worker in charge. Pass-through AI subscribers exercise delegate dispatch. This is a controlled NPC workload, not authenticated bots, players or network scaling.

Run `tools/modpack_performance_check.py --help` from the optimizer module. Supply a disposable stopped test snapshot, the official game directory, compiled Suite/NOS, `NosModpackBenchmarkProbe.dll` built from tests, and a pack directory with the manifest and matching ZIPs. `tools/modpack_report.py RUN_DIRECTORY` summarizes completed runs. Production mod ZIPs must never include the benchmark fixture. Preserve player authentication and keep tests bound to loopback.

## Completed comparison and interpretation

Four runs completed in off/on/on/off order, with two minutes of steady NPC activity each. All six Suite modules, NOS and 17 third-party mods were enabled; each world database passed its integrity check. These observations compare all NOS engine switches disabled with shipping defaults, not NOS 0.5.0 with 0.6.0. The new explicit-task-request patch is tested separately above.

Steady allocation was 3.12 MiB/s with NOS switches off versus 2.67 MiB/s with defaults, about 15% lower. Average tick work was 1.60 versus 1.51 ms, but p95/p99 tick work was higher with NOS, and RSS was higher (steady peak 1589 versus 1661 MiB). Generation and repeated-save timings overlap; the first dirty save was slower with NOS. These results do **not** establish an overall tick, world-generation, save-speed or process-memory improvement. NPC randomness, incidental entity counts, shared-host scheduling and the interruption described below limit causal conclusions.

The existing save-snapshot cleanup did show a specific retention difference: after unloading and laboratory GC, 512 tracked chunk references remained alive with switches off versus 5–7 with NOS defaults. This verifies that retention path in this fixture; it does not diagnose the owner's production heap or guarantee lower RSS.

Profiler captures remained measurable overhead even with the optimized matcher: paired-window median tick work increased from 1.41 to 2.18 ms and allocation from 2.81 to 3.52 MiB/s. Captures therefore remain explicitly opt-in and default off. These are forced frequent laboratory windows, not the production cooldown or a comparison of whole-profiler old/new implementations.

The first baseline's terminal checker reused the arm variable inside a profiler-window loop. The workload succeeded, but its final state assertion incorrectly expected the enabled arm. The checker was corrected, and the original recorded module states, activity, clean exit and database integrity were independently revalidated without changing measurements. The original terminal record is preserved. The environment also reconnected after run three; only the incomplete fourth launch was discarded and restarted from the same stopped snapshot after input-hash validation. Both events are documented in the raw records and generated report.

[Complete tables and limitations](research/modpack-0.6.0/server/report.md) · [Machine-readable analysis](research/modpack-0.6.0/server/analysis.json) · [Full run summary](research/modpack-0.6.0/server/summary.json). Process samples and the original terminal record are stored beside these files. Reproduce the tables with `python modules/optimizer/tools/modpack_report.py modules/optimizer/docs/research/modpack-0.6.0/server`.
