# Performance workspace and incremental exploration validation

Suite **1.2.51**, build **ms11-performance-map1**; official Vintage Story **1.22.7**, .NET 10. Optimizer and MOTD source are unchanged.

## Changes

- Current-page refresh now updates both Performance overview and timeline, with independent auto-refresh controls, interval selection, update age and failure status.
- Shared navigation, panel jumps, mobile layout and bounded scrolling tables across all six Performance pages. The timeline precedes the timing table.
- Background reads pause in hidden tabs, during field editing and under Safe Diagnostics. Health audits remain manual; NOS status refresh preserves drafts. Automatic reads do not start captures/scans or query block history.
- Opt-in saved-exploration map updates: 120-second default interval, four base tiles per pass, configurable limits, no concurrent export, no automatic world loading/generation/full-rebuild fallback.
- Changed-tile revisions preserve unrelated browser images and camera position; missed revisions invalidate safely.
- Automatic updates leave unsaved-edit journal entries, queued startup exports, lifecycle audit and entity-error logging intact. Palette preparation is divided into 256-block game-thread batches. Terrain files and manifest decoding complete on the export worker.

## Validation

- `python3 tools/check_sources.py`, `git diff --check`.
- Suite compilation/package against official 1.22.7: zero warnings/errors.
- Chromium: `test_performance_live_workspace.cjs`, `test_performance_timeline.cjs`, `test_performance_navigation.cjs`, `test_timeline_render_recovery.cjs`, `test_optimizer_controls.cjs`, `test_map_player_refresh.cjs`, `test_map_incremental_tiles.cjs`.
- Native `tests/runtime/webmap`: configuration bounds/clone/reload; public-player privacy matrix; capture cadence and viewer/coalescing behavior.
- Native `tests/runtime/map-memory`: 80 detached column reads, 200 chunk disposals; block/fluid preservation; skips entity/container/decor/mod metadata; private pool bounded/freed; save bytes unchanged. Runtime exploration publishes one tile per pass, clean pass zero, detects additional columns within existing tiles, retains unsaved edit journal, and preserves the published map on missing saved terrain. World-loading calls fail the test.
- Disposable official-server HTTP integration with `NSS_WEB_TEST_PERFORMANCE=1`: repeated overview/timeline reads at 1m/5m/30m, Memory/Tick/Profiler/NOS reads, owner-only map schedule persistence, header request/cache regression, clean shutdown, SQLite integrity `ok`.

These checks establish behavior and safety boundaries for the tested fixtures, not a production TPS or player-scaling improvement. Automatic terrain coverage discovery, tile rendering, overview and pyramid publication still use CPU/disk. Keep live terrain updates opt-in and conservative.

See [user guide](../../modules/suite/guides/PERFORMANCE-MAP1.md).
