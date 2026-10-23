# Suite 1.2.48 — timeline and web reads

Build: `ms11-timeline-web1`. NOS remains 0.9.0 preview; companion mods are unchanged. Animal generation overrides and the compact population rule editor belong to 1.2.48, following the published 1.2.47 release. The earlier animal validation report retains its historical build label.

## Changes and evidence

The previous timeline treated displayed intervals above 2.5 seconds as missing observations. Downsampling 30-minute and one-hour history can itself produce 3–6 second displayed intervals, so valid traces could disappear. The server now identifies gaps in raw observations before downsampling. Isolated observations render as dots; actual gaps remain breaks with gray shading. Hidden panels, container resizing and returning to the tab trigger redraws. Late responses cannot replace a newer range selection.

A top legend explains blue metrics, orange/red lag, cyan world saves, purple mod changes, green entity scans and gray missing observations. Official save events do not identify whether a save was scheduled or manual: the label is **World save / autosave**. A save marker records an observed callback window, not complete disk-flush time or proof that the save caused a stall.

Timeline reads project six metric fields without Smart Diagnosis or game API lookups. The five supported range values bound cache variants. Page and timeline responses reuse capped serialized snapshots. Automatic page reads reuse the last manual Harmony inspection; they no longer walk patched methods. A busy/failed page request does not fan out into compatibility reads.

Web elapsed and queue-wait counters remain available but are clearly separated from execution intervals. Incident evidence clips callback work to the overlapping interval and conservatively discounts coincident GC. Genuine measured web work remains visible; these changes do not make the panel immune to contributing load.

## Validation

- Release build against official Vintage Story 1.22.7: zero warnings/errors.
- Browser checks: longer/downsampled ranges, real gaps, absent values, step player counts, save markers/legend, hidden-panel recovery, resize, out-of-order responses, live overview refresh, busy-response behavior and timing presentation.
- Pure web checks: request coalescing, serialization/cache boundaries, failure handling and partial-overlap/GC evidence. A fixed 1,000-read serialization fixture allocated 0 bytes for warm reused responses versus 25,040,000 bytes for repeated serialization; this is not whole-request allocation.
- Compiled runtime checks: archived attribution, compact header and six-field timeline, raw gap detection, retained save observations and manual-inspection placeholder without game API access.
- Animal regression: 27 compiled protection/precedence/persistence checks plus browser create/edit/search/scroll/mobile checks.
- Private copied-world server: 21 integration checks with 25 mod ZIPs / 28 loaded mod records, including an actual completed save producing a marker and twelve warm parallel page reads sharing one snapshot generation. Warm recorded page reads took approximately 1–5 ms; the initial page read took 123 ms in this local fixture. Response size was 17,426 bytes with its short retained history.

The integration report is [timeline-web-1.2.48.json](validation/timeline-web-1.2.48.json). It includes selected mods, checks and safe request measurements; no credentials or request bodies.

These are functional/local workload results, not production TPS, memory or zero-lag guarantees. Real sampling gaps cannot be reconstructed. Long-session production behavior should be compared with the same player workload and profiler settings.
