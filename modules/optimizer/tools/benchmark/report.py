"""Honest comparison of profiler exports and independently evidenced workloads.

Only the Python standard library is required. No collection happens over the network.
"""
from __future__ import annotations

import json
import math
import os
import statistics
from pathlib import Path


def finite_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def percentile(values, percent):
    """Linear interpolation over sorted observations (Hyndman/Fan type 7)."""
    values = sorted(float(x) for x in values if finite_number(x))
    if not values:
        return None
    position = (len(values) - 1) * percent / 100
    low = math.floor(position)
    high = math.ceil(position)
    return values[low] + (values[high] - values[low]) * (position - low)


def describe(values):
    values = [float(x) for x in values if finite_number(x)]
    if not values:
        return {"count": 0, "mean": None, "median": None, "p95": None,
                "p99": None, "min": None, "max": None, "sampleStdev": None}
    return {"count": len(values), "mean": statistics.mean(values),
            "median": statistics.median(values), "p95": percentile(values, 95),
            "p99": percentile(values, 99), "min": min(values), "max": max(values),
            "sampleStdev": statistics.stdev(values) if len(values) > 1 else None}


def load_object(path, max_bytes=64 * 1024 * 1024):
    path = Path(path)
    if path.stat().st_size > max_bytes:
        raise ValueError(f"JSON exceeds {max_bytes} bytes: {path.name}")
    def reject_constant(value):
        raise ValueError(f"Non-finite JSON number: {value}")
    data = json.loads(path.read_text(encoding="utf-8-sig"), parse_constant=reject_constant)
    if not isinstance(data, dict):
        raise ValueError("JSON root must be an object")
    return data


def read_profiler(path, expected_duration=None):
    missing = []
    if not path or not Path(path).is_file():
        return None, ["profiler export unavailable"]
    profile = load_object(path)
    return validate_profiler(profile, expected_duration)


def validate_profiler(profile, expected_duration=None):
    """Validate again during comparison; imported trial annotations are not trusted."""
    missing = []
    if not isinstance(profile, dict):
        return None, ["profiler export unavailable"]
    if profile.get("schemaVersion") != 1 or profile.get("kind") != "newfies-optimizer-profile":
        raise ValueError("Unsupported profiler export schema/kind")
    if profile.get("gameVersion") != "1.22.7":
        missing.append("game version differs from target or is unavailable")
    tick = profile.get("tick") or {}
    if not isinstance(tick, dict) or not isinstance(profile.get("samples", []), list):
        raise ValueError("Malformed profiler tick/samples fields")
    for field in ("count", "averageMs", "medianMs", "p95Ms", "p99Ms", "worstMs", "effectiveTps"):
        if not finite_number(tick.get(field)) or tick[field] < 0:
            missing.append("tick." + field)
    if not finite_number(tick.get("count")) or tick["count"] <= 0:
        missing.append("no measured ticks")
    duration = profile.get("durationSeconds")
    if expected_duration is not None and (not finite_number(duration) or
            abs(duration - expected_duration) > max(2, expected_duration * .05)):
        missing.append("profiler duration does not match measurement period")
    if profile.get("isRunning") is not False:
        missing.append("profiler was not stopped before export")
    profile["missingMetrics"] = missing
    return profile, missing


def read_workload(path):
    if not path or not Path(path).is_file():
        return None
    result = load_object(path)
    if result.get("schemaVersion") != 1:
        raise ValueError("Unsupported workload report schema")
    return result


