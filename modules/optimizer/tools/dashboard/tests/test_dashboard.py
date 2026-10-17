import importlib.util
import json
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from pathlib import Path

spec = importlib.util.spec_from_file_location("nos_dashboard", Path(__file__).resolve().parents[1] / "serve.py")
serve = importlib.util.module_from_spec(spec)
spec.loader.exec_module(serve)


class DashboardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.report = Path(self.temp.name) / "report.json"
        self.report.write_text(json.dumps({"kind": "newfies-optimizer-benchmark", "trials": []}))
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), serve.make_handler(self.report, .1))
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.url = f"http://127.0.0.1:{self.server.server_port}"

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(2)
        self.temp.cleanup()

    def test_selected_file_and_static_assets(self):
        with urllib.request.urlopen(self.url + "/api/report") as response:
            self.assertEqual(json.load(response)["trials"], [])
            self.assertIn("default-src 'self'", response.headers["Content-Security-Policy"])
        for path in ("/", "/app.js", "/style.css"):
            with urllib.request.urlopen(self.url + path) as response:
                self.assertGreater(len(response.read()), 20)

    def test_traversal_and_arbitrary_files_never_served(self):
        for path in ("/../report.json", "/%2e%2e/report.json", "/api/report/../../etc/passwd", "/report.json"):
            with self.assertRaises(urllib.error.HTTPError) as error:
                urllib.request.urlopen(self.url + path)
            self.assertEqual(error.exception.code, 404)

    def test_invalid_report_is_explicit_unavailable(self):
        self.report.write_text("not json")
        with self.assertRaises(urllib.error.HTTPError) as error:
            urllib.request.urlopen(self.url + "/api/report")
        self.assertEqual(error.exception.code, 503)

    def test_foreign_host_is_rejected(self):
        request = urllib.request.Request(self.url + "/api/report", headers={"Host": "hostile.example"})
        with self.assertRaises(urllib.error.HTTPError) as error:
            urllib.request.urlopen(request)
        self.assertEqual(error.exception.code, 403)


if __name__ == "__main__":
    unittest.main()
