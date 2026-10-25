import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import profile_findings as pf  # noqa: E402

LOAD = "ServerSystemSupplyChunks.mainThreadLoadChunkColumn"
UNLOAD = "ServerSystemUnloadChunks.UnloadChunkColumns"


def tick(count=1000, avg=20.0, median=18.0, p95=30.0, p99=45.0, worst=60.0, overflow=0, tps=30.0):
    return {"count": count, "averageMs": avg, "medianMs": median, "p95Ms": p95, "p99Ms": p99,
            "worstMs": worst, "overflowCount": overflow, "effectiveTps": tps}


def hotspot(name, calls=100, sampled=None, total=100.0, avg=1.0, p95=2.0, worst=5.0):
    return {"name": name, "category": "chunks", "calls": calls, "sampledCalls": calls if sampled is None else sampled,
            "totalMs": total, "averageMs": avg, "p95Ms": p95, "worstMs": worst, "scope": ""}


def report(**overrides):
    base = {"kind": "newfies-optimizer-profile", "optimizerVersion": "test", "gameVersion": "1.22.7",
            "durationSeconds": 300.0, "isRunning": False, "tick": tick(), "samples": [], "hotspots": [],
            "capabilities": [], "modules": [], "overhead": {"hookCalls": 0, "hookWorkMs": 0, "sampleCalls": 0, "sampleWorkMs": 0},
            "samplesOverwritten": 0, "hotspotLookupsOmitted": 0, "engineCounters": {}, "notes": []}
    base.update(overrides)
    return base


def find(result, fragment):
    return next((f for f in result["findings"] if fragment in f["title"]), None)


def sample(elapsed, p95=10.0, pause=0.0, generating=0, chunks=100, heap=None, committed=None, fragmented=None, alloc=0, gen2=0):
    return {"elapsedSeconds": elapsed, "p95TickMs": p95, "gcPauseMsDelta": pause, "generatingChunkColumns": generating,
            "loadedChunks": chunks, "gcCommittedBytes": committed, "gcFragmentedBytes": fragmented,
            "allocatedBytesDelta": alloc, "gc2Delta": gen2, "managedHeapBytes": heap}


class StageTests(unittest.TestCase):
    def test_dominant_exact_stage_is_a_problem_with_share(self):
        # 1000 ticks * 20 ms = 20,000 ms of work; the stage used 8,000 ms of it.
        r = report(tick=tick(count=1000, avg=20.0, worst=400.0),
                   hotspots=[hotspot(LOAD, calls=200, total=8000.0, avg=40.0, p95=90.0, worst=250.0)])
        result = pf.analyze(r)
        f = find(result, "Chunk-column load completion")
        self.assertEqual(f["severity"], "problem")
        self.assertIn("40.0% of main-thread tick work", f["evidence"])
        self.assertIn("worst call = 62% of the worst tick", f["evidence"])
        row = result["stages"][0]
        self.assertTrue(row["exact"])
        self.assertAlmostEqual(row["shareOfMainThreadWork"], 40.0)

    def test_sampled_stage_is_never_extrapolated(self):
        r = report(tick=tick(count=1000, avg=20.0, worst=400.0),
                   hotspots=[hotspot(UNLOAD, calls=1000, sampled=100, total=9000.0, avg=90.0, worst=20.0)])
        result = pf.analyze(r)
        f = find(result, "Chunk unload")
        self.assertIsNone(result["stages"][0]["shareOfMainThreadWork"])
        self.assertIn("lower bound", f["evidence"])
        self.assertEqual(f["severity"], "info")  # no share, and a 20 ms worst is not a spike of the 400 ms worst tick

    def test_small_stage_is_informational(self):
        r = report(hotspots=[hotspot(LOAD, calls=500, total=50.0, avg=0.1, p95=0.25, worst=2.0)])
        self.assertEqual(find(pf.analyze(r), "Chunk-column load completion")["severity"], "info")

    def test_spike_without_large_total_is_a_watch_or_problem(self):
        r = report(tick=tick(count=1000, avg=20.0, worst=300.0),
                   hotspots=[hotspot(LOAD, calls=50, total=900.0, avg=18.0, worst=200.0)])
        self.assertEqual(find(pf.analyze(r), "Chunk-column load completion")["severity"], "problem")
        r = report(tick=tick(count=1000, avg=20.0, worst=300.0),
                   hotspots=[hotspot(LOAD, calls=50, total=300.0, avg=6.0, worst=90.0)])
        self.assertEqual(find(pf.analyze(r), "Chunk-column load completion")["severity"], "watch")

    def test_share_alone_decides_problem_and_watch(self):
        # No single call is a spike (worst 30 ms vs a 400 ms worst tick), so only the share can trigger.
        base = dict(tick=tick(count=1000, avg=20.0, worst=400.0))
        problem = report(hotspots=[hotspot(LOAD, calls=1000, total=6000.0, avg=6.0, worst=30.0)], **base)  # 30%
        watch = report(hotspots=[hotspot(LOAD, calls=1000, total=2400.0, avg=2.4, worst=30.0)], **base)    # 12%
        quiet = report(hotspots=[hotspot(LOAD, calls=1000, total=1000.0, avg=1.0, worst=30.0)], **base)   # 5%
        self.assertEqual(find(pf.analyze(problem), "Chunk-column load completion")["severity"], "problem")
        self.assertEqual(find(pf.analyze(watch), "Chunk-column load completion")["severity"], "watch")
        self.assertEqual(find(pf.analyze(quiet), "Chunk-column load completion")["severity"], "info")

    def test_spike_rule_alone_needs_an_absolute_floor(self):
        # 40% of the worst tick but only 10 ms: too small to matter, whatever the ratio.
        r = report(tick=tick(count=1000, avg=20.0, worst=25.0), hotspots=[hotspot(LOAD, calls=1000, total=50.0, avg=.05, worst=10.0)])
        self.assertEqual(find(pf.analyze(r), "Chunk-column load completion")["severity"], "info")

    def test_untimed_stages_explain_how_to_enable(self):
        r = report(modules=[{"name": "chunkLifecycleTimings", "status": "disabled", "reason": "safe mode"}])
        f = find(pf.analyze(r), "Chunk stages were not timed")
        self.assertIn("disabled: safe mode", f["evidence"])
        self.assertIn("PhaseTimings", f["next"])
        self.assertEqual(pf.analyze(r)["stages"], [])


