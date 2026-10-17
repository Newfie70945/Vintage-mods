#!/usr/bin/env python3
"""Newfie's VS Stress Tester: bounded X11 automation of licensed official clients.

No network protocol, authentication, or game simulation is implemented here.
JSONL server evidence is required for real runs. UI input is an attempt, never a
successful transaction. `--dry-run` tests the tooling only.
"""
from __future__ import annotations

import argparse
import concurrent.futures
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import random
import re
import shutil
import signal
import subprocess
import sys
import threading
import time
import uuid

VERSION = "0.1.0"
POPULATION_TARGETS = [1, 5, 10, 20, 30, 50, 75, 100, 150, 200]
SCENARIOS = {
    "idle": [("idle", 1)],
    "exploration": [("explore", 5), ("jump", 1)],
    "concentrated": [("move", 3), ("jump", 1), ("inventory", 1), ("interact", 1)],
    "mob_horde": [("move", 2), ("combat", 4)],
    "heavy_automation": [("interact", 3), ("inventory_transfer", 1), ("move", 1)],
    "combat": [("combat", 5), ("move", 1)],
    "rapid_exploration": [("teleport", 2), ("explore", 1)],
    "heavily_modded": [("move", 3), ("mine", 1), ("place", 1), ("inventory_transfer", 1)],
    "endurance": [("move", 4), ("jump", 1), ("inventory", 1), ("reconnect", 1)],
    "fractured_night": [("combat", 4), ("move", 2), ("jump", 1)],
    "building": [("mine", 2), ("place", 2), ("inventory_transfer", 1)],
}
SCENE_REQUIRED = {"mob_horde", "heavy_automation", "combat", "heavily_modded", "fractured_night", "building"}
KEY = re.compile(r"^[A-Za-z0-9_+]{1,48}$")
ID = re.compile(r"^[A-Za-z0-9_.:-]{1,100}$")
DISPLAY = re.compile(r"^(?:localhost)?:[0-9]{1,5}(?:\.[0-9]{1,2})?$")


class ConfigError(ValueError):
    pass


def utc_now():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def utc_seconds(value):
    if not isinstance(value, str):
        raise ValueError("utc must be an ISO-8601 string")
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("utc needs an explicit timezone")
    return parsed.timestamp()


def number(value, name, minimum, maximum):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or not minimum <= value <= maximum:
        raise ConfigError(f"{name} must be between {minimum} and {maximum}")
    return value


def integer(value, name, minimum, maximum):
    number(value, name, minimum, maximum)
    if int(value) != value:
        raise ConfigError(f"{name} must be an integer")
    return int(value)


def atomic_json(path, data):
    path = Path(path)
    temporary = path.with_name(path.name + ".tmp")
    with temporary.open("w", encoding="utf-8") as output:
        json.dump(data, output, indent=2, allow_nan=False)
        output.write("\n")
    temporary.replace(path)


def default_macros(bindings):
    key = lambda value: {"op": "key", "key": value}
    hold = lambda keys, seconds: {"op": "hold", "keys": keys, "seconds": seconds}
    return {
        "idle": [{"op": "wait", "seconds": 0.1}],
        "move": [hold([bindings["forward"]], 0.8)],
        "jump": [key(bindings["jump"])],
        "explore": [{"op": "look", "x": 90, "y": 0}, hold([bindings["forward"], bindings["sprint"]], 1.5), key(bindings["jump"])],
        "mine": [{"op": "mouse_hold", "button": 1, "seconds": 1.5}],
        "place": [{"op": "click", "button": 3}],
        "interact": [{"op": "click", "button": 3}, {"op": "wait", "seconds": 0.4}, key(bindings["close"])],
        "inventory": [key(bindings["inventory"]), {"op": "wait", "seconds": 0.4}, key(bindings["close"])],
        "combat": [{"op": "mouse_hold", "button": 1, "seconds": 0.4}],
    }


