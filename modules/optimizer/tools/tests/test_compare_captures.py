import contextlib
import io
import json
import random
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import compare_captures as cc  # noqa: E402


def samples(n, avg=20.0, p95=30.0, pause=1.0, players=10, entities=3000, chunks=800, seed=1, jitter=1.0):
    rng = random.Random(seed)
    return [{"elapsedSeconds": i + 1, "averageTickMs": avg + rng.uniform(-jitter, jitter), "p95TickMs": p95 + rng.uniform(-jitter, jitter),
             "gcPauseMsDelta": max(0.0, pause + rng.uniform(-.5, .5)), "players": players,
             "loadedEntities": entities, "loadedChunks": chunks} for i in range(n)]


def report(sample_list=None, modules=(), counters=None, hotspots=(), duration=300.0):
    return {"kind": "newfies-optimizer-profile", "durationSeconds": duration,
            "samples": samples(300) if sample_list is None else sample_list,
            "modules": [{"name": n, "status": s, "reason": ""} for n, s in modules],
            "engineCounters": counters or {}, "hotspots": list(hotspots)}


def spot(name, calls=500, total=500.0):
    return {"name": name, "calls": calls, "sampledCalls": calls, "totalMs": total}


def two_runs(base_kw=None, cand_kw=None, module="idleFirepitFastPath", **extra):
    base = report(samples(300, seed=1, **(base_kw or {})), modules=[(module, "disabled")], **extra)
    cand = report(samples(300, seed=2, **(cand_kw or {})), modules=[(module, "active")])
    return cc.compare([base], [cand])


class MannWhitney(unittest.TestCase):
    def test_identical_distributions_are_not_significant(self):
        rng = random.Random(7)
        a = [rng.gauss(10, 1) for _ in range(200)]
        b = [rng.gauss(10, 1) for _ in range(200)]
        self.assertGreater(cc.mann_whitney_p(a, b), 0.05)

    def test_shifted_distribution_is_significant(self):
        rng = random.Random(7)
        a = [rng.gauss(10, 1) for _ in range(200)]
        b = [rng.gauss(11, 1) for _ in range(200)]
        self.assertLess(cc.mann_whitney_p(a, b), 1e-6)

    def test_all_ties_and_empty_inputs_are_safe(self):
        self.assertEqual(cc.mann_whitney_p([5.0] * 20, [5.0] * 20), 1.0)
        self.assertEqual(cc.mann_whitney_p([], [1.0]), 1.0)

    def test_symmetric(self):
        a, b = [1, 2, 3, 4, 5, 6], [4, 5, 6, 7, 8, 9]
        self.assertAlmostEqual(cc.mann_whitney_p(a, b), cc.mann_whitney_p(b, a))