class TickTests(unittest.TestCase):
    def test_sustained_overload(self):
        f = find(pf.analyze(report(tick=tick(avg=40.0, median=38.0, p95=70.0, worst=120.0))), "Sustained overload")
        self.assertEqual(f["severity"], "problem")

    def test_regularly_over_budget(self):
        f = find(pf.analyze(report(tick=tick(avg=25.0, p95=50.0, worst=90.0))), "regularly run over budget")
        self.assertEqual(f["severity"], "watch")

    def test_spikes_report_the_tail_ratio(self):
        f = find(pf.analyze(report(tick=tick(avg=15.0, median=14.0, p95=25.0, worst=210.0))), "Occasional long ticks")
        self.assertIn("15x the median", f["title"])

    def test_calm_server(self):
        self.assertEqual(find(pf.analyze(report()), "within budget")["severity"], "info")

    def test_no_ticks(self):
        self.assertIsNotNone(find(pf.analyze(report(tick={"count": 0})), "No completed ticks"))

    def test_budget_is_configurable(self):
        f = find(pf.analyze(report(tick=tick(avg=20.0)), tick_budget_ms=16.0), "Sustained overload")
        self.assertIsNotNone(f)


class GcTests(unittest.TestCase):
    def test_pause_share_is_a_problem_above_five_percent(self):
        samples = [sample(i * 10, pause=600.0, alloc=50 * 1048576) for i in range(1, 11)]
        r = report(durationSeconds=100.0, samples=samples)
        result = pf.analyze(r)
        f = find(result, "GC pauses take a large share")
        self.assertEqual(f["severity"], "problem")
        self.assertAlmostEqual(result["gc"]["pausePercentOfWall"], 6.0)
        self.assertAlmostEqual(result["gc"]["allocMiBPerSecond"], 5.0)
        self.assertIn("Forced collections", f["notEstablished"])

    def test_low_pause_is_informational_and_fragmentation_is_flagged(self):
        samples = [sample(10, pause=1.0, committed=1000, fragmented=400)]
        result = pf.analyze(report(durationSeconds=100.0, samples=samples))
        self.assertEqual(find(result, "GC pause share is low")["severity"], "info")
        self.assertIn("40%", find(result, "fragmented")["evidence"])
        self.assertIn("not proof of a leak", find(result, "fragmented")["notEstablished"])


class CorrelationTests(unittest.TestCase):
    def build(self, slow_pause, slow_generating=0):
        samples = [sample(i, p95=10.0) for i in range(20)]
        samples += [sample(100 + i, p95=120.0, pause=slow_pause, generating=slow_generating) for i in range(5)]
        return report(durationSeconds=200.0, samples=samples)

    def test_slow_samples_that_coincide_with_gc_are_reported(self):
        f = find(pf.analyze(self.build(slow_pause=60.0)), "line up with a GC pause")
        self.assertEqual(f["severity"], "watch")
        self.assertIn("5 of 5 slow samples", f["evidence"])
        self.assertIn("not causation", f["notEstablished"])

    def test_no_coincidence_no_finding(self):
        self.assertIsNone(find(pf.analyze(self.build(slow_pause=0.0)), "line up with"))

    def test_chunk_generation_coincidence(self):
        self.assertIsNotNone(find(pf.analyze(self.build(slow_pause=0.0, slow_generating=3)), "queued chunk generation"))

    def test_baseline_rate_must_be_lower(self):
        samples = [sample(i, p95=10.0, pause=60.0) for i in range(20)] + [sample(100 + i, p95=120.0, pause=60.0) for i in range(5)]
        self.assertIsNone(find(pf.analyze(report(durationSeconds=200.0, samples=samples)), "line up with a GC pause"))

    def test_too_few_slow_samples_are_ignored(self):
        samples = [sample(i, p95=10.0) for i in range(20)] + [sample(100, p95=120.0, pause=60.0), sample(101, p95=120.0, pause=60.0)]
        self.assertIsNone(find(pf.analyze(report(durationSeconds=200.0, samples=samples)), "line up with"))


