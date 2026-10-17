import importlib.util
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

MODULE_PATH = Path(__file__).resolve().parents[1] / "stress_tester.py"
spec = importlib.util.spec_from_file_location("stress_tester", MODULE_PATH)
st = importlib.util.module_from_spec(spec)
spec.loader.exec_module(st)


class StressTesterTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.config_path = self.root / "config.json"
        data = json.loads((MODULE_PATH.parent / "example.json").read_text())
        data["clients"][0]["id"] = "player-one"
        data["limits"]["actionIntervalSeconds"] = 0.05
        self.config_path.write_text(json.dumps(data))
        self.config = st.load_config(self.config_path)

    def tearDown(self):
        self.directory.cleanup()

    def write_config(self, mutate):
        data = json.loads(self.config_path.read_text())
        mutate(data)
        self.config_path.write_text(json.dumps(data))
        return st.load_config(self.config_path)

    def evidence(self):
        return st.Evidence(self.root / "events.jsonl", "trial", ["player-one"], self.config["limits"])

    def append(self, **data):
        data.setdefault("schemaVersion", 1)
        data.setdefault("clientId", "player-one")
        data.setdefault("utc", st.utc_now())
        data.setdefault("runId", "trial")
        with (self.root / "events.jsonl").open("a") as output:
            output.write(json.dumps(data) + "\n")

    def test_example_is_valid(self):
        self.assertIn("mine", self.config["macros"])
        self.assertEqual(1, len(self.config["clients"]))

    def test_duplicate_clients_rejected(self):
        with self.assertRaises(st.ConfigError):
            self.write_config(lambda data: data["clients"].append(dict(data["clients"][0])))

    def test_command_requires_exact_explicit_allowlist(self):
        with self.assertRaises(st.ConfigError):
            self.write_config(lambda data: data["macros"].update(teleport=[{"op": "command", "text": "/tp 0 100 0"}]))
        config = self.write_config(lambda data: data.update(allowedCommands=["/tp 0 100 0"]))
        self.assertIn("teleport", config["macros"])

    def test_newline_in_command_rejected(self):
        with self.assertRaises(st.ConfigError):
            self.write_config(lambda data: data.update(allowedCommands=["/tp 0 100 0\n/op stranger"],
                  macros={"teleport": [{"op": "command", "text": "/tp 0 100 0\n/op stranger"}]}))

    def test_unbounded_limits_and_nonfinite_durations_rejected(self):
        for value in [float("nan"), float("inf"), 1000000, True]:
            with self.subTest(value=value), self.assertRaises(st.ConfigError):
                st.number(value, "duration", 0.1, 100)

    def test_scene_and_manual_macros_are_required(self):
        with self.assertRaises(st.ConfigError):
            st.actions_for(self.config, "mob_horde")
        with self.assertRaises(st.ConfigError):
            st.actions_for(self.config, "rapid_exploration")

    def test_seeded_schedule_is_reproducible_and_client_specific(self):
        weighted = st.SCENARIOS["concentrated"]
        def sample(client):
            choices = st.seeded_choices(72, client, weighted)
            return [next(choices) for _ in range(30)]
        self.assertEqual(sample("a"), sample("a"))
        self.assertNotEqual(sample("a"), sample("b"))

    def test_evidence_presence_expiry_and_disconnect(self):
        evidence = self.evidence()
        self.append(kind="presence")
        evidence.poll()
        self.assertTrue(evidence.all_online())
        self.assertEqual(1, evidence.peak)
        evidence.begin_workload()
        evidence.poll(time.time() + 31)
        self.assertFalse(evidence.all_online())
        self.assertEqual(0, evidence.workload_minimum)
        self.assertEqual(1, evidence.disconnects)

    def test_stale_connection_and_foreign_run_do_not_authenticate(self):
        evidence = self.evidence()
        self.append(kind="connected", utc="2000-01-01T00:00:00Z")
        self.append(kind="connected", runId="someone-else")
        evidence.poll()
        self.assertEqual(0, evidence.peak)

    def test_ack_requires_expected_action_owner_and_run(self):
        evidence = self.evidence()
        evidence.expect("trial:player-one:1", "player-one")
        self.append(kind="action_confirmed", actionId="trial:player-one:1", runId="different")
        self.append(kind="action_confirmed", actionId="unsolicited")
        evidence.poll()
        self.assertEqual(0, evidence.ack_count)
        self.append(kind="action_confirmed", actionId="trial:player-one:1")
        self.append(kind="action_confirmed", actionId="trial:player-one:1")
        evidence.poll()
        self.assertEqual(1, evidence.ack_count)

    def test_completed_work_types_use_only_accepted_attempt_attribution(self):
        evidence = self.evidence()
        evidence.expect("trial:player-one:0", "player-one", "move")
        evidence.expect("trial:player-one:1", "player-one", "mine")
        evidence.expect("trial:player-one:2", "player-one", "mine")
        evidence.expect("trial:player-one:3", "player-one", "place")
        self.append(kind="action_confirmed", actionId="trial:player-one:0", actionType="mine")
        self.append(kind="action_confirmed", actionId="trial:player-one:1")
        self.append(kind="action_confirmed", actionId="trial:player-one:1")
        self.append(kind="action_failed", actionId="trial:player-one:2")
        self.append(kind="action_confirmed", actionId="trial:player-one:3", runId="foreign")
        evidence.poll()
        self.assertEqual({"move": 1, "mine": 1}, evidence.work_completed_by_type)
        self.assertEqual(evidence.ack_count, sum(evidence.work_completed_by_type.values()))
        self.assertEqual("move", evidence.acks["trial:player-one:0"]["actionType"])
        self.assertEqual(1, evidence.failure_count)

    def test_partial_json_line_waits_for_completion(self):
        evidence = self.evidence()
        target = self.root / "events.jsonl"
        line = json.dumps({"schemaVersion": 1, "kind": "presence", "clientId": "player-one", "utc": st.utc_now()})
        target.write_text(line[:12])
        evidence.poll()
        self.assertEqual(0, evidence.offset)
        with target.open("a") as output:
            output.write(line[12:] + "\n")
        evidence.poll()
        self.assertTrue(evidence.all_online())

    def test_replaced_evidence_fails_closed(self):
        evidence = self.evidence()
        self.append(kind="presence")
        evidence.poll()
        (self.root / "events.jsonl").unlink()
        (self.root / "events.jsonl").write_text("")
        evidence.poll()
        self.assertIsNotNone(evidence.error)

    def test_dry_run_cannot_claim_authentication_or_gameplay(self):
        report = st.run_workload(self.config, "idle", 1, 12, 0.13, "dry", self.root / "out", dry_run=True)
        self.assertEqual("simulated_tooling_only", report["measurementKind"])
        self.assertEqual(0, report["actualConnectedPeak"])
        self.assertEqual(0, report["actualConnectedMinimum"])
        self.assertFalse(report["authenticatedSessionEvidence"])
        self.assertEqual(0, report["actionsConfirmed"])
        self.assertEqual({}, report["workCompletedByType"])
        self.assertGreater(report["actionsAttempted"], 0)
        self.assertEqual({}, report["correctnessChecks"])
        self.assertTrue((self.root / "out" / "generator-resources.jsonl").exists())

    def test_real_requires_authorization_and_server_evidence(self):
        with self.assertRaises(st.ConfigError):
            st.run_workload(self.config, "idle", 1, 12, 1, "real", self.root / "out")
        self.config["serverEvidenceFile"] = None
        with self.assertRaises(st.ConfigError):
            st.run_workload(self.config, "idle", 1, 12, 1, "real", self.root / "out", authorized=True)

    def test_held_input_released_when_keydown_fails(self):
        client = self.config["clients"][0]
        backend = st.X11Backend(client, threading.Event(), time.monotonic() + 5, "t")
        calls = []
        def command(*args):
            calls.append(args)
            if args[0] == "keydown":
                raise RuntimeError("display disconnected")
            return ""
        with patch.object(backend, "focus"), patch.object(backend, "assert_focus"), patch.object(backend, "command", command):
            with self.assertRaises(RuntimeError):
                backend.perform([{"op": "hold", "keys": ["w"], "seconds": 0.1}])
        self.assertIn(("keyup", "--", "w"), calls)
        self.assertFalse(backend.keys)

    def test_action_budget_expiry_still_releases_input(self):
        backend = st.X11Backend(self.config["clients"][0], threading.Event(), time.monotonic() + 5, "t", 0.01)
        calls = []
        with patch.object(backend, "focus"), patch.object(backend, "assert_focus"), patch.object(backend, "command", lambda *args: calls.append(args)):
            with self.assertRaises(TimeoutError):
                backend.perform([{"op": "hold", "keys": ["w"], "seconds": 0.05}])
        self.assertIn(("keyup", "--", "w"), calls)
        self.assertFalse(backend.keys)

    def test_missing_evidence_after_start_fails_closed(self):
        evidence = self.evidence()
        self.append(kind="presence")
        evidence.poll()
        (self.root / "events.jsonl").unlink()
        evidence.poll()
        self.assertIn("disappeared", evidence.error)

    def test_authentication_must_be_explicitly_enabled_on_server(self):
        evidence = self.evidence()
        self.append(kind="presence")
        evidence.poll()
        evidence.begin_workload()
        self.assertFalse(evidence.authenticated_throughout)
        self.append(kind="presence", authenticationEnabled=True)
        evidence.poll()
        # An initial unproven/auth-off interval cannot be repaired by a later event.
        self.assertFalse(evidence.authenticated_throughout)
        evidence.begin_workload()
        self.assertTrue(evidence.authenticated_throughout)
        self.append(kind="presence", authenticationEnabled=False)
        evidence.poll()
        self.assertFalse(evidence.authenticated_throughout)

    def test_wrong_client_cannot_acknowledge_another_clients_action(self):
        evidence = st.Evidence(self.root / "events.jsonl", "trial", ["player-one", "player-two"], self.config["limits"])
        evidence.expect("trial:player-one:0", "player-one")
        self.append(kind="action_confirmed", actionId="trial:player-one:0", clientId="player-two")
        evidence.poll()
        self.assertEqual(0, evidence.ack_count)

    def test_transient_disconnect_between_heartbeats_reduces_minimum(self):
        evidence = self.evidence()
        self.append(kind="presence", authenticationEnabled=True)
        evidence.poll()
        evidence.begin_workload()
        self.append(kind="disconnected")
        self.append(kind="presence", authenticationEnabled=True)
        evidence.poll()
        self.assertTrue(evidence.all_online())
        self.assertEqual(0, evidence.workload_minimum)

    def test_event_file_has_hard_bound(self):
        sink = st.EventSink(self.root / "bounded.jsonl", 1)
        try:
            sink.emit(kind="one")
            with self.assertRaises(RuntimeError):
                sink.emit(kind="two")
        finally:
            sink.close()
        self.assertEqual(1, len((self.root / "bounded.jsonl").read_text().splitlines()))

    def test_proc_resource_reading(self):
        sample = st.process_sample(__import__("os").getpid())
        self.assertIn("cpuSeconds", sample)
        self.assertGreater(sample.get("VmRSSBytes", 0), 0)


if __name__ == "__main__":
    unittest.main()