class Verdicts(unittest.TestCase):
    def test_clear_improvement_is_helps_and_names_the_module(self):
        result = two_runs(cand_kw={"avg": 17.0, "p95": 26.0})
        self.assertEqual(result["verdict"], "helps")
        self.assertEqual(result["turnedOn"], ["idleFirepitFastPath"])

    def test_clear_regression_is_hurts(self):
        result = two_runs(cand_kw={"avg": 24.0, "p95": 36.0})
        self.assertEqual(result["verdict"], "hurts")

    def test_same_performance_is_no_effect(self):
        self.assertEqual(two_runs()["verdict"], "no measurable effect")

    def test_tiny_but_detectable_change_does_not_count(self):
        # 0.2 ms is statistically visible with 300 samples but below the 0.5 ms floor.
        result = two_runs(base_kw={"jitter": .05}, cand_kw={"avg": 19.8, "p95": 29.8, "jitter": .05})
        self.assertEqual(result["verdict"], "no measurable effect")

    def test_large_but_noisy_change_is_inconclusive_not_helps(self):
        base = report(samples(30, seed=1, jitter=30.0), modules=[("m", "disabled")])
        cand = report([dict(s, averageTickMs=s["averageTickMs"] - 3, p95TickMs=s["p95TickMs"] - 3)
                       for s in samples(30, seed=2, jitter=30.0)], modules=[("m", "active")])
        result = cc.compare([base], [cand])
        self.assertNotEqual(result["verdict"], "helps")
        self.assertNotEqual(result["verdict"], "hurts")

    def test_gc_only_regression_with_better_tick_is_mixed(self):
        result = two_runs(cand_kw={"avg": 17.0, "p95": 26.0, "pause": 40.0})
        self.assertEqual(result["verdict"], "mixed")

    def test_gc_only_improvement_is_helps(self):
        result = two_runs(base_kw={"pause": 40.0}, cand_kw={"pause": 5.0})
        self.assertEqual(result["verdict"], "helps")

    def test_opposite_tick_metrics_are_mixed(self):
        result = two_runs(cand_kw={"avg": 17.0, "p95": 36.0})
        self.assertEqual(result["verdict"], "mixed")

    def test_new_failure_counter_overrides_a_good_tick_result(self):
        base = report(samples(300, seed=1), modules=[("m", "disabled")])
        cand = report(samples(300, seed=2, avg=15.0, p95=22.0), modules=[("m", "active")],
                      counters={"craftingCandidateFailures": 3})
        result = cc.compare([base], [cand])
        self.assertEqual(result["verdict"], "hurts")
        self.assertEqual(result["newFailureCounters"], ["craftingCandidateFailures"])

    def test_failure_counter_present_in_both_is_not_blamed(self):
        base = report(samples(300, seed=1), modules=[("m", "disabled")], counters={"oldFailures": 2})
        cand = report(samples(300, seed=2, avg=15.0, p95=22.0), modules=[("m", "active")], counters={"oldFailures": 2})
        self.assertEqual(cc.compare([base], [cand])["verdict"], "helps")


class Refusals(unittest.TestCase):
    def test_short_capture_is_inconclusive(self):
        base = report(samples(300, seed=1), modules=[("m", "disabled")], duration=20)
        cand = report(samples(300, seed=2, avg=10.0), modules=[("m", "active")], duration=20)
        result = cc.compare([base], [cand])
        self.assertEqual(result["verdict"], "inconclusive")
        self.assertTrue(any("short" in b for b in result["blockers"]))

    def test_few_samples_is_inconclusive(self):
        base = report(samples(10, seed=1), modules=[("m", "disabled")])
        cand = report(samples(10, seed=2, avg=10.0), modules=[("m", "active")])
        self.assertEqual(cc.compare([base], [cand])["verdict"], "inconclusive")

    def test_different_workload_is_inconclusive(self):
        result = two_runs(cand_kw={"avg": 15.0, "p95": 22.0, "players": 2, "entities": 800})
        self.assertEqual(result["verdict"], "inconclusive")
        self.assertTrue(any("loadedEntities" in b for b in result["blockers"]))

    def test_small_workload_wobble_is_tolerated(self):
        result = two_runs(cand_kw={"avg": 15.0, "p95": 22.0, "players": 11, "entities": 3100})
        self.assertEqual(result["verdict"], "helps")

    def test_several_modules_changed_is_confounded(self):
        base = report(samples(300, seed=1), modules=[("a", "disabled"), ("b", "disabled")])
        cand = report(samples(300, seed=2, avg=15.0, p95=22.0), modules=[("a", "active"), ("b", "active")])
        result = cc.compare([base], [cand])
        self.assertEqual(result["verdict"], "confounded")
        self.assertEqual(result["turnedOn"], ["a", "b"])

    def test_no_module_difference_is_flagged_as_a_noise_check(self):
        base = report(samples(300, seed=1), modules=[("m", "active")])
        cand = report(samples(300, seed=2), modules=[("m", "active")])
        result = cc.compare([base], [cand])
        self.assertTrue(any("run-to-run noise" in n for n in result["notes"]))

    def test_single_pair_warns_about_drift(self):
        self.assertTrue(any("drift" in n for n in two_runs()["notes"]))

    def test_module_that_flapped_in_one_report_is_not_counted_as_on(self):
        base = report(samples(300, seed=1), modules=[("m", "disabled")])
        on, off = report(samples(150, seed=2), modules=[("m", "active")]), report(samples(150, seed=3), modules=[("m", "conflict")])
        result = cc.compare([base], [on, off])
        self.assertEqual(result["turnedOn"], [])


