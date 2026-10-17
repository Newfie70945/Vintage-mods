import copy
import json
import math
import sys
import tempfile
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import benchmark
from report import build_comparison, compact_trial, describe, percentile, read_profiler, write_outputs


class BenchmarkTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / "snapshot").mkdir()
        (self.root / "snapshot" / "world.db").write_bytes(b"frozen world fixture")
        self.config = {"schemaVersion": 1, "gameVersion": "1.22.7", "scenario": "idle tooling test",
            "worldSnapshot": "snapshot", "resources": {"cpuQuota": "1 core", "memoryLimitBytes": 1000000000, "storageClass": "test"},
            "repeats": 3, "warmupSeconds": 1, "measurementSeconds": 1, "startupTimeoutSeconds": 3, "shutdownTimeoutSeconds": 3,
            "seed": 123, "validatedOptimizations": {}, "variants": {}}
        server_code = '''import sys,json,time\nfrom pathlib import Path\nprint("TEST READY",flush=True)\nstart=time.monotonic()\nfor line in sys.stdin:\n print("Handling Console Command " + line.strip(),flush=True)\n if line.strip()=="start": start=time.monotonic()\n if line.strip()=="export":\n  Path("profile.json").write_text(json.dumps({"schemaVersion":1,"kind":"newfies-optimizer-profile","durationSeconds":time.monotonic()-start,"isRunning":False,"tick":{"count":30,"averageMs":1,"medianMs":1,"p95Ms":1,"p99Ms":1,"worstMs":1,"effectiveTps":30},"samples":[]}))\n if line.strip()=="stop": break\n'''
        for name in benchmark.VARIANTS:
            template = self.root / name
            template.mkdir()
            (template / ".nos-benchmark-clone.json").write_text('{"purpose":"nos-local-benchmark","allowExecution":true}')
            for file, content in (("game.bin", "same official game fixture"), ("config.json", "{}"), ("mods.list", "same mod fixtures"), ("optimizer.bin", "same optimizer fixture"), ("server.py", server_code)):
                (template / file).write_text(content)
            if name == "A":
                (template / "optimizer.bin").unlink()
            self.config["variants"][name] = {"templateDirectory": name, "worldRelativePath": "world",
                "gamePaths": ["game.bin"], "configPaths": ["config.json"], "modPaths": ["mods.list"],
                "optimizerPaths": [] if name == "A" else ["optimizer.bin"],
                "profilerInstalled": name != "A", "enabledOptimizations": ["fixture"] if name == "C" else [],
                "serverCommand": [sys.executable, "server.py"], "readyPattern": "TEST READY",
                "startMeasurementStdin": ["start"], "stopMeasurementStdin": ["export"], "shutdownStdin": ["stop"],
                "profilerReport": "profile.json"}

    def tearDown(self):
        self.temp.cleanup()

    def test_percentiles_ignore_missing_and_nonfinite(self):
        self.assertEqual(percentile([1, 2, 3, 4], 50), 2.5)
        self.assertAlmostEqual(percentile([1, 2, 3, 4], 95), 3.85)
        self.assertEqual(describe([None, math.nan, math.inf, True])["count"], 0)
        self.assertIsNone(describe([1])["sampleStdev"])

    def test_reproducible_block_randomization(self):
        plan = benchmark.make_plan(self.config)
        self.assertEqual(plan, benchmark.make_plan(self.config))
        self.assertEqual(len(plan), 12)
        for repeat in range(1, 4):
            block = [t for t in plan if t["repeat"] == repeat]
            self.assertEqual({t["variant"] for t in block}, set("ABCD"))
            self.assertEqual(len({t["seed"] for t in block}), 1)

    def test_identity_and_mismatch_rejected(self):
        identity = benchmark.validate_config(self.config, self.root)
        self.assertEqual(identity["variants"]["A"]["gamePaths"], identity["variants"]["B"]["gamePaths"])
        (self.root / "C" / "mods.list").write_text("different pack")
        with self.assertRaisesRegex(ValueError, "Mismatched modPaths"):
            benchmark.validate_config(self.config, self.root)

    def test_unvalidated_combined_optimization_rejected(self):
        self.config["variants"]["D"]["enabledOptimizations"] = ["unvalidated"]
        with self.assertRaisesRegex(ValueError, "without prior validation"):
            benchmark.validate_config(self.config, self.root)

    def test_baseline_and_single_change_constraints(self):
        self.config["variants"]["B"]["enabledOptimizations"] = ["oops"]
        with self.assertRaisesRegex(ValueError, "disable every"):
            benchmark.validate_config(self.config, self.root)
        self.config["variants"]["B"]["enabledOptimizations"] = []
        self.config["variants"]["C"]["enabledOptimizations"] = ["one", "two"]
        with self.assertRaisesRegex(ValueError, "exactly one"):
            benchmark.validate_config(self.config, self.root)

    def test_path_escape_and_symlinks_rejected(self):
        with self.assertRaises(ValueError):
            benchmark.contained(self.root, "../outside")
        try:
            (self.root / "A" / "link").symlink_to(self.root / "snapshot")
        except OSError:
            self.skipTest("symlinks unavailable")
        with self.assertRaisesRegex(ValueError, "Symlink"):
            benchmark.validate_config(self.config, self.root)

    def test_missing_profiler_never_becomes_zero(self):
        profile, missing = read_profiler(self.root / "missing.json", 30)
        self.assertIsNone(profile)
        self.assertTrue(missing)

    def test_synthetic_or_idle_never_qualifies(self):
        trials = [{"variant": v, "repeat": repeat, "seed": repeat, "status": "completed",
            "profiler": {"tick": {"averageMs": 1 if v == "B" else .1}}, "workload": {"actualConnectedPeak": 0}}
            for repeat in range(3) for v in "ABCD"]
        report = build_comparison(trials, {"identityValidated": True, "synthetic": True})
        self.assertTrue(all(not item["eligibleForReview"] for item in report["comparisons"]))
        write_outputs(report, self.root / "report")
        self.assertTrue((self.root / "report" / "trials.csv").is_file())

    def valid_comparison_fixture(self):
        """Unit-only made-up structures to verify gates; never written as results."""
        checks = {name: True for name in ("saveReloadIntegrity", "inventoryCorrectness", "simulationTiming", "permissions", "combatCorrectness")}
        trials = []
        for repeat in range(3):
            for variant in "BC":
                trials.append({"id": f"unit-only-{repeat}-{variant}", "repeat": repeat, "variant": variant, "seed": repeat,
                    "status": "completed", "environment": {"source": "unit fixture only"},
                    "measurementStartedUtc": "2000-01-01T00:00:00Z", "measurementStoppedUtc": "2000-01-01T00:30:00Z",
                    "profiler": {"schemaVersion": 1, "kind": "newfies-optimizer-profile", "gameVersion": "1.22.7",
                        "durationSeconds": 1800, "startedUtc": "2000-01-01T00:00:00Z", "stoppedUtc": "2000-01-01T00:30:00Z", "isRunning": False,
                        "tick": {"count": 54000, "averageMs": 1, "medianMs": 1, "p95Ms": 2, "p99Ms": 3, "worstMs": 4, "effectiveTps": 30, "source": "unit fixture"}, "samples": []},
                    "workload": {"schemaVersion": 1, "measurementKind": "official_client_workload", "authenticatedSessionEvidence": True,
                        "actualConnectedMinimum": 1, "actualConnectedPeak": 1, "configuredClients": 1,
                        "requestedDurationSeconds": 1800, "workloadDurationSeconds": 1800 + repeat * .001,
                        "workloadStartedUtc": "2000-01-01T00:00:00Z", "workloadCompletedUtc": "2000-01-01T00:30:00Z",
                        "actionsConfirmed": 10, "actionsFailed": 0, "pendingUnconfirmed": 0, "connectionFailures": 0,
                        "workCompletedByType": {"mine": 10},
                        "disconnects": 0, "interactionFailures": 0, "status": "passed", "errors": [], "scenario": "unit fixture", "correctnessChecks": checks},
                    "health": {"serverErrors": 0, "queueGrowth": False}, "missingMetrics": []})
        return trials

    def test_valid_structural_gate_and_duration_tolerance(self):
        trials = self.valid_comparison_fixture()
        trials[-1]["workload"]["workloadDurationSeconds"] += .003
        report = build_comparison(trials, {"identityValidated": True, "measurementSeconds": 1800, "scenario": "unit fixture"})
        pair = next(item for item in report["comparisons"] if item["baseline"] == "B" and item["candidate"] == "C")
        self.assertTrue(pair["eligibleForReview"], pair["blockingReasons"])
        self.assertIn("manual", pair["conclusion"])

    def test_missing_profiler_blocks_otherwise_complete_metadata(self):
        trials = self.valid_comparison_fixture()
        for trial in trials:
            trial["profiler"] = None
        report = build_comparison(trials, {"identityValidated": True, "measurementSeconds": 1800})
        pair = next(item for item in report["comparisons"] if item["baseline"] == "B" and item["candidate"] == "C")
        self.assertFalse(pair["eligibleForReview"])
        self.assertIn("required profiler measurements are unavailable or misaligned", pair["blockingReasons"])

    def test_synthetic_flag_alone_blocks_complete_structural_fixture(self):
        report = build_comparison(self.valid_comparison_fixture(), {"identityValidated": True, "synthetic": True, "measurementSeconds": 1800})
        pair = next(item for item in report["comparisons"] if item["baseline"] == "B" and item["candidate"] == "C")
        self.assertFalse(pair["eligibleForReview"])
        self.assertIn("synthetic fixtures are not measured server results", pair["blockingReasons"])

    def test_campaign_preview_bounds_do_not_mutate_raw_history(self):
        trial = {"profiler": {"samples": [{"i": i} for i in range(1000)]}}
        view = compact_trial(trial)
        self.assertEqual(len(view["profiler"]["samples"]), 128)
        self.assertEqual(len(trial["profiler"]["samples"]), 1000)
        self.assertEqual(view["profiler"]["samples"][0]["i"], 0)
        self.assertEqual(view["profiler"]["samples"][-1]["i"], 999)

    def test_workload_early_success_exit_is_not_completed_load(self):
        server = benchmark.ProcessCapture([sys.executable, "-c", "import time; time.sleep(10)"], self.root, self.root / "server.log")
        driver = benchmark.ProcessCapture([sys.executable, "-c", "pass"], self.root, self.root / "driver.log")
        driver.process.wait(timeout=3)
        try:
            with self.assertRaisesRegex(RuntimeError, "before its measurement"):
                benchmark.wait_phase(server, 5, None, driver)
        finally:
            driver.stop(2)
            server.stop(2)

    def test_local_process_boundaries_and_fresh_snapshot(self):
        identity = benchmark.validate_config(self.config, self.root)
        trial = benchmark.run_trial(self.config, self.root, self.root / "trial", {"id": "test-C", "repeat": 1,"variant": "C", "seed": 42}, identity)
        self.assertEqual(trial["status"], "completed", trial["failures"])
        self.assertEqual(trial["profiler"]["tick"]["count"], 30)
        self.assertEqual((self.root / "trial" / "clone" / "world" / "world.db").read_bytes(), b"frozen world fixture")
        self.assertFalse((self.root / "C" / "world").exists())
        self.assertIsNone(trial["workload"])


if __name__ == "__main__":
    unittest.main()
