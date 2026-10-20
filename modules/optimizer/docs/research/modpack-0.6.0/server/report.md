# NOS 0.6.0 full-Suite/selected-modpack laboratory comparison

4 passing runs; order: stock, nos, nos, stock. 120 seconds of steady moving-NPC AI/physics per run. Each value is the median of per-run observations, followed by the observed range. This is not a player-capacity benchmark.

| Measurement | NOS switches off | NOS defaults | Unit |
| --- | ---: | ---: | --- |
| Generation, 64 columns | 11.20 (10.88–11.52) | 10.96 (10.78–11.13) | s |
| Steady tick average | 1.60 (1.47–1.74) | 1.51 (1.50–1.52) | ms |
| Steady tick p95 | 2.50 (2.00–3.00) | 3.00 (3.00–3.00) | ms |
| Steady tick p99 | 4.50 (4.00–5.00) | 5.50 (5.00–6.00) | ms |
| Worst steady tick | 31.00 (24.00–38.00) | 21.50 (18.00–25.00) | ms |
| Effective steady ticks/s | 29.70 (29.69–29.72) | 29.68 (29.68–29.69) | ticks/s |
| Other loaded entities at steady end | 38.50 (23.00–54.00) | 32.50 (32.00–33.00) | entities |
| Steady CPU | 8.68 (8.24–9.12) | 8.32 (8.27–8.36) | % of one core |
| Steady allocation | 3.12 (3.05–3.19) | 2.67 (2.65–2.69) | MiB/s |
| Steady GC pause | 0.75 (0.67–0.82) | 0.68 (0.66–0.69) | ms/s |
| Steady peak RSS | 1589.25 (1564.39–1614.11) | 1660.86 (1590.06–1731.67) | MiB |
| Steady end RSS | 1589.74 (1564.85–1614.63) | 1661.40 (1590.55–1732.25) | MiB |
| Steady end GC committed | 1337.09 (1314.05–1360.12) | 1406.53 (1331.61–1481.46) | MiB |
| Steady end GC fragmented | 20.21 (0.39–40.03) | 0.43 (0.40–0.47) | MiB |
| Steady end managed estimate | 1130.86 (1128.10–1133.62) | 1129.16 (1123.60–1134.72) | MiB |
| First dirty save | 157.05 (142.66–171.43) | 228.34 (221.01–235.67) | ms |
| Repeated dirty saves | 114.99 (108.68–121.30) | 109.84 (102.77–116.91) | ms |
| Save main-thread preparation | 76.39 (72.94–79.85) | 82.06 (74.69–89.43) | ms |
| Tracked chunk references after unload + lab GC | 512.00 (512.00–512.00) | 6.00 (5.00–7.00) | chunks |

## Engine-profiler cost windows

Per-run medians across paired windows, then median/range across all runs. Each on window contains a 3s capture within about 4s, including completion work. This measures the current optimized Suite profiler, not the old implementation.

| Measurement | Off windows | Capture windows |
| --- | ---: | ---: |
| Tick average, ms | 1.41 (1.24–1.62) | 2.18 (2.08–2.55) |
| Tick p95, ms | 2.00 (2.00–3.00) | 3.00 (3.00–4.00) |
| Allocation, MiB/s | 2.81 (2.26–3.47) | 3.52 (3.29–3.80) |
| CPU, % of one core | 8.27 (7.15–9.25) | 10.32 (9.54–11.43) |

Validation bookkeeping note: the first stock run completed its workload and integrity checks, but an inner profiler-window variable shadowed the arm identifier in the terminal module-state assertion. The checker was fixed before the NOS runs. The original terminal record is retained, and the recorded stock state/exit/integrity/error/tick/activity evidence was independently revalidated without changing any measured values. See validationCorrection and result.original-terminal.json.

Environment note: execution reconnected after the third completed trial and interrupted the fourth launch. Only the final baseline was restarted from the stopped world copy after input-hash validation. The quota remained 2 vCPU / 8 GiB, but physical-host identity was not recorded before reconnect. Small cross-run timing/CPU differences are not evidence of a causal gain. See environment-interruption.json.

## Limits

- Two runs per arm are descriptive observations, not significance or production guarantees.
- Tick work is the official integer-millisecond pre-sleep sample, observed once per tick. It is not the frame-profiler root duration and cannot resolve small sub-millisecond differences.
- NPCs are forced-active and fixture-locally-tracked for stock physics without clients; pass-through AI subscribers are test controls. The normal task-start loop does not use explicit ExecuteTask, so its new module is measured separately.
- World/NPC randomness, shared 2-vCPU container scheduling, caches and incidental entities remain confounders.
- Profiler on windows are four seconds containing one requested three-second automatic-style capture and its finalization; off windows are four seconds without it. They alternate order, and are much more frequent than the production 300-second cooldown.
- Discord has no token; online gameplay, webpanel polling, client graphics, network delivery and every loaded feature are not exercised.
- Compacting GC/unload calls are retention measurements outside the steady phase, not optimizer behavior.

All run databases passed SQLite integrity checks. See raw result JSON, process samples, enabled-mod inventory and artifact/world hashes for reproduction. No benchmark fixture belongs in a production Mods folder.
