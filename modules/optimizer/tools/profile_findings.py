#!/usr/bin/env python3
"""Turn one Newfie's Server Optimizer capture (/optimizer export, latest.json) into ranked findings.

Every finding states the numbers it rests on and what is NOT established. Thresholds are
heuristics for deciding where to look next, not performance claims or capacity limits.

    python3 profile_findings.py path/to/latest.json [--tick-budget-ms 33.3] [--json]
"""
import argparse
import json
import statistics
import sys
from pathlib import Path

# The three main-thread stages timed on every call by the chunkLifecycleTimings module.
STAGES = {
    "ServerSystemSupplyChunks.mainThreadLoadChunkColumn": "Chunk-column load completion",
    "ServerSystemUnloadChunks.UnloadChunkColumns": "Chunk unload",
    "ServerSystemEntitySimulation.SendEntityDespawns": "Entity despawn send",
}

SEVERITY_ORDER = {"problem": 0, "watch": 1, "info": 2}
GC_PAUSE_PROBLEM_PERCENT = 5.0
GC_PAUSE_WATCH_PERCENT = 2.0
FRAGMENTATION_WATCH_RATIO = 0.25
STAGE_PROBLEM_SHARE = 25.0
STAGE_WATCH_SHARE = 10.0
STAGE_PROBLEM_SPIKE_SHARE = 0.5
STAGE_WATCH_SPIKE_SHARE = 0.25
MIN_SPIKE_MS = 25.0
OVERHEAD_WATCH_PERCENT = 2.0
SHORT_CAPTURE_SECONDS = 60
SLOW_SAMPLE_MIN = 3
GC_COINCIDENCE_MS = 20.0


def _num(value):
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) else None


def _finding(severity, title, evidence, next_step="", not_established=""):
    return {"severity": severity, "title": title, "evidence": evidence,
            "next": next_step, "notEstablished": not_established}


def _median(values):
    values = [v for v in values if v is not None]
    return statistics.median(values) if values else None


def capture_quality(report):
    findings = []
    duration = _num(report.get("durationSeconds")) or 0
    tick = report.get("tick") or {}
    if report.get("isRunning"):
        findings.append(_finding("info", "Capture is still running",
                                 "The export is a snapshot of a running capture.",
                                 "Stop the capture (/optimizer profile stop) for a final report."))
    if duration < SHORT_CAPTURE_SECONDS:
        findings.append(_finding("watch", "Short capture",
                                 f"Only {duration:.0f}s were captured (under {SHORT_CAPTURE_SECONDS}s).",
                                 "Capture the whole event, for example a full trip between populated areas.",
                                 "Percentiles from a few dozen ticks are not stable."))
    overwritten = _num(report.get("samplesOverwritten")) or 0
    if overwritten > 0:
        findings.append(_finding("watch", "Sample history was overwritten",
                                 f"{int(overwritten)} early samples were dropped from the bounded ring.",
                                 "Raise Profiling.MaxSamples or Profiling.SampleIntervalSeconds, or capture less time.",
                                 "Tick percentiles and hotspots still cover the whole capture; the sample timeline does not."))
    overflow = _num(tick.get("overflowCount")) or 0
    if overflow > 0:
        findings.append(_finding("info", "Some ticks exceeded the histogram range",
                                 f"{int(overflow)} tick(s) are counted but not in the percentile buckets.",
                                 "", "Percentiles near the top may be unavailable rather than low."))
    omitted = _num(report.get("hotspotLookupsOmitted")) or 0
    if omitted > 0:
        findings.append(_finding("watch", "Timing hotspots were omitted",
                                 f"{int(omitted)} lookup(s) did not fit in the hotspot table.",
                                 "Raise Profiling.MaxHotspots (up to 128).",
                                 "Some timed methods are missing from this report."))
    overhead = report.get("overhead") or {}
    work = (_num(overhead.get("hookWorkMs")) or 0) + (_num(overhead.get("sampleWorkMs")) or 0)
    if duration > 0 and work > 0:
        percent = work / (duration * 1000) * 100
        if percent >= OVERHEAD_WATCH_PERCENT:
            findings.append(_finding("watch", "Profiler self-timing is noticeable",
                                     f"Hooks and sampling used about {work:.0f} ms ({percent:.1f}% of the capture).",
                                     "Compare against a profiler-off run before trusting small differences.",
                                     "Self-timing excludes Harmony dispatch, so real overhead is higher."))
    return findings