def validate_macro(name, steps, limits, allowed_commands):
    if not isinstance(name, str) or not ID.fullmatch(name):
        raise ConfigError("Macro names must be simple identifiers")
    if not isinstance(steps, list) or not 1 <= len(steps) <= 64:
        raise ConfigError(f"Macro {name} needs 1..64 steps")
    duration = 0.0
    for step in steps:
        if not isinstance(step, dict):
            raise ConfigError(f"Invalid step in {name}")
        op = step.get("op")
        if op in ("key", "hold"):
            keys = [step.get("key")] if op == "key" else step.get("keys")
            if not isinstance(keys, list) or not 1 <= len(keys) <= 4 or any(not isinstance(k, str) or not KEY.fullmatch(k) for k in keys):
                raise ConfigError(f"Invalid X11 key in {name}")
        if op in ("hold", "wait", "mouse_hold"):
            duration += number(step.get("seconds"), f"{name}.seconds", 0.01, 10)
        if op in ("click", "mouse_hold"):
            integer(step.get("button"), f"{name}.button", 1, 5)
        if op in ("look", "point"):
            integer(step.get("x"), f"{name}.x", -1000 if op == "look" else 0, 1000 if op == "look" else 16384)
            integer(step.get("y"), f"{name}.y", -1000 if op == "look" else 0, 1000 if op == "look" else 16384)
        if op == "command":
            command = step.get("text")
            if not isinstance(command, str) or not 1 <= len(command) <= 256 or "\n" in command or "\r" in command or not command.startswith("/"):
                raise ConfigError(f"{name}: command must be one explicit server command")
            if command not in allowed_commands:
                raise ConfigError(f"{name}: command is absent from exact allowedCommands list")
            duration += 0.3
        if op not in {"key", "hold", "wait", "click", "mouse_hold", "look", "point", "command"}:
            raise ConfigError(f"Unsupported macro operation {op!r}")
    if duration > limits["maxActionSeconds"]:
        raise ConfigError(f"Macro {name} exceeds maxActionSeconds")


def load_config(path):
    path = Path(path)
    if path.stat().st_size > 1024 * 1024:
        raise ConfigError("Configuration exceeds 1 MiB")
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict) or data.get("schemaVersion") != 1:
        raise ConfigError("Expected configuration schemaVersion 1")
    server = data.get("server", {})
    if not isinstance(server, dict) or not isinstance(server.get("address"), str) or not 1 <= len(server["address"]) <= 253:
        raise ConfigError("server.address is required")
    integer(server.get("port"), "server.port", 1, 65535)
    limits = {"maxClients": 4, "maxParallelDisplays": 4, "maxDurationSeconds": 21600, "maxActionSeconds": 10,
              "actionIntervalSeconds": 4, "startupTimeoutSeconds": 60, "resourceIntervalSeconds": 5,
              "maxEvents": 100000, "evidenceMaxBytes": 16 * 1024 * 1024, "presenceExpirySeconds": 30,
              "freshConnectionSeconds": 60, "requirePresenceHeartbeat": True}
    overrides = data.get("limits", {})
    if not isinstance(overrides, dict) or any(k not in limits for k in overrides):
        raise ConfigError("Unknown limits field")
    limits.update(overrides)
    integer(limits["maxClients"], "maxClients", 1, 200)
    integer(limits["maxParallelDisplays"], "maxParallelDisplays", 1, 32)
    integer(limits["maxEvents"], "maxEvents", 100, 1000000)
    integer(limits["evidenceMaxBytes"], "evidenceMaxBytes", 1024, 256 * 1024 * 1024)
    for name, minimum, maximum in [("maxDurationSeconds", 1, 86400), ("maxActionSeconds", 0.1, 30),
                                   ("actionIntervalSeconds", 0.05, 120), ("startupTimeoutSeconds", 1, 300),
                                   ("resourceIntervalSeconds", 0.2, 60), ("presenceExpirySeconds", 5, 300),
                                   ("freshConnectionSeconds", 1, 300)]:
        number(limits[name], name, minimum, maximum)
    if not isinstance(limits["requirePresenceHeartbeat"], bool):
        raise ConfigError("requirePresenceHeartbeat must be boolean")
    clients = data.get("clients")
    if not isinstance(clients, list) or not 1 <= len(clients) <= limits["maxClients"]:
        raise ConfigError("clients must be nonempty and within maxClients")
    seen_ids, seen_windows = set(), set()
    for client in clients:
        if not isinstance(client, dict) or not isinstance(client.get("id"), str) or not ID.fullmatch(client["id"]):
            raise ConfigError("Each client needs its exact server PlayerUID as id")
        if not isinstance(client.get("display"), str) or not DISPLAY.fullmatch(client["display"]):
            raise ConfigError("Each client needs a local X11 display, such as :1")
        window = str(client.get("window", ""))
        if not re.fullmatch(r"(?:0x[0-9a-fA-F]+|[0-9]+)", window) or int(window, 0 if window.startswith("0x") else 10) <= 0:
            raise ConfigError("Each client needs an existing numeric X11 window id")
        client["window"] = str(int(window, 0 if window.startswith("0x") else 10))
        pid = client.get("processId")
        if pid is not None:
            integer(pid, "processId", 1, 2**31 - 1)
        if not isinstance(client.get("connectOnStart", False), bool):
            raise ConfigError("connectOnStart must be boolean")
        window_key = (client["display"], client["window"])
        if client["id"] in seen_ids or window_key in seen_windows:
            raise ConfigError("Duplicate client id or X11 window")
        seen_ids.add(client["id"])
        seen_windows.add(window_key)
    bindings = {"forward": "w", "sprint": "Control_L", "jump": "space", "inventory": "e", "close": "Escape", "chat": "t"}
    bindings.update(data.get("bindings", {}))
    if any(not isinstance(v, str) or not KEY.fullmatch(v) for v in bindings.values()):
        raise ConfigError("Invalid binding")
    commands = data.get("allowedCommands", [])
    if not isinstance(commands, list) or len(commands) > 128 or any(not isinstance(c, str) for c in commands):
        raise ConfigError("allowedCommands must contain at most 128 explicit commands")
    macros = default_macros(bindings)
    custom_macros = data.get("macros", {})
    if not isinstance(custom_macros, dict) or len(custom_macros) > 128:
        raise ConfigError("macros must be an object with at most 128 definitions")
    macros.update(custom_macros)
    for name, steps in macros.items():
        validate_macro(name, steps, limits, commands)
    evidence = data.get("serverEvidenceFile")
    if evidence is not None and not isinstance(evidence, str):
        raise ConfigError("serverEvidenceFile must be a local path")
    if evidence:
        evidence = str((path.parent / evidence).resolve()) if not Path(evidence).is_absolute() else evidence
    scenes = data.get("scenes", {})
    if not isinstance(scenes, dict):
        raise ConfigError("scenes must be an object")
    data.update(clients=clients, limits=limits, bindings=bindings, macros=macros, scenes=scenes, serverEvidenceFile=evidence)
    return data


