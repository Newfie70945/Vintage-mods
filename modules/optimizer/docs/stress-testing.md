# Newfie's VS Stress Tester

The standalone tool is `tools/stress-tester/stress_tester.py`. It uses Python 3.10+ standard library plus the system `xdotool` executable for real Linux X11 input. Read [client feasibility](client-feasibility.md) first: this delivery tested **zero real client sessions**, and input attempts are not proof of completed gameplay.

## Setup

1. Clone a dedicated test server, its world and actual production mod collection. Keep `VerifyPlayerAuth=true`. Restrict access to your test accounts and use a separate address/port from production.
2. Use separately authorized licensed accounts for each concurrent session. Start official 1.22.7 desktop clients on a separate load-generator machine, authenticate normally and join the saved test server.
3. Provide X11 displays with a compatible window manager. Each display can have several client windows, but only one can receive input at once. Prefer one client per display for exploration/combat. This driver does not install virtual GPUs, create displays, launch or license desktop clients.
4. On Debian-based **load-generator** hosts, install input tooling with `sudo apt-get install xdotool`. Use `DISPLAY=:1 xdotool search --name 'Vintage Story'` and `DISPLAY=:1 xdotool getwindowpid WINDOW_ID` to identify each real game window. Do not point it at your everyday desktop; held movement keys and clicks are intentional.
5. Copy `tools/stress-tester/example.json` to a private run configuration. Replace the server address/port metadata, every exact server PlayerUID, local display and numeric window ID. An optional `processId` is checked against the window. Duplicate IDs/windows, oversized macros and unsafe limits are rejected. Metadata does **not** automatically change the server selected in the game UI.
6. Enable the optimizer's optional, bounded session evidence on the isolated test server. Set `Benchmarking.WriteSessionEvidence=true`. The file is `<server data directory>/NewfiesOptimizer/sessions.jsonl`. For a single manual run, `Benchmarking.RunId` may match the driver's run ID; for automated warmup/measurement phases, leave it empty so fresh UID/time-scoped presence applies to both phases. Action acknowledgements always require the exact current driver run ID. Supply the resulting JSONL to the harness as `serverEvidenceFile` through a trusted shared filesystem or an append-preserving transport. Do not run the harness with the ability to rewrite server evidence. Start a new evidence file between trials, not during a run.
7. Calibrate key bindings, mouse look, GUI coordinates and each prepared scene on **one** real player before increasing population. Ensure sufficient GPU/RAM/account resources; CPU alone does not describe generator capacity.

Example tooling checks from the source root:

```sh
python3 tools/stress-tester/stress_tester.py validate --config tools/stress-tester/example.json
python3 tools/stress-tester/stress_tester.py plan --config tools/stress-tester/example.json --scenario exploration --clients 1 --seed 17227 --duration 1800
python3 tools/stress-tester/stress_tester.py run --config tools/stress-tester/example.json --scenario exploration --clients 1 --seed 17227 --duration 1 --run-id tooling-check --output results/tooling-check --dry-run
python3 -m unittest discover -s tools/stress-tester/tests -v
```

Example actual workload after setup, using a **new** output directory:

```sh
python3 tools/stress-tester/stress_tester.py run --config private-clients.json --scenario exploration --clients 1 --seed 17227 --duration 1800 --run-id trial-c-01 --output results/trial-c-01/workload --authorized-test-server
```

`--authorized-test-server` is the operator's explicit assertion that the target is an owned/authorized dedicated test server. It is required for any real input. The operator must check the official client is connected to the configured test server. This tool does not resolve arbitrary server addresses or probe the Internet.

The runner waits for fresh server evidence for every requested UID. By default, presence expires after 30 seconds. An idle game window without trusted server evidence does not count as a connected player. Do not disable heartbeat checks to hide connection failures.

## Actions and preparation

Built-in macros issue forward movement, sprint, jump, relative mouse look, mining clicks, placing/interact clicks, inventory open/close, and combat clicks. Defaults are starting bindings, not proof of compatibility with your control setup. `mine` and `place` remain unconfirmed until a server-side observer checks actual blocks, claims, inventory consumption and logs.