def tick_findings(report, budget_ms):
    tick = report.get("tick") or {}
    count = _num(tick.get("count")) or 0
    if count <= 0:
        return [_finding("info", "No completed ticks were recorded",
                         "The tick hook produced no data.", "Check /optimizer modules for tickMetrics.")]
    avg, med = _num(tick.get("averageMs")), _num(tick.get("medianMs"))
    p95, p99, worst = _num(tick.get("p95Ms")), _num(tick.get("p99Ms")), _num(tick.get("worstMs"))
    tps = _num(tick.get("effectiveTps"))
    summary = (f"{int(count)} ticks; average {avg}, median {med}, p95 {p95}, p99 {p99}, worst {worst} ms"
               + (f"; effective {tps:.1f} ticks/s" if tps is not None else "") + f"; budget {budget_ms:g} ms.")
    caveat = "Tick work is the integer-millisecond pre-sleep sample, so sub-millisecond changes are not resolvable."
    if avg is not None and avg > budget_ms:
        return [_finding("problem", "Sustained overload: average tick exceeds the budget", summary,
                         "Find what dominates every tick (entity loop, callbacks); spikes are not the main issue.", caveat)]
    if p95 is not None and p95 > budget_ms:
        return [_finding("watch", "Ticks regularly run over budget", summary,
                         "Look at which stage or callback is busy during the slow ticks.", caveat)]
    if worst is not None and worst > 3 * budget_ms:
        ratio = f" (worst is {worst / med:.0f}x the median)" if med else ""
        return [_finding("watch", "Occasional long ticks (spikes)" + ratio, summary,
                         "Spikes usually come from bursts: chunk loading/unloading, saves, GC. See the stage and GC findings.", caveat)]
    return [_finding("info", "Tick times are within budget", summary, "", caveat)]


def stage_findings(report):
    tick = report.get("tick") or {}
    hotspots = {h.get("name"): h for h in report.get("hotspots") or [] if isinstance(h, dict)}
    work_ms = None
    if _num(tick.get("count")) and _num(tick.get("averageMs")) is not None:
        work_ms = tick["count"] * tick["averageMs"]
    worst_tick = _num(tick.get("worstMs"))
    findings, rows = [], []
    for name, label in STAGES.items():
        spot = hotspots.get(name)
        if not spot:
            continue
        calls, sampled = _num(spot.get("calls")) or 0, _num(spot.get("sampledCalls")) or 0
        total, avg = _num(spot.get("totalMs")) or 0, _num(spot.get("averageMs"))
        p95, worst = _num(spot.get("p95Ms")), _num(spot.get("worstMs"))
        exact = calls > 0 and sampled == calls
        share = (total / work_ms * 100) if (exact and work_ms) else None
        rows.append({"stage": label, "method": name, "calls": int(calls), "exact": exact, "totalMs": total,
                     "averageMs": avg, "p95Ms": p95, "worstMs": worst, "shareOfMainThreadWork": share})
        if calls <= 0:
            continue
        spike_share = (worst / worst_tick) if (worst is not None and worst_tick) else None
        severity = "info"
        if (share is not None and share >= STAGE_PROBLEM_SHARE) or \
                (spike_share is not None and spike_share >= STAGE_PROBLEM_SPIKE_SHARE and worst >= MIN_SPIKE_MS * 2):
            severity = "problem"
        elif (share is not None and share >= STAGE_WATCH_SHARE) or \
                (spike_share is not None and spike_share >= STAGE_WATCH_SPIKE_SHARE and worst >= MIN_SPIKE_MS):
            severity = "watch"
        parts = [f"{int(calls)} calls, average {avg} ms, p95 {p95} ms, worst {worst} ms"]
        if share is not None:
            parts.append(f"{total:.0f} ms in total = {share:.1f}% of main-thread tick work")
        if spike_share is not None:
            parts.append(f"worst call = {spike_share * 100:.0f}% of the worst tick")
        nxt = {"problem": f"Work inside {label.lower()} first: its subscribers and per-entity/per-column cost.",
               "watch": f"Keep {label.lower()} in view; compare with a quiet period.",
               "info": ""}[severity]
        findings.append(_finding(severity, f"{label} stage", "; ".join(parts) + ("" if exact else " (sampled: lower bound)"),
                                 nxt, "Timings are inclusive of subscribers and other patches and overlap the tick; do not add stages together."
                                 + ("" if exact else " Unsampled calls are not extrapolated.")))
    if not rows:
        modules = {m.get("name"): m for m in report.get("modules") or [] if isinstance(m, dict)}
        state = modules.get("chunkLifecycleTimings")
        reason = f"{state.get('status')}: {state.get('reason')}" if state else "module not present in this report"
        findings.append(_finding("info", "Chunk stages were not timed", reason,
                                 "Set Profiling.PhaseTimings=true and Compatibility.SafeMode=false, restart, and capture again.",
                                 "Without these timings a chunk-load stall cannot be attributed to a stage."))
    return findings, rows


