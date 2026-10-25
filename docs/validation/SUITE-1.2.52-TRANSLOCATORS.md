# Suite 1.2.52 · ms11-translocator1

This replaces the translocator prototype withheld in the original Claude review. Public Mod DB was rechecked: Suite 1.2.51 is still the latest public version; the unreleased candidate remains 1.2.52. Other mod binaries are unchanged.

## Privacy and correctness

The public map shares community discoveries, including player-created/repaired pads. Observation of a loaded official `BlockEntityStaticTranslocator` creates a record; reading its target never creates another record. Both source and destination must have their own observation and repaired state before target coordinates are published. Public DTOs omit nullable unknown target fields entirely and contain neither private target IDs nor player UIDs/names. There are no fabricated use/discoverer counts.

Only the official block entity's `TargetLocation` is used, verified against official 1.22.7. There is no movement-jump, /tp, warp, respawn or guessed reverse-link inference, and no target-side world read. The client also refuses to draw a line or target popup without a matching repaired destination marker. The original Y-coordinate bug cannot occur because no departure pad is inferred from a destination position.

## Work and lifecycle bounds

Tracking is optional/off by default. A 250 ms listener gives one eligible player a turn, capped at 128 loaded-block probes and a 2 ms soft budget per turn. A single engine call can exceed a soft budget. Player cursors advance across turns and positions are finite-checked. The cursor cache is bounded to 256 records, removed on disconnect or disable; an advancing start offset avoids repeatedly scanning one initial slice when a larger population churns that cache. Selection, placement and interaction add explicit local observations, including creative placement; spectators and non-overworld dimensions are excluded.

No terrain generation or load requests. Discovery records are capped at 5,000. JSON loading, serialization and atomic file replacement happen on one background worker, coalesced over about a second. Short locked snapshots use immutable values; serialization/file work occurs outside the registry lock. The worker survives live toggles, avoiding competing writers; stop removes tick/event handlers. Shutdown allows a bounded two-second final flush. Files are world-hash scoped and validate schema/world identity. Legacy v1 inferred records are not imported. Errors are displayed in WebPanel status; observation failures are contained and log-throttled so this layer does not throw through a player's interaction callback.

The layer's data is not a world backup and does not modify game saves. Large maps still incur bounded map-response/browser-render work; no production TPS improvement is claimed.

## Controls

WebPanel → Web Map → Public layers → **Translocators**, configure discovery radius (3–16, default 6), then **Save Settings**. The public map has an independent overlay checkbox. Marker updates follow the normal cached metadata feed; no immediate full-world scan runs. Existing pads need observation after the feature is enabled. Pad repair and placement are observed while tracking is on.

## Validation

- Official 1.22.7 / .NET 10 Suite Release build: zero warnings/errors.
- 47 compiled runtime checks cover undiscovered coordinates absent from JSON, independently known endpoints, broken/repaired states, no invented reverse links, removal, world/schema separation, 5,000-record limit, bounded native API probes, two-player fairness, native pad observation, creation/repair, no target-area reads, unloaded chunks, failure isolation, stop/restart handlers and asynchronous persistence/reload.
- Chromium test covers overlay visibility, known-endpoint-only lines and popup, absence of fabricated counters, WebPanel toggle/radius roundtrip and read-only controls.
- Existing detached map-reader/export, incremental tile, player-refresh and Performance workspace regressions pass.
- Disposable official server checks cover the public map feed, rejection of direct private-file downloads, live off/on configuration, persisted radius/enable settings, Suite/NOS capture controls, clean shutdown and copied-world SQLite integrity.

Runtime fixtures use controlled players and intercepted world access. The disposable server has no authenticated game clients; these checks are not a production load benchmark.