def correctness_reasons(trial):
    reasons = []
    if trial.get("status") != "completed":
        reasons.append("trial did not complete cleanly")
    if trial.get("identityChanged"):
        reasons.append("prepared inputs changed during the campaign")
    workload = trial.get("workload") or {}
    if workload.get("measurementKind") != "official_client_workload":
        reasons.append("no authenticated official-client workload evidence")
    if not finite_number(workload.get("actualConnectedPeak")) or workload["actualConnectedPeak"] <= 0:
        reasons.append("no verified connected players")
    if workload.get("authenticatedSessionEvidence") is not True:
        reasons.append("authenticated session evidence unavailable")
    if workload.get("actualConnectedMinimum") != workload.get("configuredClients") or not workload.get("configuredClients"):
        reasons.append("continuous intended player population not verified")
    if not finite_number(workload.get("workloadDurationSeconds")) or workload["workloadDurationSeconds"] < 1800:
        reasons.append("measurement shorter than 30 minutes cannot establish sustained performance")
    if workload.get("status") not in ("completed", "passed"):
        reasons.append("workload did not report completion")
    if workload.get("errors") != []:
        reasons.append("workload errors are nonempty or unavailable")
    # Coverage timestamps must come from the workload and capture, not requested duration.
    try:
        from datetime import datetime
        ws, we, ps, pe = (datetime.fromisoformat(str(value).replace("Z", "+00:00")) for value in
            (workload.get("workloadStartedUtc"), workload.get("workloadCompletedUtc"),
             trial.get("measurementStartedUtc"), trial.get("measurementStoppedUtc")))
        duration = (pe - ps).total_seconds()
        if duration <= 0 or (min(we, pe) - max(ws, ps)).total_seconds() < duration * .98:
            reasons.append("authenticated workload does not cover at least 98% of measured interval")
    except (ValueError, TypeError):
        reasons.append("actual workload/profile timing coverage unavailable")
    try:
        profile = trial.get("profiler") or {}
        begin, finish, capture_begin, capture_finish = (
            datetime.fromisoformat(str(value).replace("Z", "+00:00")) for value in
            (profile.get("startedUtc"), profile.get("stoppedUtc"),
             trial.get("measurementStartedUtc"), trial.get("measurementStoppedUtc")))
        if abs((begin - capture_begin).total_seconds()) > 2 or abs((finish - capture_finish).total_seconds()) > 2:
            reasons.append("profiler capture timestamps do not match trial measurement boundaries")
    except (ValueError, TypeError):
        reasons.append("profiler capture timestamp evidence unavailable")
    if not finite_number(workload.get("actionsConfirmed")) or workload["actionsConfirmed"] <= 0:
        reasons.append("no confirmed completed gameplay work")
    completed = workload.get("workCompletedByType")
    if (not isinstance(completed, dict) or not completed or
            any(not isinstance(key, str) or not finite_number(value) or value < 0 for key, value in completed.items()) or
            sum(completed.values()) != workload.get("actionsConfirmed")):
        reasons.append("confirmed work by action type unavailable or inconsistent")
    for key in ("actionsFailed", "pendingUnconfirmed", "connectionFailures", "disconnects", "interactionFailures"):
        if workload.get(key) != 0:
            reasons.append(f"{key} is nonzero or unavailable")
    checks = workload.get("correctnessChecks")
    required = ("saveReloadIntegrity", "inventoryCorrectness", "simulationTiming", "permissions", "combatCorrectness")
    if not isinstance(checks, dict) or any(checks.get(k) is not True for k in required):
        reasons.append("required correctness checks are not all evidenced")
    health = trial.get("health") or {}
    if health.get("serverErrors") != 0 or health.get("queueGrowth") is not False:
        reasons.append("server errors/queue growth not cleared by external evidence")
    if not isinstance(trial.get("environment"), dict) or not trial["environment"]:
        reasons.append("observed host environment unavailable")
    return reasons