def gc_findings(report):
    samples = [s for s in report.get("samples") or [] if isinstance(s, dict)]
    duration = _num(report.get("durationSeconds")) or (samples[-1].get("elapsedSeconds") if samples else 0) or 0
    if not samples or duration <= 0:
        return [], {}
    pause = sum(_num(s.get("gcPauseMsDelta")) or 0 for s in samples)
    alloc = sum(_num(s.get("allocatedBytesDelta")) or 0 for s in samples)
    gen2 = sum(_num(s.get("gc2Delta")) or 0 for s in samples)
    pause_percent = pause / (duration * 1000) * 100
    committed = [(_num(s.get("gcCommittedBytes")), _num(s.get("gcFragmentedBytes"))) for s in samples]
    committed = [(c, f) for c, f in committed if c and f is not None]
    ratio = (committed[-1][1] / committed[-1][0]) if committed else None
    stats = {"pauseMs": pause, "pausePercentOfWall": pause_percent, "allocMiBPerSecond": alloc / duration / 1048576,
             "gen2Collections": int(gen2), "fragmentationRatio": ratio}
    findings = []
    summary = (f"{pause:.0f} ms of GC pause over {duration:.0f}s ({pause_percent:.2f}% of wall time), "
               f"{int(gen2)} gen-2 collections, allocation {stats['allocMiBPerSecond']:.1f} MiB/s.")
    if pause_percent >= GC_PAUSE_PROBLEM_PERCENT:
        findings.append(_finding("problem", "GC pauses take a large share of time", summary,
                                 "Find the largest allocation sources (dotnet-counters / dotnet-trace) before patching call sites; test GC settings as a labelled comparison.",
                                 "Pause time does not say which code allocated. Forced collections would add stalls."))
    elif pause_percent >= GC_PAUSE_WATCH_PERCENT:
        findings.append(_finding("watch", "GC pauses are noticeable", summary, "Check whether they line up with the slow ticks (see correlation).",
                                 "Pause time does not say which code allocated."))
    else:
        findings.append(_finding("info", "GC pause share is low", summary))
    if ratio is not None and ratio >= FRAGMENTATION_WATCH_RATIO:
        findings.append(_finding("watch", "Heap is heavily fragmented",
                                 f"{ratio * 100:.0f}% of committed GC memory is fragmented at the end of the capture.",
                                 "Large-object behaviour and heap settings matter more than per-call garbage here.",
                                 "Fragmentation is not proof of a leak."))
    return findings, stats


def correlation_findings(report, budget_ms):
    samples = [s for s in report.get("samples") or [] if isinstance(s, dict)]
    p95s = [_num(s.get("p95TickMs")) for s in samples]
    baseline = _median(p95s)
    if baseline is None:
        return []
    cutoff = max(2 * baseline, budget_ms)
    slow = [i for i, v in enumerate(p95s) if v is not None and v >= cutoff]
    rest = [i for i, v in enumerate(p95s) if v is not None and v < cutoff]
    if len(slow) < SLOW_SAMPLE_MIN:
        return []

    def rate(indices, predicate):
        hits = sum(1 for i in indices if predicate(i))
        return hits, len(indices)

    def gc(i):
        return (_num(samples[i].get("gcPauseMsDelta")) or 0) >= GC_COINCIDENCE_MS

    def generating(i):
        return (_num(samples[i].get("generatingChunkColumns")) or 0) > 0

    def chunk_growth(i):
        if i == 0:
            return False
        a, b = _num(samples[i - 1].get("loadedChunks")), _num(samples[i].get("loadedChunks"))
        return a is not None and b is not None and abs(b - a) >= 5

    findings = []
    for label, predicate, nxt in (
            (f"a GC pause of {GC_COINCIDENCE_MS:.0f} ms or more", gc, "Look at allocation sources and GC settings."),
            ("queued chunk generation", generating, "Chunk generation/loading is active during slow periods; use the chunk stage timings."),
            ("a change of 5+ loaded chunks", chunk_growth, "Chunk load/unload coincides with slow periods; use the chunk stage timings.")):
        hit_slow, n_slow = rate(slow, predicate)
        hit_rest, n_rest = rate(rest, predicate)
        if hit_slow == 0 or n_rest == 0:
            continue
        slow_rate, rest_rate = hit_slow / n_slow, hit_rest / n_rest
        if slow_rate >= 0.5 and slow_rate >= 2 * max(rest_rate, 0.01):
            findings.append(_finding(
                "watch", f"Slow samples line up with {label}",
                f"{hit_slow} of {n_slow} slow samples (p95 tick >= {cutoff:.0f} ms) vs {hit_rest} of {n_rest} other samples.",
                nxt, "Coincidence in one-second samples is not causation."))
    return findings


