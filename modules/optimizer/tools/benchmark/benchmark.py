#!/usr/bin/env python3
"""Run randomized A/B/C/D trials on fresh copies of explicitly prepared local clones."""
from __future__ import annotations

import argparse
from collections import deque
import datetime as dt
import hashlib
import json
import os
import platform
import random
import re
import shutil
import signal
import subprocess
import sys
import threading
import time
from pathlib import Path

from report import build_comparison, compact_trial, describe, load_object, read_profiler, read_workload, write_outputs

VARIANTS = ("A", "B", "C", "D")
MAX_FILES = 200000


def utc_now():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def dump(path, value):
    Path(path).write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def contained(root, relative):
    relative = Path(relative)
    if relative.is_absolute() or ".." in relative.parts:
        raise ValueError("Paths inside clones must be relative without '..'")
    path = (root / relative).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError("Path escapes prepared clone")
    return path


def tree_hash(path):
    path = Path(path)
    if not path.exists() or path.is_symlink():
        raise ValueError(f"Missing input or symlink: {path}")
    files = [path] if path.is_file() else sorted(path.rglob("*"))
    if len(files) > MAX_FILES:
        raise ValueError("Input tree exceeds file-count limit")
    hasher = hashlib.sha256()
    count = 0
    total = 0
    for file in files:
        if file.is_symlink():
            raise ValueError(f"Symlink in benchmark input: {file}")
        if not file.is_file():
            continue
        name = file.name if path.is_file() else file.relative_to(path).as_posix()
        file_hash = hashlib.sha256()
        with file.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                file_hash.update(block)
                total += len(block)
        hasher.update(name.encode("utf-8") + b"\0" + file_hash.digest())
        count += 1
    return {"sha256": hasher.hexdigest(), "files": count, "bytes": total}


def hash_paths(root, paths):
    if not isinstance(paths, list) or not paths:
        raise ValueError("gamePaths/configPaths/modPaths must each list actual prepared inputs")
    return {name: tree_hash(contained(root, name)) for name in sorted(paths)}


