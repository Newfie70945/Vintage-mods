# Controlled benchmark and dashboard guide

The tools in `tools/benchmark/` run **owner-prepared, disposable local test clones**. They do not connect to arbitrary servers or implement game authentication. The separate official-client harness is in `tools/stress-tester/`.

There are no measured multiplayer performance improvements in the included synthetic fixtures. This delivery has no available licensed client population. The maximum authentic client count tested is **0**. An idle server startup or short smoke run is operational verification, not evidence of higher player capacity.

## Four required interventions

| Variant | Meaning | Required constraints |
|---|---|---|
| A | Untouched official server and matching production mod pack | No optimizer/profiler artifact; no optimization enabled |
| B | Same server plus profiler | Every optimization disabled |
| C | Same server plus profiler and one intervention | Exactly one independently configurable change enabled |
| D | Same server plus profiler and previously validated intervention set | Each enabled intervention references saved validation evidence |

An empty D set is allowed while no changes have been validated; it is equivalent to B and cannot demonstrate combined gains. A referenced evidence file is hashed, **not automatically accepted as proof**. Configured interventions must also be checked against the actual optimizer configuration/module status and mod inventory. Declaring an option in the manifest does not enable it or validate it.

The runner hashes the frozen world snapshot and each variant's declared official runtime files, ordinary game configuration and base mod pack. Those inputs must match across A/B/C/D. Optimizer files/config are hashed separately because their presence and settings intentionally differ. Use `gamePaths` to include **all** official runtime files that matter and `modPaths` to include the complete base mod collection; a partial list cannot establish whole-installation equivalence. Templates are scanned for symlinks and cannot contain the destination world. Every trial receives a new copy of its template plus a new copy of the same frozen world. Completed trial worlds/logs remain available for independent persistence checks.

Do not point this tool at your production installation or production world. It never deletes or modifies the frozen snapshot/template itself, but your configured local commands execute with your account's privileges. Use an unprivileged test account and a private output directory. Disk space must accommodate twelve or more separate worlds. Secrets in owner-provided commands/logs are your private data; the dashboard must stay local.

## Prepare a campaign

Copy `tools/benchmark/example.json` and fill in the real paths/commands. Place a `.nos-benchmark-clone.json` in each template directory:

```json
{"purpose":"nos-local-benchmark","allowExecution":true}
```

Prepare a clean frozen world directory while the source test server is stopped. A practical snapshot is the complete `Saves/` directory containing its `.vcdbs` and related files, copied fresh to each trial's `testdata/Saves/`. The test `serverconfig.json` must select the same saved world. Templates must have distinct test ports/data paths and their world destination must be absent. No server directory, credentials, game binaries, or licensed accounts are distributed in the example.

`gamePaths`, `configPaths`, `modPaths`, `optimizerPaths`, report paths and `worldRelativePath` are relative to each template root. `worldSnapshot` and `templateDirectory` are relative to the campaign JSON file. Existing output directories must be empty. Inputs with symlinks or traversal paths are rejected.

Run from the repository root:

```sh
python3 tools/benchmark/benchmark.py validate --config my-campaign.json --output /tmp/unused-validation-output
python3 tools/benchmark/benchmark.py plan --config my-campaign.json --output ./campaign-plan
python3 tools/benchmark/benchmark.py run --config my-campaign.json --output ./campaign-results --authorized-test-clones
```

The explicit execution flag is an operator acknowledgement that these are authorized local disposable clones. Planning does not launch processes. Commands are arrays passed directly to the operating system; the tool does not insert a shell. Optional placeholders are `{clone_dir}`, `{output_dir}`, `{trial_id}`, `{seed}`, `{phase}`, `{scenario}`, and `{duration_seconds}`. Escape literal braces as `{{` and `}}` in command elements.