class PooledAndHotspots(unittest.TestCase):
    def test_multiple_captures_per_side_are_pooled(self):
        base = [report(samples(150, seed=s), modules=[("m", "disabled")]) for s in (1, 2)]
        cand = [report(samples(150, seed=s, avg=17.0, p95=26.0), modules=[("m", "active")]) for s in (3, 4)]
        result = cc.compare(base, cand)
        self.assertEqual(result["samples"], {"baseline": 300, "candidate": 300})
        self.assertEqual(result["verdict"], "helps")
        self.assertFalse(any("drift" in n for n in result["notes"]))

    def test_hotspot_that_got_slower_is_listed(self):
        base = report(samples(300, seed=1), modules=[("m", "disabled")], hotspots=[spot("Stage.A", 500, 250.0)])
        cand = report(samples(300, seed=2), modules=[("m", "active")], hotspots=[spot("Stage.A", 500, 600.0)])
        rows = cc.compare([base], [cand])["hotspots"]
        self.assertEqual([r["name"] for r in rows], ["Stage.A"])
        self.assertEqual(rows[0]["direction"], "slower")

    def test_hotspot_with_few_calls_or_small_change_is_ignored(self):
        base = report(samples(300, seed=1), modules=[("m", "disabled")],
                      hotspots=[spot("Few", 10, 5.0), spot("Small", 500, 250.0)])
        cand = report(samples(300, seed=2), modules=[("m", "active")],
                      hotspots=[spot("Few", 10, 50.0), spot("Small", 500, 255.0)])
        self.assertEqual(cc.compare([base], [cand])["hotspots"], [])


class Cli(unittest.TestCase):
    def write(self, directory, name, data):
        path = Path(directory) / name
        path.write_text(json.dumps(data), encoding="utf-8")
        return str(path)

    def run_cli(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = cc.main(list(argv))
        return code, out.getvalue(), err.getvalue()

    def test_text_and_json_output(self):
        with tempfile.TemporaryDirectory() as d:
            off = self.write(d, "off.json", report(samples(300, seed=1), modules=[("m", "disabled")]))
            on = self.write(d, "on.json", report(samples(300, seed=2, avg=17.0, p95=26.0), modules=[("m", "active")]))
            code, text, _ = self.run_cli("--baseline", off, "--candidate", on)
            self.assertEqual(code, 0)
            self.assertIn("VERDICT: HELPS", text)
            code, out, _ = self.run_cli("--baseline", off, "--candidate", on, "--json")
            self.assertEqual(json.loads(out)["verdict"], "helps")

    def test_wrong_file_kind_and_missing_file_fail_cleanly(self):
        with tempfile.TemporaryDirectory() as d:
            bad = self.write(d, "bad.json", {"kind": "other"})
            self.assertEqual(self.run_cli("--baseline", bad, "--candidate", bad)[0], 2)
            self.assertEqual(self.run_cli("--baseline", d + "/none.json", "--candidate", bad)[0], 2)


if __name__ == "__main__":
    unittest.main()

class CadenceRegressions(unittest.TestCase):
    def test_gc_is_normalized_by_observed_interval(self):
        base=report([{ "elapsedSeconds": i+1, "gcPauseMsDelta": 10} for i in range(60)])
        candidate=report([{ "elapsedSeconds": (i+1)*2, "gcPauseMsDelta": 20} for i in range(60)])
        self.assertEqual(cc._metric_values([base], "gcPauseMsDelta"), cc._metric_values([candidate], "gcPauseMsDelta"))
    def test_missing_workload_cannot_prove_improvement(self):
        b=report(samples(300));c=report(samples(300,avg=10,p95=15))
        for s in b["samples"]: s.pop("players")
        self.assertEqual(cc.compare([b],[c])["verdict"], "inconclusive")
