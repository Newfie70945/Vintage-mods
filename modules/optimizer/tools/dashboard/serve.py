#!/usr/bin/env python3
"""Loopback-only, read-only dashboard for local NOS benchmark/profile evidence."""
from __future__ import annotations

import argparse
import json
import mimetypes
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

ASSETS = Path(__file__).resolve().parent
MAX_REPORT_BYTES = 64 * 1024 * 1024


def make_handler(report_path, minimum_interval=2):
    # Exactly one selected file is readable; URLs are never converted into disk paths.
    report_path = Path(report_path).resolve()
    lock = threading.Lock()
    cache = {"loadedAt": -float("inf"), "data": None, "error": None, "mtime": None}

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            # Reject DNS-rebinding requests even though the listener is loopback-only.
            host = self.headers.get("Host", "").lower()
            allowed_hosts = {f"127.0.0.1:{self.server.server_port}", f"localhost:{self.server.server_port}"}
            if host not in allowed_hosts:
                self.respond(403, b'{"error":"local Host required"}', "application/json")
                return
            path = urlsplit(self.path).path
            if path in ("/", "/app.js", "/style.css"):
                asset = ASSETS / ({"/": "index.html", "/app.js": "app.js", "/style.css": "style.css"}[path])
                self.respond(200, asset.read_bytes(), mimetypes.guess_type(asset.name)[0] or "application/octet-stream")
                return
            if path != "/api/report":
                self.respond(404, b'{"error":"not found"}', "application/json")
                return
            with lock:
                now = time.monotonic()
                if now - cache["loadedAt"] >= minimum_interval:
                    cache["loadedAt"] = now
                    try:
                        if report_path.stat().st_size > MAX_REPORT_BYTES:
                            raise ValueError("Selected report exceeds 64 MiB")
                        data = report_path.read_bytes()
                        value = json.loads(data)
                        if not isinstance(value, dict) or value.get("kind") not in ("newfies-optimizer-benchmark", "newfies-optimizer-profile"):
                            raise ValueError("Unsupported report kind")
                        cache.update(data=data, error=None, mtime=report_path.stat().st_mtime)
                    except (OSError, ValueError) as error:
                        cache.update(data=None, error=type(error).__name__ + ": selected evidence unavailable or invalid")
                data = cache["data"]
                error = cache["error"]
            if error:
                self.respond(503, json.dumps({"error": error}).encode(), "application/json")
            else:
                self.respond(200, data, "application/json")

        def respond(self, status, data, content_type):
            self.send_response(status)
            self.send_header("Content-Type", content_type + "; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Referrer-Policy", "no-referrer")
            self.send_header("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'")
            self.end_headers()
            self.wfile.write(data)

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--poll-seconds", type=float, default=2)
    args = parser.parse_args()
    if not 1 <= args.port <= 65535 or not 2 <= args.poll_seconds <= 3600:
        parser.error("port must be 1–65535; polling must be 2–3600 seconds")
    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(args.report, args.poll_seconds))
    server.daemon_threads = True
    print(f"Read-only dashboard: http://127.0.0.1:{args.port}/", flush=True)
    try:
        server.serve_forever(poll_interval=.5)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