def actions_for(config, scenario):
    weighted = SCENARIOS[scenario]
    for name, _ in weighted:
        if name not in config["macros"]:
            raise ConfigError(f"Scenario {scenario} requires a configured {name!r} macro; no game UI coordinates or commands are assumed")
    if scenario in SCENE_REQUIRED:
        scene = config["scenes"].get(scenario, {})
        if not isinstance(scene, dict) or scene.get("prepared") is not True or not scene.get("description"):
            raise ConfigError(f"Scenario {scenario} requires a manually prepared and documented scene")
    for client in config["clients"]:
        if client.get("connectOnStart", False) and client.get("connectMacro", "connect") not in config["macros"]:
            raise ConfigError(f"Client {client['id']} needs its explicit connect UI macro")
    return weighted


def seeded_choices(seed, client_id, weighted):
    client_seed = int.from_bytes(hashlib.sha256(client_id.encode()).digest()[:8], "big") ^ seed
    rng = random.Random(client_seed)
    names, weights = zip(*weighted)
    while True:
        yield rng.choices(names, weights=weights, k=1)[0]


class EventSink:
    def __init__(self, path, maximum):
        self.path, self.maximum, self.count = Path(path), maximum, 0
        self.lock = threading.Lock()
        self.output = self.path.open("w", encoding="utf-8")

    def emit(self, **data):
        with self.lock:
            if self.count >= self.maximum:
                raise RuntimeError("Event limit reached; workload stopped instead of growing indefinitely")
            data.setdefault("utc", utc_now())
            self.output.write(json.dumps(data, allow_nan=False) + "\n")
            self.output.flush()
            self.count += 1

    def close(self):
        self.output.close()