def module_findings(report):
    findings = []
    for module in report.get("modules") or []:
        if isinstance(module, dict) and module.get("status") in ("conflict", "failed"):
            findings.append(_finding("watch", f"Module {module.get('name')} is {module.get('status')}",
                                     str(module.get("reason") or ""), "Fix the cause or ignore that module's numbers.",
                                     "Data from this module is missing or partial."))
    for key, value in (report.get("engineCounters") or {}).items():
        if "fail" in str(key).lower() and (_num(value) or 0) > 0:
            findings.append(_finding("problem", f"Engine module failures: {key}", f"Counter is {int(value)}.",
                                     "Check the server log; the module stands itself down after a failure.",
                                     "A counter does not prove the optimizer caused the original exception."))
    return findings


def analyze(report, tick_budget_ms=33.3):
    stage_list, stage_rows = stage_findings(report)
    gc_list, gc_stats = gc_findings(report)
    findings = (tick_findings(report, tick_budget_ms) + stage_list + gc_list +
                correlation_findings(report, tick_budget_ms) + module_findings(report) + capture_quality(report))
    findings.sort(key=lambda f: SEVERITY_ORDER[f["severity"]])
    return {"optimizerVersion": report.get("optimizerVersion"), "gameVersion": report.get("gameVersion"),
            "durationSeconds": report.get("durationSeconds"), "findings": findings,
            "stages": stage_rows, "gc": gc_stats}


def render_text(result):
    lines = [f"Newfie's Server Optimizer capture: {result['durationSeconds']}s, game {result['gameVersion']}, "
             f"optimizer {result['optimizerVersion']}", ""]
    for finding in result["findings"]:
        lines.append(f"[{finding['severity'].upper()}] {finding['title']}")
        lines.append(f"    {finding['evidence']}")
        if finding["next"]:
            lines.append(f"    Next: {finding['next']}")
        if finding["notEstablished"]:
            lines.append(f"    Not established: {finding['notEstablished']}")
        lines.append("")
    if result["stages"]:
        lines.append("Chunk lifecycle stages (inclusive, main thread):")
        for row in result["stages"]:
            share = "n/a" if row["shareOfMainThreadWork"] is None else f"{row['shareOfMainThreadWork']:.1f}%"
            lines.append(f"    {row['stage']:<30} calls={row['calls']:<6} avg={row['averageMs']} p95={row['p95Ms']} "
                         f"worst={row['worstMs']} ms  share={share}")
    return "\n".join(lines).rstrip() + "\n"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("report", type=Path, help="profile JSON written by /optimizer export (latest.json)")
    parser.add_argument("--tick-budget-ms", type=float, default=33.3, help="server tick budget (default 33.3)")
    parser.add_argument("--json", action="store_true", help="print machine-readable findings")
    args = parser.parse_args(argv)
    try:
        report = json.loads(args.report.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        print(f"Could not read {args.report}: {error}", file=sys.stderr)
        return 2
    if report.get("kind") != "newfies-optimizer-profile":
        print("This does not look like an optimizer profile export (kind != newfies-optimizer-profile).", file=sys.stderr)
        return 2
    result = analyze(report, args.tick_budget_ms)
    print(json.dumps(result, indent=2) if args.json else render_text(result), end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
