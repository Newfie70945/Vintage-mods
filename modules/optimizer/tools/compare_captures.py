#!/usr/bin/env python3
"""Decide whether an optimizer setting helped, hurt, or did nothing, from two sets of captures.

Capture the same workload twice: a BASELINE with the module off and a CANDIDATE with it on
(/optimizer export writes latest.json each time; copy it aside). Several captures per side are
pooled, so an A-B-A-B run is better than a single pair.

    python3 compare_captures.py --baseline off1.json off2.json --candidate on1.json on2.json [--json]

The verdict compares per-second samples with a rank test (Mann-Whitney) and also requires the
change to be large enough to matter. It refuses to call a result when the captures are too short,
the workload (players, entities, chunks) differs, or several modules changed at once.
Samples from one run are autocorrelated, so p-values are optimistic; the effect-size floors and
the "inconclusive" outcomes are the real safeguard.
"""
import argparse
import json
import math
import statistics
import sys
from pathlib import Path

ALPHA = 0.01
MIN_SAMPLES_PER_SIDE = 30
MIN_DURATION_SECONDS = 60
TICK_MIN_ABS_MS = 0.5
TICK_MIN_REL = 0.05
GC_MIN_ABS_MS = 5.0
GC_MIN_REL = 0.20
WORKLOAD_MAX_REL = 0.25
WORKLOAD_MIN_ABS = {"players": 2, "loadedEntities": 100, "loadedChunks": 50}
HOTSPOT_MIN_CALLS = 50
HOTSPOT_MIN_REL = 0.25
HOTSPOT_MIN_ABS_MS = 0.25

# (sample field, label, min abs change, min relative change)
METRICS = (
    ("averageTickMs", "Average tick (ms, per-second samples)", TICK_MIN_ABS_MS, TICK_MIN_REL),
    ("p95TickMs", "p95 tick (ms, per-second samples)", TICK_MIN_ABS_MS, TICK_MIN_REL),
    ("gcPauseMsDelta", "GC pause per second (ms)", GC_MIN_ABS_MS, GC_MIN_REL),
)
TICK_FIELDS = {"averageTickMs", "p95TickMs"}


def _num(value):
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) else None


def _values(samples, field):
    return [v for v in (_num(s.get(field)) for s in samples if isinstance(s, dict)) if v is not None]


def mann_whitney_p(a, b):
    """Two-sided p-value of the Mann-Whitney U test (normal approximation, tie-corrected)."""
    n1, n2 = len(a), len(b)
    if n1 == 0 or n2 == 0:
        return 1.0
    ordered = sorted([(v, 0) for v in a] + [(v, 1) for v in b])
    ranks, ties, i = [0.0] * len(ordered), 0.0, 0
    while i < len(ordered):
        j = i
        while j + 1 < len(ordered) and ordered[j + 1][0] == ordered[i][0]:
            j += 1
        for k in range(i, j + 1):
            ranks[k] = (i + j) / 2 + 1
        size = j - i + 1
        ties += size ** 3 - size
        i = j + 1
    rank_sum_a = sum(r for r, (_, side) in zip(ranks, ordered) if side == 0)
    u = rank_sum_a - n1 * (n1 + 1) / 2
    n = n1 + n2
    sigma_sq = n1 * n2 / 12 * ((n + 1) - ties / (n * (n - 1)))
    if sigma_sq <= 0:
        return 1.0
    z = (abs(u - n1 * n2 / 2) - 0.5) / math.sqrt(sigma_sq)
    return math.erfc(max(z, 0) / math.sqrt(2))


def _active_modules(reports):
    """Modules active in EVERY report of a side (a module that flapped is not reliably on)."""
    sets = []
    for report in reports:
        sets.append({m.get("name") for m in report.get("modules") or []
                     if isinstance(m, dict) and m.get("status") == "active"})
    return set.intersection(*sets) if sets else set()


def _counters(reports):
    totals = {}
    for report in reports:
        for key, value in (report.get("engineCounters") or {}).items():
            if _num(value) is not None:
                totals[key] = totals.get(key, 0) + value
    return totals


def _pooled_hotspots(reports):
    pooled = {}
    for report in reports:
        for spot in report.get("hotspots") or []:
            if not isinstance(spot, dict) or not spot.get("name"):
                continue
            total, sampled = _num(spot.get("totalMs")) or 0, _num(spot.get("sampledCalls")) or 0
            entry = pooled.setdefault(spot["name"], {"total": 0.0, "sampled": 0.0, "calls": 0.0})
            entry["total"] += total
            entry["sampled"] += sampled
            entry["calls"] += _num(spot.get("calls")) or 0
    return pooled


