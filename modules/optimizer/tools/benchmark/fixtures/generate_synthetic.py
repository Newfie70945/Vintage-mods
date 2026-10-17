#!/usr/bin/env python3
"""Generate deliberately labelled fake evidence for chart/math tests, never a benchmark."""
import argparse
import json
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from report import build_comparison, write_outputs


def fixture():
    trials = []
    for repeat in range(1, 4):
        for variant, cost in (("A", 10), ("B", 11), ("C", 9), ("D", 9)):
            average = cost + repeat * .1
            samples = [{"timestampUtc": f"2000-01-01T00:{index:02d}:00Z", "elapsedSeconds": index * 60,
                        "averageTickMs": average + math.sin(index / 3), "p95TickMs": average + 3,
                        "effectiveTps": 30, "players": 5, "processCpuPercent": 100 + index / 2,
                        "managedHeapBytes": (256 + index / 4) * 1048576,
                        "workingSetBytes": (400 + index / 4) * 1048576,
                        "loadedEntities": 180, "loadedChunks": 64, "gcPauseMsDelta": index % 6,
                        "networkSentBytesDelta": 10000 + index * 10} for index in range(31)]
            trials.append({"id": f"SYNTHETIC-r{repeat}-{variant}", "repeat": repeat, "variant": variant,
                "seed": repeat, "status": "completed", "environment": {"kind": "synthetic"},
                "profiler": {"schemaVersion": 1, "kind": "newfies-optimizer-profile", "gameVersion": "1.22.7",
                    "durationSeconds": 1800, "isRunning": False,
                    "tick": {"count": 54000, "averageMs": average, "medianMs": average - 1,
                             "p95Ms": average + 3, "p99Ms": average + 6, "worstMs": 40, "effectiveTps": 30},
                    "samples": samples, "hotspots": [], "modules": [{"name": "SYNTHETIC", "status": "fixture", "reason": "Not a running module"}]},
                "workload": {"schemaVersion": 1, "measurementKind": "synthetic_tooling_only",
                    "scenario": "SYNTHETIC chart fixture", "configuredClients": 5, "actualConnectedPeak": 0,
                    "actionsConfirmed": 0, "workloadDurationSeconds": 1800},
                "missingMetrics": ["No authentic clients or server execution; fabricated fixture"],
                "resources": {}, "health": None})
    return build_comparison(trials, {"synthetic": True, "identityValidated": False, "scenario": "SYNTHETIC · chart and math testing only", "gameVersion": "1.22.7"})


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    write_outputs(fixture(), args.output)
    print("Wrote SYNTHETIC fixture, not benchmark results.")