Custom macro steps support:

| Operation | Parameters | Meaning |
|---|---|---|
| `key` | `key` | One X11 key/chord |
| `hold` | `keys`, `seconds` | Hold up to four configured keys, then release |
| `mouse_hold` | `button`, `seconds` | Bounded mining/combat hold |
| `click` | `button` | X11 mouse button 1–5 |
| `look` | relative `x`, `y` | Bounded relative mouse motion; must be calibrated |
| `point` | window-relative `x`, `y` | Position cursor over a calibrated UI target |
| `wait` | `seconds` | Bounded UI wait |
| `command` | `text` | Type one permitted server command into chat |

Every command must begin with `/`, fit one line and appear **exactly** in `allowedCommands`. Enter commands that already work with the test account's real permissions. No administrator privileges, teleport syntax or server commands are invented by the harness. Commands do not bypass claims or anti-cheat. Keep them out of ordinary production accounts.

For inventory/container work, define a calibrated `inventory_transfer` macro that opens the prepared container, points/clicks the intended slots and closes the UI. Use the proper resolution and actual game control scheme. For example, a transfer macro can contain `point`, `key` with your supported shift chord or `click`, `wait`, then the configured close key. This is a mechanism for scripting real UI input; it is **not** a transaction validator. Compare item identities/counts and container state on the authoritative server before and after the run and after save/reload.

For reconnect tests, define `reconnect` to navigate the official menu from an already-connected world back to the saved test server. For optional initial joining, set client `connectOnStart=true` and `connectMacro` to a calibrated connection macro. The official client authenticates through its normal login; credentials are never typed by this tool. Join bursts should be staggered to respect the server's legitimate login protection.

For rapid teleportation, define `teleport` using authorized commands already tested by the owner. Include all exact command strings in `allowedCommands`; two locations can be traversed in one bounded macro. Verify that they actually load the intended terrain. Teleportation often grants movement grace in anti-cheat; do not mistake that for tested ordinary movement.

## Scenarios

Each client's action **choice sequence** is deterministic for its UID and seed. Timing remains sensitive to UI latency, frame rate, focus contention and host resources. Compare actual confirmed work, not only seed or elapsed time.

| Scenario | Executable action pattern | Required owner preparation and correctness evidence |
|---|---|---|
| `idle` (A) | Connected clients remain idle | Same location; continuous session evidence; synchronization/network metrics |
| `exploration` (B) | Seeded sprint/forward/look/jump | Different initial directions and unexplored areas; server-confirmed positions, generated/loaded chunks and latency |
| `concentrated` (C) | Movement, jumping, inventory, interaction | Same controlled settlement; verify claims, transactions and entity replication |
| `mob_horde` (D) | Movement and combat | `scenes.mob_horde.prepared=true`, description, controlled authentic mobs; owner prepares 100/250/500/1000 as resources permit; verify live mob counts and behavior |
| `heavy_automation` (E) | Container interaction, movement, transfer macro | Prepared base and `inventory_transfer`; machines, production rates, temperature/spoilage/contents must be checked |
| `combat` (F) | Combat and movement | Prepared permitted combat scene, equipment/targets; verify damage, death, inventories and responsiveness |
| `rapid_exploration` (G) | Teleport macro and exploration | Explicit `teleport` macro/allowlist, permitted accounts, terrain and chunk synchronization checks |
| `heavily_modded` (H) | Movement, mine, place, transfer | Prepared cloned pack, identical hashed mods/world, inventory macro; independent anti-cheat/OreShield/claim checks |
| `endurance` (I) | Movement, jumping, inventory, reconnect | Explicit reconnect macro, 2–6 hours (`--duration 7200` to `21600`), longer configurable to 86400; memory/queues/errors and persistence checks |
| `fractured_night` (J) | Combat, movement, jumping | Prepared compatible event server, real event/mob/particle setup; normal-night comparison and mechanic preservation |
| `building` | Mine, place, transfer | Prepared test structure, permitted claims and inventory transfer macro; authoritative block/item reconciliation |

