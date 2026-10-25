# Suite 1.2.52 — loaded-only dirty terrain updates

Version correction: the author confirmed this work remains part of Suite 1.2.52. This is the updated candidate, `ms11-loaded-map1`; companion and optimizer code is unchanged.

## Change

Live terrain updates alternate a new resident-only dirty pass with the existing saved-exploration pass. Defaults remain off, 120 seconds and four tiles. With both modes active each kind runs every other interval; disabling the direct save reader leaves resident-only passes available.

The new path patches existing raw cached tiles from currently resident solid/fluid surface blocks. It never opens the world-save database, invokes a loader, generates terrain, sets KeepLoaded or disposes native chunks. Missing sections defer the column. After a partial capture, missing sections leave old pixels intact. A completed capture is painted on the export worker, then existing overview/style/zoom publication updates the map.

Dirty candidates rotate, capped at 2,048 checks per pass. Eligibility checks run at most 64 at once with a 2 ms soft budget. Resident capture runs at most 2,048 probe steps with a 2 ms soft budget, yielding at least 25 ms between incomplete slices. One native operation can exceed a soft budget. Native chunk references are reacquired/released inside each main-thread slice; only numeric surface buffers live across yields. Main-thread work is separately timed in Suite Timing Hotspots.

Only successful published coordinates are acknowledged. New edits during an export remain queued through the journal's mutation/touch protection. The per-pass tile cap is preserved. Unloaded dirty areas, deferred tiles and areas without raw cached tiles remain queued.

Map placement/break tracking now belongs to the map lifecycle rather than BlockLogger's event/logging gates. It remains available if the database fails or logging is disabled, and stops when the map is disabled. Existing rollback dirty marking remains. This is an event-driven journal, not a full-world change detector; unsupported background mod edits and changes made while the map was disabled are not retroactively reconstructed.

## Additional bug fixed

The exporter wrote cache version 12 while the automatic-update service required version 13. This could reject its own fresh export. The service now uses the exporter's shared version constant; existing compatible v12 raw tiles need no rebuild solely for this fix. The service no longer requires a normal-style LOD directory when checking whether alternate-style/repairable caches can be updated. Cache compatibility is still checked by the exporter; unsupported caches never trigger an automatic full rebuild.

## Validation

- Suite builds against official Vintage Story 1.22.7 / .NET 10 with zero warnings/errors.
- Native map-memory fixture uses actual official ServerChunk data to publish an unsaved changed roof/material. Other tiles and save database bytes stay unchanged. A known unloaded dirty tile stays queued; no-work passes write zero tiles.
- Fixture changes the journal during capture and confirms the newer edit is retained. A later unchanged capture acknowledges only completed work.
- Fixture unloads a column between bounded surface slices and confirms no partial pixels are published or acknowledged, then permits a natural reload and verifies a successful retry.
- Native world-loader stubs throw/count any request; the count remains zero. Resident data remains alive and is not disposed by export. Captures work with the direct save reader disabled.
- Map event handlers journal placements without a logging subsystem and remain inert when map runtime is disabled. Cache version consistency is checked against the compiled service/exporter constants.
- Existing detached-reader, startup failure, per-chunk journal acknowledgement, incremental saved-exploration and unreadable-save fallback checks remain included.
- Browser and disposable-server integration results accompany the distribution. This is not a production load benchmark or a guarantee that one native unpack always fits the soft budget.