def validate_config(config, config_dir):
    if config.get("schemaVersion") != 1 or config.get("gameVersion") != "1.22.7":
        raise ValueError("Expected schemaVersion 1 and gameVersion 1.22.7")
    for key in ("scenario", "resources", "worldSnapshot", "variants"):
        if not config.get(key):
            raise ValueError(f"Missing {key}")
    if not isinstance(config["resources"], dict) or not all(k in config["resources"] for k in ("cpuQuota", "memoryLimitBytes", "storageClass")):
        raise ValueError("Explicit CPU quota, RAM limit and storage class are required")
    for key, minimum, maximum in (("repeats", 3, 100), ("warmupSeconds", 1, 86400), ("measurementSeconds", 1, 604800),
                                   ("startupTimeoutSeconds", 1, 3600), ("shutdownTimeoutSeconds", 1, 300)):
        value = config.get(key)
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not minimum <= value <= maximum:
            raise ValueError(f"{key} must be between {minimum} and {maximum}")
    if not isinstance(config["repeats"], int):
        raise ValueError("repeats must be an integer")
    if isinstance(config.get("seed"), bool) or not isinstance(config.get("seed"), int):
        raise ValueError("An integer randomization seed is required")
    snapshot = (config_dir / config["worldSnapshot"]).resolve()
    world = tree_hash(snapshot)
    if not snapshot.is_dir():
        raise ValueError("worldSnapshot must be a frozen directory")
    if set(config["variants"]) != set(VARIANTS):
        raise ValueError("Provide exactly variants A, B, C and D")
    fingerprints = {}
    for name in VARIANTS:
        variant = config["variants"][name]
        root = (config_dir / variant["templateDirectory"]).resolve()
        marker = load_object(root / ".nos-benchmark-clone.json")
        if marker.get("purpose") != "nos-local-benchmark" or marker.get("allowExecution") is not True:
            raise ValueError(f"{name}: template lacks explicit prepared-test-clone marker")
        tree_hash(root)  # Reject symlinks anywhere, including unlisted executable inputs.
        for field in ("serverCommand",):
            command = variant.get(field)
            if not isinstance(command, list) or not command or any(not isinstance(s, str) or "\0" in s for s in command):
                raise ValueError(f"{name}: {field} must be a nonempty string array; shells are not inserted")
        for field in ("gamePaths", "configPaths", "modPaths"):
            fingerprints.setdefault(name, {})[field] = hash_paths(root, variant.get(field))
        world_target = contained(root, variant["worldRelativePath"])
        if world_target.exists():
            raise ValueError(f"{name}: template world target must be absent; frozen snapshot is copied fresh per trial")
        if not variant.get("readyPattern") or len(variant["readyPattern"]) > 500:
            raise ValueError(f"{name}: explicit readiness log pattern is required")
        re.compile(variant["readyPattern"])
        for field in ("profilerReport", "workloadReport", "healthReport"):
            if variant.get(field):
                contained(root, variant[field])
        for field in ("startMeasurementStdin", "stopMeasurementStdin", "shutdownStdin"):
            values = variant.get(field, [])
            if not isinstance(values, list) or any(not isinstance(s, str) or "\n" in s or "\r" in s for s in values):
                raise ValueError(f"{name}: {field} must be separate console lines")
        enabled = variant.get("enabledOptimizations", [])
        if not isinstance(enabled, list) or any(not isinstance(x, str) for x in enabled) or len(enabled) != len(set(enabled)):
            raise ValueError(f"{name}: enabledOptimizations must be unique strings")
        expected_profiler = name != "A"
        if variant.get("profilerInstalled") is not expected_profiler:
            raise ValueError(f"{name}: profilerInstalled must be {expected_profiler}")
        if name in ("A", "B") and enabled:
            raise ValueError("A and B must disable every optimization")
        if name == "C" and len(enabled) != 1:
            raise ValueError("C must enable exactly one intervention")
        optimizer_paths = variant.get("optimizerPaths", [])
        if name == "A" and optimizer_paths:
            raise ValueError("A must not contain declared optimizer artifacts")
        if name != "A" and not optimizer_paths:
            raise ValueError("B/C/D must hash their optimizer artifacts/configuration")
        fingerprints[name]["optimizerPaths"] = hash_paths(root, optimizer_paths) if optimizer_paths else {}
        fingerprints[name]["template"] = tree_hash(root)
    for key in ("gamePaths", "configPaths", "modPaths"):
        if any(fingerprints[n][key] != fingerprints["A"][key] for n in VARIANTS):
            raise ValueError(f"Mismatched {key} across variants; comparisons require identical declared base inputs")
    validated = config.get("validatedOptimizations", {})
    if not isinstance(validated, dict):
        raise ValueError("validatedOptimizations must map names to previous correctness/benchmark evidence files")
    validated_hashes = {}
    for key in config["variants"]["D"].get("enabledOptimizations", []):
        if key not in validated:
            raise ValueError(f"D includes {key} without prior validation evidence")
        validated_hashes[key] = tree_hash((config_dir / validated[key]).resolve())
    workload = config.get("workloadCommand")
    if workload is not None and (not isinstance(workload, list) or not workload or any(not isinstance(x, str) for x in workload)):
        raise ValueError("workloadCommand must be a command array")
    return {"world": world, "variants": fingerprints, "validationEvidence": validated_hashes}


def make_plan(config):
    randomizer = random.Random(config["seed"])
    trials = []
    # Randomized blocks: each repeat has all four interventions with one matched workload seed.
    for repeat in range(config["repeats"]):
        order = list(VARIANTS)
        randomizer.shuffle(order)
        scenario_seed = randomizer.randrange(0, 2**31)
        for variant in order:
            trials.append({"id": f"r{repeat + 1:02d}-{variant}", "repeat": repeat + 1,
                           "variant": variant, "seed": scenario_seed})
    return trials