class Evidence:
    """Read bounded, local, server-owned JSONL. Commands cannot forge evidence."""
    def __init__(self, path, run_id, clients, limits):
        self.path, self.run_id = Path(path), run_id
        self.clients, self.limits = set(clients), limits
        self.offset, self.identity = 0, None
        self.online, self.last_presence, self.acks = set(), {}, {}
        self.authenticated = {}
        self.authenticated_throughout = True
        self.peak = self.disconnects = self.invalid = self.read_count = 0
        self.ack_count = self.failure_count = 0
        self.work_completed_by_type = {}
        self.error = None
        self.lock = threading.Lock()
        self.heartbeat_seen = False
        self.pending = {}
        self.workload_minimum = None

    def expect(self, action_id, client_id, action_type=None):
        with self.lock:
            self.pending[action_id] = (client_id, time.time(), action_type)

    def begin_workload(self):
        with self.lock:
            self.workload_minimum = len(self.online)
            self.authenticated_throughout = all(self.authenticated.get(c) is True for c in self.clients)

    def poll(self, now=None):
        now = time.time() if now is None else now
        try:
            stat = self.path.stat()
            if stat.st_size > self.limits["evidenceMaxBytes"]:
                raise RuntimeError("Server evidence file exceeded configured limit; rotate it between runs")
            identity = (stat.st_dev, stat.st_ino)
            if self.identity is not None and (identity != self.identity or stat.st_size < self.offset):
                raise RuntimeError("Server evidence was replaced or truncated during the run")
            self.identity = identity
            with self.path.open("rb") as source:
                source.seek(self.offset)
                while True:
                    before = source.tell()
                    line = source.readline(65538)
                    if not line:
                        break
                    if len(line) > 65536:
                        raise RuntimeError("Server evidence line exceeds 64 KiB")
                    if not line.endswith(b"\n"):
                        source.seek(before)
                        break
                    self.offset = source.tell()
                    try:
                        event = json.loads(line)
                        self._apply(event, now)
                    except (ValueError, TypeError, KeyError, AttributeError):
                        self.invalid += 1
            if self.limits["requirePresenceHeartbeat"]:
                with self.lock:
                    for client in list(self.online):
                        if now - self.last_presence.get(client, 0) > self.limits["presenceExpirySeconds"]:
                            self.online.remove(client)
                            self.disconnects += 1
            with self.lock:
                if self.workload_minimum is not None:
                    self.workload_minimum = min(self.workload_minimum, len(self.online))
        except FileNotFoundError:
            if self.identity is not None:
                self.error = "Server evidence file disappeared during the run"
        except (OSError, RuntimeError) as error:
            self.error = str(error)

    def _apply(self, event, now):
        client = event["clientId"]
        if client not in self.clients or event.get("schemaVersion") != 1:
            return
        # A blank server RunId is allowed for connection evidence, never for action acknowledgements.
        if event.get("runId") not in (None, "", self.run_id):
            return
        stamp = utc_seconds(event["utc"])
        if stamp > now + 5:
            raise ValueError("Server clock is ahead by more than five seconds")
        kind = event["kind"]
        self.read_count += 1
        with self.lock:
            if kind in ("connected", "presence"):
                if now - stamp <= self.limits["freshConnectionSeconds"] and stamp >= self.last_presence.get(client, 0):
                    self.online.add(client)
                    self.last_presence[client] = stamp
                    self.authenticated[client] = event.get("authenticationEnabled") is True
                    if self.workload_minimum is not None:
                        self.authenticated_throughout &= self.authenticated[client]
                    self.peak = max(self.peak, len(self.online))
                    self.heartbeat_seen |= kind == "presence"
            elif kind == "disconnected":
                # Ignore stale leaves preceding a more recent heartbeat for the same identity.
                if stamp >= self.last_presence.get(client, 0) and client in self.online:
                    self.online.remove(client)
                    self.disconnects += 1
            elif kind in ("action_confirmed", "action_failed"):
                action = event.get("actionId")
                expected = self.pending.get(action)
                if (event.get("runId") == self.run_id and expected is not None
                        and expected[0] == client and stamp >= expected[1] - 5 and action not in self.acks):
                    self.acks[action] = {"kind": kind, "clientId": client, "utc": event["utc"], "detail": str(event.get("detail", ""))[:500]}
                    self.ack_count += kind == "action_confirmed"
                    self.failure_count += kind == "action_failed"
                    # Attribute accepted outcomes to the attempted action, never to a claimed
                    # type in external evidence. Unknown attribution deliberately stays absent.
                    if kind == "action_confirmed" and expected[2] is not None:
                        action_type = expected[2]
                        self.work_completed_by_type[action_type] = self.work_completed_by_type.get(action_type, 0) + 1
                        self.acks[action]["actionType"] = action_type
            if self.workload_minimum is not None:
                self.workload_minimum = min(self.workload_minimum, len(self.online))

    def is_online(self, client):
        with self.lock:
            return client in self.online

    def all_online(self):
        with self.lock:
            return len(self.online) == len(self.clients)