Every repeat includes A/B/C/D in a seeded randomized order; all four share the same workload seed. At least three repeats are required. Use a sufficiently long warmup to reach normal JIT, cache and chunk behavior. For production review, use at least **30 minutes of measured work** per trial and separate 2–6 hour endurance runs. Randomization reduces order bias; it does not control noisy neighbours, thermal throttling, storage contention or unrelated host load. Record those separately and rerun a campaign when its environment changes.

The runner waits for each server's owner-selected readiness log pattern, runs a warmup workload, starts the profiler through configured console input, then runs the measured workload and samples the exact launched server process. It stops the profiler, waits up to ten seconds for a fresh export, and requests graceful shutdown. Examples use the official console's slash-prefixed commands. Each console line must fit within 1023 UTF-8 bytes including its newline. The tool waits up to five seconds for the official `Handling Console Command …` log acknowledgement before sending another line: the inspected official console can combine consecutive stdin writes into one command. Startup, shutdown, acknowledgement, driver and export failures remain in `trial.json` and `report.json`; the campaign stops after a failed trial. Logs are capped at 32 MiB per process and truncation is recorded.

The separate real-client driver command can be:

```json
["python3", "/absolute/repository/tools/stress-tester/stress_tester.py", "run",
 "--config", "/private/official-clients.json", "--scenario", "{scenario}", "--clients", "5",
 "--duration", "{duration_seconds}", "--seed", "{seed}", "--run-id", "{trial_id}-{phase}",
 "--output", "{clone_dir}/workload-{phase}", "--authorized-test-server"]
```

Set each variant's `workloadReport` to `workload-measurement/report.json`. The driver needs actual authenticated official clients and fresh trusted server evidence; it does not create game accounts or fake sessions. Configure idle/exploration/concentrated/mob-horde/automation/combat/teleport/heavily-modded/endurance/event scenarios as documented with that harness. Different scenarios/populations need separate campaigns. Requested bot counts are targets; report the maximum **actually authenticated** count. Never infer player counts from spawned entities.

Official clients can remain connected between warmup and measurement. Their startup/authentication or reconnect delay can otherwise leave the measured interval with insufficient work. Review profile and workload timing before accepting a comparison. Stop/restart of an external input driver is not itself a persistent client session. A reconnect scenario intentionally changes population; it requires a scenario-specific correctness and workload validator rather than this tool's conservative constant-population gate.

## Evidence and unavailable measurements

Each trial records the requested resource allocation, observed host/cgroup metadata, input fingerprints, seed/order, actual process exit status, stdout/stderr log, server-PID CPU/RSS samples, profiler JSON, workload evidence and an optional owner-generated health report. Resource samples include **only the exact launched PID**, excluding its children and the load generator. Launch the real server executable directly rather than a shell wrapper. CPU is elapsed process CPU time over wall time and may exceed 100% on multicore machines. On systems without readable Linux `/proc`, those metrics stay unavailable. Generator/official-client CPU and memory live in the stress tester's separate resource log. The runner keeps the last 8192 resource samples in memory; `samplesOverwritten` records earlier eviction. Raw `server-resources.jsonl` keeps earlier observations up to a 64 MiB disk cap, with explicit truncation reporting. Resource summaries describe the retained window, not silently the whole endurance run.

The profiler's `schemaVersion: 1`, `kind: newfies-optimizer-profile` export is consumed unchanged. `latest.json`/`latest.csv` aliases are in the test server's `NewfiesOptimizer/` directory after export. Use the correct data-path prefix in `profilerReport`. Samples can be overwritten by its bounded ring; whole-capture tick histograms and retained history have different coverage, which the profiler reports.

A has no optimizer profiler. Therefore tick metrics for A ordinarily remain **unavailable**. Measuring profiler tick-time overhead requires an identical independent collector on A and B with the same measurement boundaries; do not install the profiler on A and call it unmodified. OS CPU/RSS samples alone cannot produce tick percentiles. GC pause, autosave duration, packet details and third-party-system attribution remain unavailable unless the target-version profiler actually supports them; missing fields are never filled with estimates or zeroes.

The optional `healthReport` must be produced by a real external validator, for example:

```json
{"serverErrors":0,"queueGrowth":false,"evidenceSource":"local validator and saved log paths"}
```

Do not create this by assuming an empty console means correct saves. Preserve errors, disconnects, action acknowledgements, interaction failures, queue accumulation, simulation timing and post-reload world/inventory/entity/block comparisons. Workload `correctnessChecks` must independently evidence `saveReloadIntegrity`, `inventoryCorrectness`, `simulationTiming`, `permissions` and `combatCorrectness`; adapt a scenario-specific external validator before considering results production-ready. The supplied stress tester intentionally leaves checks unproven. Non-applicable checks need documented scenario review, not silently invented passes.

## Comparison and exports

Outputs are `report.json`, `trials.csv`, a standalone `report.html`, the plan/input manifest and per-trial evidence. Campaign JSON/dashboard histories use at most 128 evenly spaced samples per trial to bound memory and payload; the first/last sample and original sample count are preserved, and downsampling is labelled. Each `trial.json` and original profiler export retains its full available history. The profiler sample `p95TickMs` is the cumulative capture P95; its average tick/TPS fields describe the observed sample interval. A comparison can also be rebuilt from saved trial files:

```sh
python3 tools/benchmark/benchmark.py compare --trial results/trials/r01-B/trial.json --trial results/trials/r01-C/trial.json --output imported-report
```

Imported trials do not automatically inherit a verified campaign identity. Such comparisons remain explicitly limited. Reports show mean/median/P95/P99/min/max/sample standard deviation **across independent trial metrics**. For example, a median of trial P99 values is not a pooled P99 over all ticks. Empirical percentiles use sorted observations and linear interpolation; missing/non-finite values are excluded and never treated as zero. Three repetitions describe basic variation, not statistical certainty.

The review gate stays closed without three independent repeats, matching seeded scenario/duration/client/work counts, full measured ticks, authenticated continuously present clients, at least 30 minutes measured time, zero evidenced failures/disconnects, queue-health evidence and independent correctness checks. Confirmed action totals must be accompanied by matching `workCompletedByType` counts, so an easier mix of actions cannot silently qualify as the same work. Synthetic/idle-only fixtures never qualify. Even qualifying data says **manual review required**, not “faster in production”. A lower tick duration does not count as success if less correct work completed. Report throughput, memory retention and regression costs beside CPU/tick measurements.

## Local interactive dashboard

```sh
python3 tools/dashboard/serve.py --report campaign-results/report.json --port 8765
```

Open `http://127.0.0.1:8765`. The separate process binds only loopback and adds no game-server listener. It serves exactly the chosen JSON plus three fixed static assets, rejects arbitrary/traversal URLs, sets a restrictive content policy, reads the file at most once every two seconds, and polls every five seconds while visible. It cannot edit reports or server settings. Use an owner-managed SSH tunnel for a remote test machine; do not expose this unauthenticated diagnostic viewer publicly.

Select a trial and metric, adjust the history window, hover for sample values, inspect hotspots/status/evidence limits, compare intervention changes and export JSON/history CSV. Missing samples remain gaps. Cards/charts display the measured TPS, tick duration, CPU/RAM, GC, player/entity/chunk/network fields when available. Save timing and uninstrumented internals remain unavailable. The standalone HTML works without hosting; the interactive dashboard needs its local Python process.

To preview layout and verify report math without a game server:

```sh
python3 tools/benchmark/fixtures/generate_synthetic.py --output /tmp/nos-SYNTHETIC-preview
python3 tools/dashboard/serve.py --report /tmp/nos-SYNTHETIC-preview/report.json
python3 -m unittest discover -s tools/benchmark/tests -v
python3 -m unittest discover -s tools/dashboard/tests -v
```

Every fixture is prominently labelled **SYNTHETIC** in JSON, HTML and dashboard, has zero authentic clients and is blocked from performance conclusions. The process-boundary test runs only a local tiny Python fake console; it tests orchestration, never Vintage Story performance.