class ProcessCapture:
    def __init__(self, command, cwd, log_path, ready_pattern=None):
        self.ready = threading.Event()
        self.truncated = False
        self.error = None
        self.ack_event = threading.Event()
        self.ack_matcher = None
        self.ack_lock = threading.Lock()
        self.process = subprocess.Popen(command, cwd=cwd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT, start_new_session=(os.name == "posix"))
        self.thread = threading.Thread(target=self._capture, args=(log_path, ready_pattern), daemon=True)
        self.thread.start()

    def _capture(self, log_path, pattern):
        matcher = re.compile(pattern) if pattern else None
        seen = ""
        size = 0
        try:
            with Path(log_path).open("wb") as file:
                while True:
                    block = self.process.stdout.read1(8192)
                    if not block:
                        break
                    if size < 32 * 1024 * 1024:
                        file.write(block[:32 * 1024 * 1024 - size])
                    else:
                        self.truncated = True
                    size += len(block)
                    decoded = block.decode("utf-8", "replace")
                    with self.ack_lock:
                        if self.ack_matcher:
                            self.ack_text = (self.ack_text + decoded)[-65536:]
                            if self.ack_matcher.search(self.ack_text):
                                self.ack_event.set()
                    if matcher and not self.ready.is_set():
                        seen = (seen + decoded)[-65536:]
                        if matcher.search(seen):
                            self.ready.set()
        except Exception as error:
            self.error = str(error)

    def send(self, lines):
        for line in lines:
            encoded = (line + "\n").encode("utf-8")
            if len(encoded) > 1023:
                raise ValueError("Console line exceeds the official 1024-byte input buffer")
            if self.process.poll() is not None:
                raise RuntimeError("Server exited before console command")
            # The stock console treats an entire stdin read as one command. Waiting
            # for its logged acknowledgement prevents consecutive writes coalescing.
            with self.ack_lock:
                self.ack_event.clear()
                self.ack_text = ""
                self.ack_matcher = re.compile(r"Handling Console Command[ \t]+" + re.escape(line) + r"[ \t]*(?:\r?\n|$)")
            self.process.stdin.write(encoded)
            self.process.stdin.flush()
            if not self.ack_event.wait(5):
                raise RuntimeError("Official server console acknowledgement missing; no further command was sent")
            with self.ack_lock:
                self.ack_matcher = None

    def stop(self, timeout):
        if self.process.poll() is None:
            if os.name == "posix":
                os.killpg(self.process.pid, signal.SIGTERM)
            else:
                self.process.terminate()
            try:
                self.process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                if os.name == "posix":
                    os.killpg(self.process.pid, signal.SIGKILL)
                else:
                    self.process.kill()
                self.process.wait(timeout=5)
        self.thread.join(timeout=2)
        if self.process.stdin:
            try:
                self.process.stdin.close()
            except BrokenPipeError:
                pass
        if self.process.stdout and not self.thread.is_alive():
            self.process.stdout.close()
        return self.process.returncode


def linux_sample(pid):
    """Only the exact launched server PID; children excluded and explicitly reported."""
    proc = Path("/proc") / str(pid)
    stat = (proc / "stat").read_text().rsplit(")", 1)[1].split()
    ticks = os.sysconf("SC_CLK_TCK")
    rss = int(stat[21]) * os.sysconf("SC_PAGE_SIZE")
    result = {"cpuSeconds": (int(stat[11]) + int(stat[12])) / ticks, "workingSetBytes": rss}
    io_path = proc / "io"
    if io_path.exists():
        for line in io_path.read_text().splitlines():
            key, value = line.split(":", 1)
            if key in ("read_bytes", "write_bytes"):
                result[key] = int(value.strip())
    return result


def expanded(command, values):
    return [str(part).format_map(values) for part in command]


def wait_phase(server, seconds, samples, driver=None, raw_samples=None):
    end = time.monotonic() + seconds
    previous = None
    while time.monotonic() < end:
        if server.process.poll() is not None:
            raise RuntimeError("Server exited during workload")
        if driver and driver.process.poll() is not None:
            if driver.process.returncode != 0:
                raise RuntimeError("Workload generator exited with an error")
            if end - time.monotonic() > 2:
                raise RuntimeError("Workload generator exited before its measurement phase ended")
        if samples is not None:
            now = time.monotonic()
            try:
                sample = linux_sample(server.process.pid)
                sample.update({"timestampUtc": utc_now(), "monotonicSeconds": now})
                if previous:
                    sample["cpuPercent"] = 100 * (sample["cpuSeconds"] - previous["cpuSeconds"]) / (now - previous["monotonicSeconds"])
                samples.append(sample)
                if raw_samples is not None:
                    raw_samples["count"] += 1
                    if raw_samples["file"].tell() < 64 * 1024 * 1024:
                        raw_samples["file"].write(json.dumps(sample) + "\n")
                    else:
                        raw_samples["truncated"] = True
                previous = sample
            except (OSError, ValueError, IndexError):
                pass
        time.sleep(min(1, max(0, end - time.monotonic())))


def host_identity():
    identity = {"platform": platform.platform(), "cpuCount": os.cpu_count(), "python": platform.python_version()}
    for key, path in (("cpuMax", "/sys/fs/cgroup/cpu.max"), ("memoryMax", "/sys/fs/cgroup/memory.max"),
                      ("cpuset", "/sys/fs/cgroup/cpuset.cpus.effective")):
        try:
            identity[key] = Path(path).read_text().strip()
        except OSError:
            identity[key] = None
    return identity


