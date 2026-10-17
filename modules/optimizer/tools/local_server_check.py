#!/usr/bin/env python3
"""Isolated Linux idle smoke check. This is not a multiplayer benchmark."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import threading
import time
import zipfile


def read_process(pid: int) -> dict:
    result = {"timestampUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    try:
        raw = Path(f"/proc/{pid}/stat").read_text()
        fields = raw[raw.rfind(")") + 2:].split()
        result["processCpuSeconds"] = (int(fields[11]) + int(fields[12])) / os.sysconf("SC_CLK_TCK")
        result["workingSetBytes"] = int(fields[21]) * os.sysconf("SC_PAGE_SIZE")
        result["virtualBytes"] = int(fields[20])
    except (OSError, ValueError, IndexError):
        result["processMetricsUnavailable"] = True
    return result


def wait_alive(process: subprocess.Popen, seconds: float) -> None:
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        if process.poll() is not None:
            raise RuntimeError(f"Server exited early with {process.returncode}")
        time.sleep(min(.25, max(0, end - time.monotonic())))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, required=True)
    parser.add_argument("--snapshot", type=Path, required=True, help="Stopped disposable template data directory")
    parser.add_argument("--output", type=Path, required=True, help="Fresh output directory; existing paths refused")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--modzip", type=Path)
    parser.add_argument("--engine-patches", action="store_true", help="Enable guarded API modules for startup/save checks; no throughput claim")
    parser.add_argument("--mode", choices=["stock", "disabled", "profile", "detailed"], default="profile")
    parser.add_argument("--seconds", type=int, default=60)
    parser.add_argument("--warmup", type=int, default=20)
    parser.add_argument("--port", type=int, default=42577)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("Output exists; use a fresh path. Existing data is never overwritten.")
    if not 30 <= args.seconds <= 21600 or not 0 <= args.warmup <= 3600:
        parser.error("Measurement must be 30..21600 seconds; warmup 0..3600 seconds")
    if args.mode != "stock" and (args.modzip is None or not args.modzip.is_file()):
        parser.error("A compiled --modzip is required for non-stock modes")
    game = args.game_dir.resolve()
    output = args.output.resolve()
    snapshot = args.snapshot.resolve()
    if output.is_relative_to(snapshot) or output.is_relative_to(game):
        parser.error("Output must be outside both the snapshot and the official game directory")
    if not (snapshot / "serverconfig.json").is_file():
        parser.error("Template must contain serverconfig.json")
    if any(path.is_symlink() for path in snapshot.rglob("*")):
        parser.error("Snapshot symlinks are refused; provide a separate stopped test copy")
    # Require a readable, cleanly saved template before starting a second process.
    template_config = json.loads((snapshot / "serverconfig.json").read_text())
    saved_name = Path(template_config["WorldConfig"]["SaveFileLocation"]).name
    template_save = snapshot / "Saves" / saved_name
    with sqlite3.connect(f"file:{template_save}?mode=ro", uri=True) as db:
        if db.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
            raise RuntimeError("Template save failed integrity check")
    output.mkdir(parents=True)
    data = output / "data"
    shutil.copytree(snapshot, data, ignore=shutil.ignore_patterns("Logs", "Cache", "Backups"))
    config_path = data / "serverconfig.json"
    config = json.loads(config_path.read_text())
    config.update(Ip="127.0.0.1", Port=args.port, AdvertiseServer=False, Upnp=False,
                  VerifyPlayerAuth=True, PassTimeWhenEmpty=True,
                  ModPaths=["Mods", str(data / "Mods")])
    config["WorldConfig"]["SaveFileLocation"] = str(data / "Saves" / saved_name)
    config_path.write_text(json.dumps(config, indent=2))
    if args.mode != "stock":
        (data / "Mods").mkdir(exist_ok=True)
        shutil.copy2(args.modzip, data / "Mods" / args.modzip.name)
        config_dir = data / "ModConfig"
        config_dir.mkdir(exist_ok=True)
        optimizer = {"Enabled": args.mode != "disabled", "Profiling": {
            "AutoStart": False, "SampleIntervalSeconds": 1, "MaxSamples": 600,
            "MainThreadCpu": args.mode == "detailed", "SystemTimings": args.mode == "detailed",
            "PhaseTimings": args.mode == "detailed", "CallbackTimings": args.mode == "detailed",
            "ExportOnStop": True}, "Compatibility": {"SafeMode": args.mode != "detailed" and not args.engine_patches}}
        (config_dir / "optimizer.json").write_text(json.dumps(optimizer, indent=2))
    env = os.environ.copy()
    env["DOTNET_ROOT"] = str(Path(args.dotnet).resolve().parent) if "/" in args.dotnet else env.get("DOTNET_ROOT", "")
    command = [args.dotnet, str(game / "VintagestoryServer.dll"), "--dataPath", str(data),
               "--ip", "127.0.0.1", "--port", str(args.port)]
    ready = threading.Event()
    log_truncated = threading.Event()
    acknowledgements: dict[str, threading.Event] = {}
    command_gate = threading.Lock()
    started = time.monotonic()
    process = subprocess.Popen(command, cwd=game, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, text=True, bufsize=1, env=env)

    def read_output() -> None:
        written = 0
        with (output / "console.log").open("w") as log:
            for line in process.stdout:
                if "Dedicated Server now running on Port" in line:
                    ready.set()
                if "Handling Console Command " in line:
                    with command_gate:
                        for command_text, signal in acknowledgements.items():
                            if ("Handling Console Command " + command_text) in line:
                                signal.set()
                if written < 16 * 1024 * 1024:
                    written += log.write(line)
                    log.flush()
                else:
                    log_truncated.set()

    reader = threading.Thread(target=read_output, daemon=True)
    reader.start()
    samples = []
    result = {"kind": "isolated-idle-smoke", "mode": args.mode, "enginePatchesRequested": args.engine_patches, "authenticatedClients": 0,
              "multiplayerPerformanceEvidence": False, "gameVersion": "1.22.7",
              "measurementSeconds": args.seconds, "warmupSeconds": args.warmup,
              "snapshotSha256": hashlib.sha256(template_save.read_bytes()).hexdigest(),
              "serverPid": process.pid, "command": command}
    if args.mode != "stock":
        result["optimizerZipSha256"] = hashlib.sha256(args.modzip.read_bytes()).hexdigest()
        with zipfile.ZipFile(args.modzip) as archive:
            result["optimizerAssemblySha256"] = hashlib.sha256(archive.read("NewfiesServerOptimizer.dll")).hexdigest()

    def send(text: str) -> None:
        # 1.22.7 ServerConsole passes an entire stdin ReadAsync buffer as one
        # command; it does not split multiple newline-delimited commands.
        signal = threading.Event()
        with command_gate:
            acknowledgements[text] = signal
        process.stdin.write(text + "\n")
        process.stdin.flush()
        deadline = time.monotonic() + 10
        while not signal.wait(.1):
            if process.poll() is not None or time.monotonic() >= deadline:
                raise RuntimeError(f"Server did not acknowledge console command: {text}")

    try:
        while not ready.wait(.25):
            if process.poll() is not None or time.monotonic() - started > 180:
                raise RuntimeError("Server did not reach dedicated-server ready state")
        result["startupSeconds"] = time.monotonic() - started
        wait_alive(process, args.warmup)
        if args.mode != "stock":
            send("/optimizer modules")
        if args.mode in ("profile", "detailed"):
            send("/optimizer profile start")
        elif args.mode == "disabled":
            send("/optimizer profile start")
            send("/optimizer status")
        send("/stats")
        measured = time.monotonic()
        while time.monotonic() - measured < args.seconds:
            sample = read_process(process.pid)
            sample["elapsedSeconds"] = time.monotonic() - measured
            samples.append(sample)
            wait_alive(process, 1)
        send("/stats")
        if args.mode in ("profile", "detailed"):
            send("/optimizer profile stop")
            wait_alive(process, 2)
            send("/optimizer report")
            if args.mode == "detailed":
                for subcommand in ("status", "hotspots", "entities", "chunks", "network", "gc", "benchmark"):
                    send("/optimizer " + subcommand)
            wait_alive(process, 2)
        send("/save")
        wait_alive(process, 2)
        send("/stop")
        process.wait(timeout=45)
        reader.join(timeout=2)
        result["exitCode"] = process.returncode
        result["consoleTruncated"] = log_truncated.is_set()
        save = data / "Saves" / saved_name
        with sqlite3.connect(f"file:{save}?mode=ro", uri=True) as db:
            result["saveIntegrity"] = db.execute("PRAGMA integrity_check").fetchone()[0]
            tables = [r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")]
            result["saveRows"] = {t: db.execute('SELECT COUNT(*) FROM "' + t.replace('"', '""') + '"').fetchone()[0] for t in tables}
        result["samples"] = samples
        console = (output / "console.log").read_text()
        result["optimizerAcknowledged"] = None if args.mode == "stock" else "[NewfiesOptimizer]" in console
        result["disabledCaptureRejected"] = ("Optimizer disabled in optimizer.json." in console
                                               and "Capture started." not in console) if args.mode == "disabled" else None
        profiles = []
        if args.mode in ("profile", "detailed"):
            for path in data.rglob("profile-*.json"):
                try:
                    profile = json.loads(path.read_text())
                    if profile.get("kind") == "newfies-optimizer-profile" and profile.get("isRunning") is False:
                        profiles.append({"path": str(path.relative_to(output)),
                                         "samples": len(profile.get("samples", [])),
                                         "tickCount": profile.get("tick", {}).get("count", 0),
                                         "hotspotCount": len(profile.get("hotspots", [])),
                                         "engineModules": {m.get("name"): m.get("status") for m in profile.get("modules", []) if m.get("name") in ("onlinePlayerSnapshots", "nearbyPlayerQueries", "bulkStreamReads")},
                                         "engineCounters": profile.get("engineCounters", {}),
                                         "timingModules": {m.get("name"): m.get("status") for m in profile.get("modules", [])
                                                           if m.get("name") in ("systemTimings", "phaseTimings", "callbackTimings")},
                                         "durationSeconds": profile.get("durationSeconds")})
                except (ValueError, OSError):
                    pass
        result["profileExports"] = profiles
        result["validIdleCheck"] = (process.returncode == 0 and result["saveIntegrity"] == "ok"
                                    and not log_truncated.is_set()
                                    and (args.mode == "stock" or result["optimizerAcknowledged"])
                                    and (args.mode != "disabled" or result["disabledCaptureRejected"])
                                    and (args.mode not in ("profile", "detailed") or any(
                                        p["samples"] >= args.seconds // 2 and p["tickCount"] > 0 for p in profiles))
                                    and (args.mode != "detailed" or any(p["hotspotCount"] > 0 and all(
                                        p["timingModules"].get(group) == "captured" for group in
                                        ("systemTimings", "phaseTimings", "callbackTimings")) for p in profiles)))
        result["serverErrors"] = [line.strip() for line in console.splitlines() if any(tag in line for tag in ("[Server Error]", "[Error]", "[Server Fatal]"))]
        result["engineModulesVerified"] = (not args.engine_patches or args.mode in ("stock", "disabled") or any(
            profile["engineModules"].get("onlinePlayerSnapshots") == "active" and profile["engineModules"].get("bulkStreamReads") == "active"
            for profile in profiles))
        result["validIdleCheck"] = result["validIdleCheck"] and not result["serverErrors"] and result["engineModulesVerified"]
    except BaseException as error:
        result["failure"] = f"{type(error).__name__}: {error}"
        result["samples"] = samples
        result["validIdleCheck"] = False
        if process.poll() is None:
            try:
                send("/stop")
                process.wait(timeout=20)
            except (OSError, subprocess.TimeoutExpired):
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
    finally:
        (output / "result.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps({k: v for k, v in result.items() if k not in ("samples", "command")}, indent=2))
    return 0 if result.get("validIdleCheck") else 1


if __name__ == "__main__":
    raise SystemExit(main())