def judge_metric(base, cand, min_abs, min_rel):
    """Return a verdict dict for one lower-is-better series."""
    if not base or not cand:
        return {"verdict": "unknown", "reason": "no samples"}
    mb, mc = statistics.median(base), statistics.median(cand)
    delta = mc - mb
    rel = delta / mb if mb else (math.inf if delta > 0 else 0.0)
    p = mann_whitney_p(base, cand)
    large = abs(delta) >= min_abs and abs(rel) >= min_rel
    if p < ALPHA and large:
        verdict = "better" if delta < 0 else "worse"
    elif p < ALPHA:
        verdict = "same"  # detectable but too small to matter
    elif large:
        verdict = "unclear"  # looks big but is within the noise
    else:
        verdict = "same"
    return {"verdict": verdict, "baselineMedian": mb, "candidateMedian": mc, "delta": delta,
            "relative": None if math.isinf(rel) else rel, "p": p, "nBaseline": len(base), "nCandidate": len(cand)}


def workload_differences(base_samples, cand_samples):
    issues = []
    for field, min_abs in WORKLOAD_MIN_ABS.items():
        b, c = _values(base_samples, field), _values(cand_samples, field)
        if not b or not c:
            continue
        mb, mc = statistics.median(b), statistics.median(c)
        diff = abs(mc - mb)
        if diff >= min_abs and diff / max(mb, mc, 1) > WORKLOAD_MAX_REL:
            issues.append(f"median {field} differs: {mb:g} vs {mc:g}")
    return issues


def compare(baseline, candidate):
    base_samples = [s for r in baseline for s in r.get("samples") or []]
    cand_samples = [s for r in candidate for s in r.get("samples") or []]
    base_seconds = sum(_num(r.get("durationSeconds")) or 0 for r in baseline)
    cand_seconds = sum(_num(r.get("durationSeconds")) or 0 for r in candidate)

    base_mods, cand_mods = _active_modules(baseline), _active_modules(candidate)
    turned_on, turned_off = sorted(cand_mods - base_mods), sorted(base_mods - cand_mods)
    changed = turned_on + turned_off

    metrics = []
    for field, label, min_abs, min_rel in METRICS:
        judged = judge_metric(_values(base_samples, field), _values(cand_samples, field), min_abs, min_rel)
        judged.update({"field": field, "label": label})
        metrics.append(judged)

    base_counters, cand_counters = _counters(baseline), _counters(candidate)
    new_failures = sorted(k for k, v in cand_counters.items()
                          if "fail" in k.lower() and v > 0 and base_counters.get(k, 0) == 0)

    hotspots = []
    bp, cp = _pooled_hotspots(baseline), _pooled_hotspots(candidate)
    for name in sorted(set(bp) & set(cp)):
        b, c = bp[name], cp[name]
        if b["sampled"] < HOTSPOT_MIN_CALLS or c["sampled"] < HOTSPOT_MIN_CALLS:
            continue
        ab, ac = b["total"] / b["sampled"], c["total"] / c["sampled"]
        delta = ac - ab
        rel = delta / ab if ab else None
        if abs(delta) >= HOTSPOT_MIN_ABS_MS and rel is not None and abs(rel) >= HOTSPOT_MIN_REL:
            hotspots.append({"name": name, "baselineAvgMs": ab, "candidateAvgMs": ac, "delta": delta, "relative": rel,
                             "direction": "faster" if delta < 0 else "slower"})
    hotspots.sort(key=lambda h: -abs(h["delta"]))

    blockers = []
    if len(base_samples) < MIN_SAMPLES_PER_SIDE or len(cand_samples) < MIN_SAMPLES_PER_SIDE:
        blockers.append(f"too few samples ({len(base_samples)} baseline, {len(cand_samples)} candidate; "
                        f"need {MIN_SAMPLES_PER_SIDE} each)")
    if base_seconds < MIN_DURATION_SECONDS or cand_seconds < MIN_DURATION_SECONDS:
        blockers.append(f"captures too short ({base_seconds:.0f}s and {cand_seconds:.0f}s; need {MIN_DURATION_SECONDS}s each)")
    blockers += workload_differences(base_samples, cand_samples)

    tick = [m for m in metrics if m["field"] in TICK_FIELDS]
    worse = [m for m in metrics if m["verdict"] == "worse"]
    better = [m for m in metrics if m["verdict"] == "better"]
    tick_worse = [m for m in tick if m["verdict"] == "worse"]
    tick_better = [m for m in tick if m["verdict"] == "better"]

    if new_failures:
        verdict, why = "hurts", "Engine failure counters appeared only with the candidate: " + ", ".join(new_failures) + "."
    elif blockers:
        verdict, why = "inconclusive", "Not comparable: " + "; ".join(blockers) + "."
    elif tick_worse and not tick_better:
        verdict, why = "hurts", "Tick time is measurably worse with the candidate."
    elif tick_better and not tick_worse:
        verdict = "mixed" if any(m["field"] == "gcPauseMsDelta" for m in worse) else "helps"
        why = ("Tick time is measurably better, but GC pause got worse." if verdict == "mixed"
               else "Tick time is measurably better with the candidate.")
    elif tick_better and tick_worse:
        verdict, why = "mixed", "Average and p95 tick moved in opposite directions."
    elif better or worse:
        verdict = "mixed" if better and worse else ("helps" if better else "hurts")
        why = "Only GC pause moved; tick time is unchanged."
    elif any(m["verdict"] == "unclear" for m in metrics):
        verdict, why = "inconclusive", "A difference is visible but within run-to-run noise; capture more repeats."
    else:
        verdict, why = "no measurable effect", "No metric moved by a meaningful, statistically detectable amount."

    notes = []
    if not changed:
        notes.append("No module differs between the two sides, so this measures run-to-run noise. "
                     "A large change here means the comparison method is unreliable for this workload.")
    elif len(changed) > 1:
        notes.append("Several modules differ (" + ", ".join(changed) + "); the effect cannot be attributed to one of them. "
                     "Change one module at a time.")
        if verdict in ("helps", "hurts", "mixed"):
            verdict, why = "confounded", why + " " + "More than one module changed."
    if len(baseline) == 1 and len(candidate) == 1:
        notes.append("One capture per side cannot separate the setting from time-of-day or world-state drift. "
                     "Repeat as baseline, candidate, baseline, candidate for a stronger answer.")

    return {"verdict": verdict, "summary": why, "turnedOn": turned_on, "turnedOff": turned_off,
            "blockers": blockers, "metrics": metrics, "hotspots": hotspots[:10], "newFailureCounters": new_failures,
            "notes": notes,
            "samples": {"baseline": len(base_samples), "candidate": len(cand_samples)},
            "seconds": {"baseline": base_seconds, "candidate": cand_seconds}}