def run_trial(config, config_dir, directory, trial, identity):
    directory.mkdir(parents=True)
    variant = config["variants"][trial["variant"]]
    template = (config_dir / variant["templateDirectory"]).resolve()
    clone = directory / "clone"
    trial = dict(trial, status="failed", startedUtc=utc_now(), environment=host_identity(),
                 profiler=None, workload=None, health=None, missingMetrics=[], failures=[])
    samples = deque(maxlen=8192)
    resource_file = (directory / "server-resources.jsonl").open("w", encoding="utf-8")
    raw_samples = {"file": resource_file, "count": 0, "truncated": False}
    server = driver = None
    try:
        current = validate_config(config, config_dir)
        if current != identity:
            trial["identityChanged"] = True
            raise RuntimeError("Prepared input hashes changed since campaign plan")
        shutil.copytree(template, clone)
        world = (config_dir / config["worldSnapshot"]).resolve()
        shutil.copytree(world, contained(clone, variant["worldRelativePath"]))
        values = {"trial_id": trial["id"], "seed": trial["seed"], "output_dir": str(directory.resolve()),
                  "clone_dir": str(clone.resolve()), "scenario": config["scenario"], "phase": "warmup",
                  "duration_seconds": config["warmupSeconds"]}
        server = ProcessCapture(expanded(variant["serverCommand"], values), clone, directory / "server.log", variant["readyPattern"])
        deadline = time.monotonic() + config["startupTimeoutSeconds"]
        while not server.ready.wait(.1):
            if server.process.poll() is not None or time.monotonic() >= deadline:
                raise RuntimeError("Server readiness not evidenced within startup timeout")
        if config.get("workloadCommand"):
            driver = ProcessCapture(expanded(config["workloadCommand"], values), clone, directory / "workload-warmup.log")
        wait_phase(server, config["warmupSeconds"], None, driver)
        if driver:
            try:
                driver.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                raise RuntimeError("Warmup workload exceeded its phase deadline")
            if driver.process.returncode != 0:
                raise RuntimeError("Warmup workload failed")
            driver.stop(5)
            driver = None
        values.update(phase="measurement", duration_seconds=config["measurementSeconds"])
        trial["measurementStartedUtc"] = utc_now()
        dump(directory / "phase.json", {"phase": "measurement", "startedUtc": trial["measurementStartedUtc"],
                                      "durationSeconds": config["measurementSeconds"]})
        server.send(variant.get("startMeasurementStdin", []))
        if config.get("workloadCommand"):
            driver = ProcessCapture(expanded(config["workloadCommand"], values), clone, directory / "workload-measurement.log")
        wait_phase(server, config["measurementSeconds"], samples, driver, raw_samples)
        trial["measurementStoppedUtc"] = utc_now()
        if driver:
            try:
                driver.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                raise RuntimeError("Measured workload exceeded its phase deadline")
            if driver.process.returncode != 0:
                raise RuntimeError("Measured workload failed")
        server.send(variant.get("stopMeasurementStdin", []))
        if variant.get("profilerReport"):
            profile_path = contained(clone, variant["profilerReport"])
            export_deadline = time.monotonic() + 10
            measurement_start = dt.datetime.fromisoformat(trial["measurementStartedUtc"]).timestamp()
            while time.monotonic() < export_deadline:
                if profile_path.is_file() and profile_path.stat().st_mtime >= measurement_start:
                    break
                if server.process.poll() is not None:
                    raise RuntimeError("Server exited before fresh profiler export was observed")
                time.sleep(.1)
            else:
                raise RuntimeError("Fresh profiler export was not observed before shutdown")
        server.send(variant.get("shutdownStdin", ["stop"]))
        try:
            server.process.wait(timeout=config["shutdownTimeoutSeconds"])
        except subprocess.TimeoutExpired:
            raise RuntimeError("Server failed graceful shutdown")
        server.thread.join(timeout=2)
        if server.process.returncode != 0:
            raise RuntimeError(f"Server exited with status {server.process.returncode}")
        trial["status"] = "completed"
    except (Exception, KeyboardInterrupt) as error:
        trial["failures"].append(str(error) or "Interrupted")
        if isinstance(error, KeyboardInterrupt):
            trial["status"] = "interrupted"
    finally:
        resource_file.close()
        if driver:
            trial["workloadExitCode"] = driver.stop(5)
        if server:
            trial["serverExitCode"] = server.stop(5)
            trial["serverLogTruncated"] = server.truncated
            if server.error:
                trial["failures"].append("log capture: " + server.error)
                trial["status"] = "failed"
        for field, reader in (("profilerReport", lambda p: read_profiler(p, config["measurementSeconds"])),
                              ("workloadReport", read_workload), ("healthReport", load_object)):
            if variant.get(field):
                path = contained(clone, variant[field])
                try:
                    value = reader(path)
                    if field == "profilerReport":
                        trial["profiler"], trial["missingMetrics"] = value
                    else:
                        trial["workload" if field == "workloadReport" else "health"] = value
                except (OSError, ValueError, TypeError, KeyError) as error:
                    trial["failures"].append(field + ": " + str(error))
                    trial["missingMetrics"].append(field)
        if not trial["profiler"]:
            trial["missingMetrics"].append("profiler export unavailable")
        trial["resources"] = {"scope": "exact server process PID only; workload generator excluded; subprocesses excluded",
                              "samples": list(samples), "samplesCollected": raw_samples["count"],
                              "samplesOverwritten": max(0, raw_samples["count"] - len(samples)),
                              "rawSamplesFile": "server-resources.jsonl", "rawSamplesTruncated": raw_samples["truncated"],
                              "summaryScope": "retained last 8192 samples; raw JSONL provides earlier samples up to disk cap",
                              "cpuPercent": describe([s.get("cpuPercent") for s in samples]),
                              "workingSetBytes": describe([s.get("workingSetBytes") for s in samples])}
        trial["finishedUtc"] = utc_now()
        dump(directory / "trial.json", trial)
    return trial


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("validate", "plan", "run", "compare"))
    parser.add_argument("--config", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--authorized-test-clones", action="store_true", help="Owner authorizes execution only on explicitly prepared disposable local test clones")
    parser.add_argument("--trial", action="append", type=Path, default=[], help="trial.json file for compare")
    args = parser.parse_args(argv)
    try:
        if args.command == "compare":
            trials = [load_object(path) for path in args.trial]
            write_outputs(build_comparison(trials, {"identityValidated": False, "note": "Imported trials require independent identity review"}), args.output)
            return 0
        if not args.config:
            parser.error("--config is required")
        config = load_object(args.config)
        config_dir = args.config.resolve().parent
        identity = validate_config(config, config_dir)
        output = args.output.resolve()
        for variant in config["variants"].values():
            if output.is_relative_to((config_dir / variant["templateDirectory"]).resolve()):
                raise ValueError("Output directory must not be inside a template clone")
        if output.is_relative_to((config_dir / config["worldSnapshot"]).resolve()):
            raise ValueError("Output directory must not be inside the frozen world snapshot")
        plan = make_plan(config)
        if args.command == "validate":
            print(json.dumps({"valid": True, "identity": identity}, indent=2))
            return 0
        if output.exists() and any(output.iterdir()):
            raise ValueError("Output directory must be absent or empty; existing evidence is never overwritten")
        output.mkdir(parents=True, exist_ok=True)
        dump(output / "plan.json", {"schemaVersion": 1, "trials": plan, "identity": identity,
                                   "configuration": config, "environment": host_identity()})
        if args.command == "plan":
            print(f"Prepared {len(plan)} randomized trials; no process was started.")
            return 0
        if not args.authorized_test_clones:
            raise ValueError("run requires --authorized-test-clones; no server process was started")
        trials = []
        campaign = {"createdUtc": utc_now(), "gameVersion": config["gameVersion"], "scenario": config["scenario"],
                    "identityValidated": True, "identity": identity, "resources": config["resources"],
                    "warmupSeconds": config["warmupSeconds"], "measurementSeconds": config["measurementSeconds"], "seed": config["seed"],
                    "interventions": {key: value.get("enabledOptimizations", []) for key, value in config["variants"].items()}}
        for entry in plan:
            print(f"Starting {entry['id']} seed={entry['seed']}", flush=True)
            trial = run_trial(config, config_dir, output / "trials" / entry["id"], entry, identity)
            trials.append(compact_trial(trial))
            write_outputs(build_comparison(trials, campaign), output)
            if trial["status"] != "completed":
                print(f"Stopped after {entry['id']}: {'; '.join(trial['failures'])}", file=sys.stderr)
                return 1
        return 0
    except (OSError, ValueError, TypeError, KeyError) as error:
        print("Benchmark: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