def build_comparison(trials, campaign=None):
    """Report trial distributions; never pool already-aggregated tick percentiles."""
    campaign = campaign or {}
    def trial_tick(trial):
        profiler = trial.get("profiler")
        return profiler.get("tick", {}) if isinstance(profiler, dict) and isinstance(profiler.get("tick"), dict) else {}
    variants = {}
    for variant in ("A", "B", "C", "D"):
        members = [t for t in trials if t.get("variant") == variant]
        tick_metrics = {}
        for key in ("averageMs", "medianMs", "p95Ms", "p99Ms", "worstMs", "effectiveTps"):
            tick_metrics[key] = describe([trial_tick(t).get(key) for t in members])
        variants[variant] = {
            "trials": len(members), "completed": sum(t.get("status") == "completed" for t in members),
            "tickAcrossTrials": tick_metrics,
            "processCpuAcrossTrials": describe([t.get("resources", {}).get("cpuPercent", {}).get("mean") for t in members]),
            "peakWorkingSetAcrossTrials": describe([t.get("resources", {}).get("workingSetBytes", {}).get("max") for t in members]),
            "actualPlayersAcrossTrials": describe([(t.get("workload") or {}).get("actualConnectedPeak") for t in members]),
        }
    comparisons = []
    for baseline, candidate, purpose in (("A", "B", "profiler overhead"), ("B", "C", "single intervention"),
                                         ("B", "D", "combined interventions"), ("A", "D", "overall")):
        a = [t for t in trials if t.get("variant") == baseline]
        b = [t for t in trials if t.get("variant") == candidate]
        reasons = []
        if len(a) < 3 or len(b) < 3:
            reasons.append("fewer than three trials per variant")
        if len({t.get("repeat") for t in a}) != len(a) or len({t.get("repeat") for t in b}) != len(b):
            reasons.append("duplicate repeats cannot substitute for independent trials")
        for t in a + b:
            reasons.extend(correctness_reasons(t))
            try:
                _, missing = validate_profiler(t.get("profiler"), campaign.get("measurementSeconds"))
            except (ValueError, TypeError, KeyError):
                missing = ["invalid profiler schema"]
            if t.get("missingMetrics") or missing:
                reasons.append("required profiler measurements are unavailable or misaligned")
            workload = t.get("workload") or {}
            requested = workload.get("requestedDurationSeconds")
            actual = workload.get("workloadDurationSeconds")
            if not finite_number(requested) or not finite_number(actual) or abs(actual-requested) > max(2, requested * .02):
                reasons.append("actual workload duration does not match requested measurement period")
            if campaign.get("measurementSeconds") is not None and requested != campaign["measurementSeconds"]:
                reasons.append("requested workload duration differs from campaign measurement period")
            if campaign.get("scenario") is not None and workload.get("scenario") != campaign["scenario"]:
                reasons.append("actual workload scenario differs from campaign")
        sources = [trial_tick(t).get("source") for t in a + b]
        if sources and (any(not isinstance(source, str) or not source for source in sources) or len(set(sources)) != 1):
            reasons.append("tick measurement sources differ or are unavailable")
        # Seed and work count matching prevents lighter candidate workloads being called faster.
        def workload_signatures(members):
            return sorted((t.get("repeat", -1), t.get("seed", -1),
                           json.dumps({key: (t.get("workload") or {}).get(key) for key in
                              ("scenario", "configuredClients", "actualConnectedPeak", "actionsConfirmed", "requestedDurationSeconds", "workCompletedByType")}, sort_keys=True))
                          for t in members)
        if workload_signatures(a) != workload_signatures(b):
            reasons.append("workload counts, duration, scenario, seeds or players differ")
        if not campaign.get("identityValidated"):
            reasons.append("world/config/mod/runtime/resource identity not verified")
        if campaign.get("synthetic"):
            reasons.append("synthetic fixtures are not measured server results")
        environments = [t.get("environment") for t in a + b]
        if environments and any(item != environments[0] for item in environments):
            reasons.append("recorded host environment differs across trials")
        deltas = {}
        for key in ("averageMs", "p95Ms", "p99Ms"):
            av = variants[baseline]["tickAcrossTrials"][key]["median"]
            bv = variants[candidate]["tickAcrossTrials"][key]["median"]
            deltas[key] = ((bv - av) * 100 / av) if av and bv is not None else None
        comparisons.append({"baseline": baseline, "candidate": candidate, "purpose": purpose,
                            "medianOfTrialPercentChange": deltas, "eligibleForReview": not reasons,
                            "blockingReasons": sorted(set(reasons)),
                            "conclusion": "Comparable evidence; manual gameplay review required. No automatic production claim."
                                if not reasons else "Insufficient comparable evidence for a performance claim."})
    return {"schemaVersion": 1, "kind": "newfies-optimizer-benchmark", "campaign": campaign,
            "synthetic": bool(campaign.get("synthetic")), "variants": variants, "comparisons": comparisons,
            "trials": trials, "notes": [
                "Tick percentiles are reported per trial, then summarized across trials; they are not pooled tick percentiles.",
                "A normally has no optimizer profiler. Use the same independent collector for A and B to measure profiler overhead.",
                "A missing value means unavailable, never zero. CPU may exceed 100% on multicore machines.",
                "Synthetic fixtures and idle-only trials cannot establish production improvements."]}