class X11Backend:
    def __init__(self, client, stop, deadline, chat_key, max_action_seconds=10):
        self.client, self.stop, self.deadline, self.chat_key = client, stop, deadline, chat_key
        self.max_action_seconds, self.action_deadline = max_action_seconds, None
        self.keys, self.buttons = set(), set()
        self.env = os.environ.copy()
        self.env["DISPLAY"] = client["display"]

    def command(self, *args):
        timeout = 5
        if self.action_deadline is not None:
            remaining = self.action_deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("Configured maximum action duration exceeded")
            timeout = min(timeout, remaining)
        result = subprocess.run(["xdotool", *map(str, args)], env=self.env, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout, check=False)
        if result.returncode:
            raise RuntimeError("xdotool failed: " + result.stderr.strip()[:300])
        return result.stdout.strip()

    def verify_window(self):
        if not shutil.which("xdotool"):
            raise RuntimeError("xdotool is required for real official-client automation")
        self.command("getwindowname", self.client["window"])
        actual_pid = int(self.command("getwindowpid", self.client["window"]))
        if self.client.get("processId") is not None and self.client["processId"] != actual_pid:
            raise RuntimeError("X11 window process id does not match configured official client process")
        self.client["processId"] = actual_pid

    def focus(self):
        self.command("windowactivate", "--sync", self.client["window"])
        self.assert_focus()

    def assert_focus(self):
        if self.command("getactivewindow") != self.client["window"]:
            raise RuntimeError("Official client window did not receive focus")

    def pause(self, seconds):
        until = min(time.monotonic() + seconds, self.deadline, self.action_deadline or math.inf)
        while time.monotonic() < until:
            if self.stop.wait(min(0.05, max(0, until - time.monotonic()))):
                raise InterruptedError("Workload stopped")
        if time.monotonic() >= self.deadline:
            raise InterruptedError("Workload duration reached")
        if self.action_deadline is not None and time.monotonic() >= self.action_deadline:
            raise TimeoutError("Configured maximum action duration exceeded")

    def perform(self, steps):
        self.action_deadline = min(self.deadline, time.monotonic() + self.max_action_seconds)
        try:
            self.focus()
            for step in steps:
                if self.stop.is_set() or time.monotonic() >= self.deadline:
                    raise InterruptedError("Workload stopped")
                self.assert_focus()
                op = step["op"]
                if op == "key":
                    self.command("key", "--clearmodifiers", "--", step["key"])
                elif op == "hold":
                    for key in step["keys"]:
                        self.keys.add(key)
                        self.command("keydown", "--", key)
                    self.pause(step["seconds"])
                    for key in step["keys"]:
                        self.command("keyup", "--", key)
                        self.keys.discard(key)
                elif op == "mouse_hold":
                    self.buttons.add(step["button"])
                    self.command("mousedown", step["button"])
                    self.pause(step["seconds"])
                    self.command("mouseup", step["button"])
                    self.buttons.discard(step["button"])
                elif op == "click":
                    self.command("click", step["button"])
                elif op == "look":
                    self.command("mousemove_relative", "--sync", "--", step["x"], step["y"])
                elif op == "point":
                    self.command("mousemove", "--window", self.client["window"], step["x"], step["y"])
                elif op == "wait":
                    self.pause(step["seconds"])
                elif op == "command":
                    self.command("key", "--", self.chat_key)
                    self.pause(0.15)
                    # '--' prevents command text from being parsed as an xdotool option.
                    self.command("type", "--clearmodifiers", "--delay", 5, "--", step["text"])
                    self.command("key", "--", "Return")
                    self.pause(0.15)
        finally:
            # Cleanup has its own per-command timeout even when the action budget expired.
            self.action_deadline = None
            self.release()

    def release(self):
        errors = []
        for key in list(self.keys):
            try:
                self.command("keyup", "--", key)
                self.keys.remove(key)
            except Exception as error:
                errors.append(str(error))
        for button in list(self.buttons):
            try:
                self.command("mouseup", button)
                self.buttons.remove(button)
            except Exception as error:
                errors.append(str(error))
        if errors:
            raise RuntimeError("Input cleanup failed; manually release keys on dedicated test display: " + "; ".join(errors))


class DryBackend:
    def __init__(self, client, stop, deadline, chat_key, max_action_seconds=10):
        self.client, self.stop, self.deadline = client, stop, deadline

    def verify_window(self):
        pass

    def perform(self, steps):
        # Deliberately does not sleep for UI holds or assert a real connection.
        if self.stop.is_set():
            raise InterruptedError("Workload stopped")

    def release(self):
        pass


