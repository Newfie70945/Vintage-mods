# Newfie's VS Stress Tester 0.1.0

Python 3.10+ standard library; real input additionally requires Linux X11, a window manager and `xdotool`. The operator supplies licensed, normally authenticated official Vintage Story 1.22.7 desktop clients. No client/protocol/authentication replacement is included.

From the project root:

```sh
python3 tools/stress-tester/stress_tester.py validate --config tools/stress-tester/example.json
python3 tools/stress-tester/stress_tester.py run --config tools/stress-tester/example.json --scenario idle --clients 1 --duration 1 --run-id tooling-check --output results/tooling-check --dry-run
python3 -m unittest discover -s tools/stress-tester/tests -v
```

Dry runs test the tooling only. Maximum authentic connected clients tested in this delivery: **0**. Real input requires `--authorized-test-server`, calibrated existing windows and trusted server session evidence. Successful UI input remains an unconfirmed action until independently checked on the server.

See [setup, scenarios and evidence](../../docs/stress-testing.md) and [architecture feasibility and limits](../../docs/client-feasibility.md). Calibration-dependent inventory transfer, reconnect and teleport macros must be supplied for their scenarios; no missing operation is silently replaced by fake gameplay.