def compact_trial(trial):
    """Bound campaign memory/payload while preserving per-trial full history on disk."""
    copied = dict(trial)
    for field in ("profiler", "resources"):
        original = trial.get(field)
        if isinstance(original, dict):
            copied[field] = dict(original)
            samples = original.get("samples", [])
            if len(samples) > 128:
                copied[field]["samples"] = [samples[round(index * (len(samples) - 1) / 127)] for index in range(128)]
            copied[field]["dashboardHistoryOriginalCount"] = original.get("dashboardHistoryOriginalCount", len(samples))
            copied[field]["dashboardHistoryDownsampled"] = original.get("dashboardHistoryDownsampled", False) or len(samples) > 128
    return copied


def write_outputs(report, directory):
    import csv
    import html
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    view = dict(report, trials=[compact_trial(trial) for trial in report["trials"]])
    temporary = directory / ".report.json.tmp"
    temporary.write_text(json.dumps(view, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    os.replace(temporary, directory / "report.json")
    with (directory / "trials.csv").open("w", newline="", encoding="utf-8") as file:
        writer = csv.writer(file)
        writer.writerow(["trial", "variant", "status", "players", "confirmedActions", "averageTickMs", "p95TickMs", "p99TickMs", "meanCpuPercent", "peakWorkingSetBytes"])
        for trial in report["trials"]:
            tick = (trial.get("profiler") or {}).get("tick", {})
            workload = trial.get("workload") or {}
            writer.writerow([trial.get("id"), trial.get("variant"), trial.get("status"), workload.get("actualConnectedPeak"),
                             workload.get("actionsConfirmed"), tick.get("averageMs"), tick.get("p95Ms"), tick.get("p99Ms"),
                             trial.get("resources", {}).get("cpuPercent", {}).get("mean"), trial.get("resources", {}).get("workingSetBytes", {}).get("max")])
    rows = []
    for item in report["comparisons"]:
        rows.append("<tr><td>" + html.escape(item["baseline"] + " → " + item["candidate"]) + "</td><td>" +
                    html.escape(item["purpose"]) + "</td><td>" + html.escape(item["conclusion"]) + "</td><td>" +
                    html.escape("; ".join(item["blockingReasons"])) + "</td></tr>")
    (directory / "report.html").write_text("<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width'><title>NOS benchmark report</title>"
        "<style>body{background:#111920;color:#e1e8ed;font:16px system-ui;max-width:1200px;margin:2rem auto;padding:1rem}td,th{padding:1rem;text-align:left;border-bottom:1px solid #364451}a{color:#8bced7}</style>"
        "<h1>NOS benchmark evidence</h1><p>" + ("SYNTHETIC TEST FIXTURE — not server results." if report.get("synthetic") else "Observed evidence; incomplete metrics remain unavailable.") +
        "</p><table><thead><tr><th>Pair</th><th>Purpose</th><th>Assessment</th><th>Limits</th></tr></thead><tbody>" + "".join(rows) + "</tbody></table>"
        "<p>See report.json and trials.csv for measurements and run-to-run variation. Use the localhost dashboard for interactive charts.</p></html>", encoding="utf-8")