def process_sample(pid):
    """Linux /proc readings identify generator/client PIDs, never server metrics."""
    try:
        status = Path(f"/proc/{pid}/status").read_text()
        memory = {}
        for line in status.splitlines():
            if line.startswith(("VmRSS:", "VmSize:")):
                key, value, _ = line.split()
                memory[key[:-1] + "Bytes"] = int(value) * 1024
        content = Path(f"/proc/{pid}/stat").read_text()
        fields = content[content.rfind(")") + 2:].split()
        ticks = os.sysconf("SC_CLK_TCK")
        return {"pid": pid, "cpuSeconds": (int(fields[11]) + int(fields[12])) / ticks, **memory}
    except (OSError, ValueError, IndexError):
        return {"pid": pid, "unavailable": True}


def make_plan(config, scenario, clients, seed, duration, run_id):
    weighted = actions_for(config, scenario)
    return {
        "schemaVersion": 1, "tool": "Newfie's VS Stress Tester", "toolVersion": VERSION,
        "runId": run_id, "scenario": scenario, "seed": seed, "durationSeconds": duration,
        "server": config["server"], "configuredClients": clients,
        "clientIds": [c["id"] for c in config["clients"][:clients]],
        "populationTargets": POPULATION_TARGETS, "maximumAuthenticClientsTestedInThisDelivery": 0,
        "workloadSchedule": {c["id"]: [next_choice for _, next_choice in zip(range(10), seeded_choices(seed, c["id"], weighted))]
                             for c in config["clients"][:clients]},
        "successRequires": "Trusted server evidence plus gameplay/persistence correctness checks; input events prove attempts only",
    }