Scene-dependent scenarios refuse to start without a corresponding `scenes` entry containing `prepared: true` and a nonempty `description`. This is preparation metadata, **not** evidence that the world exists or that interactions succeeded. Spawn populations, automation bases and custom events are not generated through guessed commands. They must be prepared and measured on the actual server.

Population targets are 1, 5, 10, 20, 30, 50, 75, 100, 150 and 200. Each level requires that many real client windows and authorized sessions; the runner never creates fake clients to satisfy a target. Increase `maxClients`/`maxParallelDisplays` deliberately after measuring generator resources. A shared display serializes all macros and can become the bottleneck. Merely maintaining 200 idle sessions does not demonstrate 200 active explorers or combatants.

## Evidence format

The optimizer can emit optional server-owned session records. Exact UID records are excluded from normal profiling and enabled only for isolated testing:

```json
{"schemaVersion":1,"kind":"presence","clientId":"EXACT_PLAYER_UID","utc":"2026-10-17T12:00:00Z","runId":"trial-c-01","authenticationEnabled":true}
```

Supported connection kinds are `connected`, `presence` and `disconnected`. Fresh presence/join entries establish the observed online population. Server/client clocks must agree within five seconds. Another run ID is ignored. A missing, replaced, truncated, oversized or stale evidence source prevents trusted continuous-session claims. A blank server run ID permits connection tracking, but action acknowledgements must match the explicit trial run ID.

The profile's connection evidence does **not** validate gameplay transactions. An independent, validated authoritative observer can append:

```json
{"schemaVersion":1,"kind":"action_confirmed","clientId":"EXACT_PLAYER_UID","utc":"2026-10-17T12:00:02Z","runId":"trial-c-01","actionId":"trial-c-01:EXACT_PLAYER_UID:0","detail":"Authoritative block and inventory changes reconciled"}
```

`action_failed` has the same fields. Acknowledgements must refer to an actual attempted action, the same owner UID and current run; duplicates are ignored. The observer must correlate action IDs emitted in `events.jsonl` with real server outcomes. Such an inventory/combat/world-outcome observer is **not included** in this delivery. Do not manually fabricate acknowledgements to obtain a passing comparison.

## Outputs and interpretation

* `plan.json`: target, scenario, seed, configured UIDs and first ten deterministic choices.
* `events.jsonl`: input attempts, dispatches and failures; `gameplaySuccess` is deliberately unknown after a keypress.
* `generator-resources.jsonl`: cumulative CPU seconds, RSS and virtual memory for the Python generator and discovered official-client PIDs on Linux. These are **not** server CPU/RAM. Include X11/compositor/GPU/host totals separately when reporting capacity.
* `report.json`: connected peak **and minimum**, authenticated-session evidence, actual workload duration, disconnects, attempted/confirmed/failed/unconfirmed action counts and errors.
* `action-acknowledgements.json`: accepted independent outcome evidence, where provided.

`authenticatedSessionEvidence=true` requires continuous server-backed online observations for all requested UIDs and explicit `authenticationEnabled=true` on session records. It still relies on the owner's trusted official server and evidence transport; a window title/PID alone proves no authentication.

Reports deliberately leave `correctnessChecks` empty and normally finish as `awaiting_gameplay_validation`. Successful input dispatch is not successful gameplay. The benchmark comparison must remain blocked until independently validated world save/reload, persistence, inventory, permissions, combat, movement, simulation timing and relevant anti-cheat/OreShield checks exist. Idle runs also require healthy server metrics and continuous presence; they do not validate combat or building.

`--dry-run` never creates connections or successful interactions. Its report states `simulated_tooling_only`, `actualConnectedPeak=0`, `actualConnectedMinimum=0`, no authenticated-session evidence, no confirmed actions and no correctness checks. It is excluded from any performance claim.

The tool writes bounded files/maps and rejects excessive clients, duration, event counts, file sizes and macro durations. A reached bound stops the workload; it does not silently discard important work. SIGINT/SIGTERM and input errors trigger key/button release. If X11 disappears, cleanup can fail; the error names that condition and the owner must release input/restart the dedicated display. It does not kill game clients or shut down the test server automatically.