class ModuleAndQualityTests(unittest.TestCase):
    def test_module_problems_and_failure_counters(self):
        r = report(modules=[{"name": "phaseTimings", "status": "conflict", "reason": "foreign owner"},
                            {"name": "tickMetrics", "status": "active", "reason": ""}],
                   engineCounters={"entityLifecycleFailures": 2, "otherCalls": 9, "barrelFailures": 0})
        result = pf.analyze(r)
        self.assertEqual(find(result, "phaseTimings is conflict")["evidence"], "foreign owner")
        self.assertEqual(find(result, "failures: entityLifecycleFailures")["severity"], "problem")
        self.assertIsNone(find(result, "barrelFailures"))

    def test_capture_quality_notes(self):
        r = report(durationSeconds=20.0, isRunning=True, samplesOverwritten=7, hotspotLookupsOmitted=3,
                   tick=tick(overflow=4), overhead={"hookWorkMs": 300, "sampleWorkMs": 200})
        result = pf.analyze(r)
        for fragment in ("Short capture", "still running", "overwritten", "omitted", "histogram range", "self-timing"):
            self.assertIsNotNone(find(result, fragment), fragment)

    def test_findings_are_sorted_most_severe_first(self):
        r = report(tick=tick(avg=40.0), durationSeconds=20.0)
        severities = [f["severity"] for f in pf.analyze(r)["findings"]]
        self.assertEqual(severities, sorted(severities, key=lambda s: pf.SEVERITY_ORDER[s]))
        self.assertEqual(severities[0], "problem")

    def test_missing_and_null_fields_do_not_crash(self):
        r = {"kind": "newfies-optimizer-profile", "tick": {"count": 5, "averageMs": None, "worstMs": None},
             "samples": [{"elapsedSeconds": 1, "p95TickMs": None}], "hotspots": [{"name": LOAD, "calls": None}],
             "modules": [None, {"name": "x"}], "engineCounters": {"a": None}}
        result = pf.analyze(r)
        self.assertIn("findings", result)
        self.assertIn("Chunk", pf.render_text(result) + "Chunk")


class CliTests(unittest.TestCase):
    def run_cli(self, text, *args):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "latest.json"
            path.write_text(text, encoding="utf-8")
            out, err = io.StringIO(), io.StringIO()
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                code = pf.main([str(path), *args])
            return code, out.getvalue(), err.getvalue()

    def test_text_and_json_output(self):
        r = report(hotspots=[hotspot(LOAD, total=8000.0, calls=200, worst=250.0)], tick=tick(worst=400.0))
        code, out, _ = self.run_cli(json.dumps(r))
        self.assertEqual(code, 0)
        self.assertIn("[PROBLEM] Chunk-column load completion stage", out)
        self.assertIn("Chunk lifecycle stages", out)
        code, out, _ = self.run_cli(json.dumps(r), "--json")
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(out)["stages"][0]["method"], LOAD)

    def test_rejects_other_files_and_bad_json(self):
        code, _, err = self.run_cli(json.dumps({"kind": "something-else"}))
        self.assertEqual(code, 2)
        self.assertIn("optimizer profile", err)
        code, _, err = self.run_cli("{not json")
        self.assertEqual(code, 2)
        self.assertEqual(pf.main(["/nonexistent/latest.json"]), 2)


if __name__ == "__main__":
    unittest.main()

class RetainedWindowRegressions(unittest.TestCase):
    def test_ring_window_uses_retained_duration(self):
        r=report(durationSeconds=10000,samplesOverwritten=9000,samples=[sample(9000+i,pause=100) for i in range(101)])
        _,stats=pf.gc_findings(r)
        self.assertAlmostEqual(stats["pausePercentOfWall"],10)
    def test_missing_gc_samples_are_not_low_gc_evidence(self):
        findings,stats=pf.gc_findings(report(samples=[{"elapsedSeconds": 10,"gcPauseMsDelta":None}]))
        self.assertEqual(stats,{})
        self.assertIn("unavailable",findings[0]["title"])