def run_workload(config, scenario, clients, seed, duration, run_id, output, dry_run=False, authorized=False):
    if not dry_run and not authorized:
        raise ConfigError("Real runs require --authorized-test-server and a dedicated server you own or have permission to test")
    if not dry_run and not config.get("serverEvidenceFile"):
        raise ConfigError("Real runs require serverEvidenceFile; UI automation does not establish connection evidence")
    weighted = actions_for(config, scenario)
    selected = config["clients"][:clients]
    if len(selected) != clients or clients > config["limits"]["maxClients"]:
        raise ConfigError("Requested population exceeds configured client windows or maxClients")
    output = Path(output)
    output.mkdir(parents=True, exist_ok=False)
    atomic_json(output / "plan.json", make_plan(config, scenario, clients, seed, duration, run_id))
    sink = EventSink(output / "events.jsonl", config["limits"]["maxEvents"])
    resources = EventSink(output / "generator-resources.jsonl", config["limits"]["maxEvents"])
    stop, lock = threading.Event(), threading.Lock()
    started_wall, started = utc_now(), time.monotonic()
    deadline = started + config["limits"]["startupTimeoutSeconds"] + len(selected) * config["limits"]["maxActionSeconds"]
    backend_type = DryBackend if dry_run else X11Backend
    backends = {c["id"]: backend_type(c, stop, deadline, config["bindings"]["chat"], config["limits"]["maxActionSeconds"]) for c in selected}
    evidence = None if dry_run else Evidence(config["serverEvidenceFile"], run_id, [c["id"] for c in selected], config["limits"])
    attempted = failed = connection_failures = reconnect_attempts = 0
    pending = {}
    errors, cancelled = [], False
    actual_start = None
    workload_started_utc = None
    original_handler = None
    if threading.current_thread() is threading.main_thread():
        original_handler = signal.getsignal(signal.SIGTERM)
        def terminate(_signal, _frame):
            raise KeyboardInterrupt()
        signal.signal(signal.SIGTERM, terminate)

    def execute(client, action_name, iteration):
        nonlocal attempted, failed, connection_failures, reconnect_attempts
        action_id = f"{run_id}:{client['id']}:{iteration}"
        if evidence is not None and not evidence.is_online(client["id"]):
            with lock:
                connection_failures += 1
            sink.emit(kind="connection_unavailable", clientId=client["id"], actionId=action_id, action=action_name)
            return
        with lock:
            attempted += 1
            reconnect_attempts += action_name == "reconnect"
            pending[action_id] = client["id"]
        if evidence is not None:
            evidence.expect(action_id, client["id"], action_name)
        sink.emit(kind="action_attempt", clientId=client["id"], actionId=action_id, action=action_name,
                  measurementKind="simulated_tooling_only" if dry_run else "official_client_workload")
        try:
            backends[client["id"]].perform(config["macros"][action_name])
            sink.emit(kind="input_dispatched", clientId=client["id"], actionId=action_id, action=action_name,
                      gameplaySuccess=None)
        except InterruptedError:
            sink.emit(kind="action_interrupted", clientId=client["id"], actionId=action_id, action=action_name)
        except Exception as error:
            with lock:
                failed += 1
                errors.append(str(error))
            sink.emit(kind="input_failure", clientId=client["id"], actionId=action_id, action=action_name, detail=str(error)[:500])
            stop.set()

    def worker(group):
        choices = {c["id"]: seeded_choices(seed, c["id"], weighted) for c in group}
        iterations = {c["id"]: 0 for c in group}
        while not stop.is_set() and time.monotonic() < deadline:
            round_start = time.monotonic()
            for client in group:
                if stop.is_set() or time.monotonic() >= deadline:
                    break
                iteration = iterations[client["id"]]
                iterations[client["id"]] += 1
                execute(client, next(choices[client["id"]]), iteration)
            stop.wait(max(0, config["limits"]["actionIntervalSeconds"] - (time.monotonic() - round_start)))

    try:
        for client in selected:
            backends[client["id"]].verify_window()
        if evidence is not None:
            # Optional connection macros operate only the already-authenticated official UI.
            for client in selected:
                if client.get("connectOnStart", False):
                    sink.emit(kind="connect_ui_attempt", clientId=client["id"])
                    backends[client["id"]].perform(config["macros"][client.get("connectMacro", "connect")])
            startup_deadline = time.monotonic() + config["limits"]["startupTimeoutSeconds"]
            while time.monotonic() < startup_deadline:
                evidence.poll()
                if evidence.error:
                    raise RuntimeError(evidence.error)
                if evidence.all_online():
                    break
                time.sleep(0.1)
            if not evidence.all_online():
                raise RuntimeError("Not all configured PlayerUIDs have fresh trusted server connection evidence; no workload started")
        if evidence is not None:
            evidence.begin_workload()
        workload_started_utc = utc_now()
        actual_start = time.monotonic()
        deadline = actual_start + duration
        for backend in backends.values():
            backend.deadline = deadline
        sink.emit(kind="workload_started", runId=run_id, scenario=scenario, actualConnected=0 if dry_run else len(evidence.online))
        groups = {}
        for client in selected:
            groups.setdefault(client["display"], []).append(client)
        # Groups on a shared display run serially. Separate displays can run in parallel.
        # Reject excess groups rather than silently queuing whole client populations.
        if len(groups) > config["limits"]["maxParallelDisplays"]:
            raise ConfigError("Display groups exceed maxParallelDisplays; reduce population or explicitly raise the bound")
        with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, len(groups))) as pool:
            futures = [pool.submit(worker, group) for group in groups.values()]
            next_sample = 0
            try:
                while any(not future.done() for future in futures):
                    for future in futures:
                        if future.done() and future.exception() is not None:
                            raise future.exception()
                    if evidence is not None:
                        evidence.poll()
                        if evidence.error:
                            raise RuntimeError(evidence.error)
                    if time.monotonic() >= next_sample:
                        samples = [dict(role="generator", **process_sample(os.getpid()))]
                        samples.extend(dict(role="official_client", clientId=c["id"], **process_sample(c["processId"]))
                                       for c in selected if c.get("processId") is not None)
                        resources.emit(kind="load_generator_resources", processes=samples,
                                       note="These are load-generator/client processes, not server measurements")
                        next_sample = time.monotonic() + config["limits"]["resourceIntervalSeconds"]
                    stop.wait(0.05)
                for future in futures:
                    future.result()
            finally:
                stop.set()
    except KeyboardInterrupt:
        cancelled = True
        stop.set()
    except Exception as error:
        errors.append(str(error))
        stop.set()
    finally:
        stop.set()
        for backend in backends.values():
            try:
                backend.release()
            except Exception as error:
                errors.append(str(error))
        if evidence is not None:
            evidence.poll()
            if evidence.error and evidence.error not in errors:
                errors.append(evidence.error)
        if original_handler is not None:
            signal.signal(signal.SIGTERM, original_handler)
        confirmed = 0 if evidence is None else evidence.ack_count
        server_failed = 0 if evidence is None else evidence.failure_count
        elapsed = 0 if actual_start is None else max(0, time.monotonic() - actual_start)
        status = "cancelled" if cancelled else "failed" if errors else "tooling_simulation" if dry_run else "awaiting_gameplay_validation"
        report = {
            "schemaVersion": 1, "tool": "Newfie's VS Stress Tester", "toolVersion": VERSION,
            "measurementKind": "simulated_tooling_only" if dry_run else "official_client_workload",
            "runId": run_id, "scenario": scenario, "seed": seed, "startedUtc": started_wall, "completedUtc": utc_now(),
            "workloadStartedUtc": workload_started_utc, "workloadCompletedUtc": utc_now() if actual_start is not None else None,
            "configuredClients": clients, "actualConnectedPeak": 0 if evidence is None else evidence.peak,
            "actualConnectedMinimum": 0 if evidence is None or evidence.workload_minimum is None else evidence.workload_minimum,
            "authenticatedSessionEvidence": evidence is not None and evidence.workload_minimum == clients
                                            and evidence.authenticated_throughout and evidence.error is None,
            "maximumAuthenticClientsTestedInThisDelivery": 0, "requestedDurationSeconds": duration,
            "workloadDurationSeconds": elapsed, "actionsAttempted": attempted, "actionsConfirmed": confirmed,
            "workCompletedByType": {} if evidence is None else dict(evidence.work_completed_by_type),
            "actionsFailed": failed + server_failed, "interactionFailures": failed + server_failed,
            "pendingUnconfirmed": max(0, attempted - confirmed - server_failed),
            "connectionFailures": connection_failures, "disconnects": 0 if evidence is None else evidence.disconnects,
            "reconnectAttempts": reconnect_attempts, "errors": errors, "status": status,
            "connectionEvidence": {"source": "none" if dry_run else "trusted_local_server_jsonl",
                                   "heartbeatSeen": False if evidence is None else evidence.heartbeat_seen,
                                   "invalidEvents": 0 if evidence is None else evidence.invalid,
                                   "note": "Presence proves a connected session, not correct inventory, combat or world outcomes"},
            "correctnessChecks": {},
            "validationRequired": ["world_save_reload", "entity_and_block_persistence", "inventory_and_container_transactions",
                                   "claims_and_permissions", "combat", "movement", "anticheat", "oreshield_if_enabled"],
            "generatorResourcesFile": "generator-resources.jsonl", "actionEventsFile": "events.jsonl",
            "scalingLimitations": "One focus owner per display; groups share input time. Official clients require GPU/RAM and licensed accounts.",
        }
        atomic_json(output / "report.json", report)
        if evidence is not None:
            atomic_json(output / "action-acknowledgements.json", evidence.acks)
        sink.close()
        resources.close()
    return report


