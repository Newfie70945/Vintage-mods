# Local performance evidence dashboard

```sh
python3 tools/dashboard/serve.py --report /absolute/path/report.json --port 8765
```

Open `http://127.0.0.1:8765`. Supports profiler JSON and controlled benchmark JSON. This separate process is read-only, binds loopback, accepts only localhost/127.0.0.1 Host headers, serves only explicitly allowed URLs, and rate-limits report reads. No dependency installation is required. Do not expose it as a public unauthenticated service.

Charts support trial/metric selection, range controls, hover sample values and JSON/CSV exports. All content is rendered as text; untrusted report strings are not interpreted as HTML. Campaign histories are downsampled to 128 display samples per trial; full retained histories remain in each `trial.json` and profiler export. Missing values are labelled unavailable, not zero. Synthetic previews are prominently marked.

Run server/path/content-policy tests from the repository root:

```sh
python3 -m unittest discover -s tools/dashboard/tests -v
```

Full methodology: [`../../docs/benchmark-methodology.md`](../../docs/benchmark-methodology.md).