def render_text(result):
    lines = [f"VERDICT: {result['verdict'].upper()} - {result['summary']}", ""]
    if result["turnedOn"] or result["turnedOff"]:
        lines.append("Modules turned on in the candidate: " + (", ".join(result["turnedOn"]) or "none"))
        lines.append("Modules turned off in the candidate: " + (", ".join(result["turnedOff"]) or "none"))
        lines.append("")
    lines.append(f"Samples: {result['samples']['baseline']} baseline, {result['samples']['candidate']} candidate "
                 f"({result['seconds']['baseline']:.0f}s / {result['seconds']['candidate']:.0f}s).")
    for m in result["metrics"]:
        if m["verdict"] == "unknown":
            lines.append(f"  {m['label']}: no data")
            continue
        rel = "n/a" if m["relative"] is None else f"{m['relative'] * 100:+.1f}%"
        lines.append(f"  {m['label']}: {m['baselineMedian']:.2f} -> {m['candidateMedian']:.2f} "
                     f"({m['delta']:+.2f}, {rel}, p={m['p']:.3g}) => {m['verdict']}")
    if result["hotspots"]:
        lines += ["", "Timed methods that changed (average ms per sampled call):"]
        for h in result["hotspots"]:
            lines.append(f"  {h['name']}: {h['baselineAvgMs']:.3f} -> {h['candidateAvgMs']:.3f} ({h['relative'] * 100:+.0f}%, {h['direction']})")
    for note in result["notes"]:
        lines += ["", "Note: " + note]
    return "\n".join(lines).rstrip() + "\n"


def _load(paths):
    reports = []
    for path in paths:
        report = json.loads(Path(path).read_text(encoding="utf-8"))
        if report.get("kind") != "newfies-optimizer-profile":
            raise ValueError(f"{path} is not an optimizer profile export")
        reports.append(report)
    return reports


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--baseline", nargs="+", required=True, help="captures taken with the setting OFF")
    parser.add_argument("--candidate", nargs="+", required=True, help="captures taken with the setting ON")
    parser.add_argument("--json", action="store_true", help="print machine-readable output")
    args = parser.parse_args(argv)
    try:
        result = compare(_load(args.baseline), _load(args.candidate))
    except (OSError, ValueError) as error:
        print(f"Could not compare: {error}", file=sys.stderr)
        return 2
    print(json.dumps(result, indent=2) if args.json else render_text(result), end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