def parser():
    root = argparse.ArgumentParser(description=__doc__)
    root.add_argument("command", choices=["validate", "plan", "run"])
    root.add_argument("--config", required=True, type=Path)
    root.add_argument("--scenario", choices=sorted(SCENARIOS), default="idle")
    root.add_argument("--clients", type=int, default=1)
    root.add_argument("--seed", type=int, default=17227)
    root.add_argument("--duration", type=float, default=60)
    root.add_argument("--run-id", default=None)
    root.add_argument("--output", type=Path)
    root.add_argument("--dry-run", action="store_true")
    root.add_argument("--authorized-test-server", action="store_true")
    return root


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        config = load_config(args.config)
        if args.command == "validate":
            print(json.dumps({"valid": True, "configuredClients": len(config["clients"]), "macros": sorted(config["macros"])}))
            return 0
        run_id = args.run_id or "stress-" + uuid.uuid4().hex[:12]
        if not ID.fullmatch(run_id):
            raise ConfigError("run-id must be a simple identifier, maximum 100 characters")
        integer(args.clients, "clients", 1, min(len(config["clients"]), config["limits"]["maxClients"]))
        number(args.duration, "duration", 0.05, config["limits"]["maxDurationSeconds"])
        if args.command == "plan":
            result = make_plan(config, args.scenario, args.clients, args.seed, args.duration, run_id)
            if args.output:
                atomic_json(args.output, result)
            else:
                print(json.dumps(result, indent=2))
            return 0
        if args.output is None:
            raise ConfigError("run requires a new --output directory")
        report = run_workload(config, args.scenario, args.clients, args.seed, args.duration,
                              run_id, args.output, args.dry_run, args.authorized_test_server)
        print(json.dumps({"report": str(args.output / "report.json"), "status": report["status"],
                          "actualConnectedPeak": report["actualConnectedPeak"], "actionsAttempted": report["actionsAttempted"]}))
        return 2 if report["errors"] or report["status"] == "cancelled" else 0
    except (ConfigError, ValueError, OSError) as error:
        print("Stress tester: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
