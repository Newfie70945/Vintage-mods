# Live terrain updates — Suite 1.2.52

Open **Web Map → Export Schedule → Live terrain updates**, enable the checkbox, set the interval/tile limit, then **Save Export Schedule**. Defaults remain **off**, **120 seconds**, **4 tiles per pass**. An initial compatible terrain export is required.

Automatic passes alternate:

- **Loaded builds:** patch dirty areas already loaded by the game, using their current surface blocks. These changes do not have to wait for a world save. Unloaded or partially loaded columns stay queued; no load/generation request or keep-loaded flag is issued.
- **Saved exploration:** publish newly explored terrain once the normal world save contains it, using the detached read-only save reader. This pass does not consume unsaved build edits. If the direct reader is disabled, only loaded-build passes run.

The interval is **between passes**, so with both modes active each kind normally runs every other interval. Large backlogs take several passes. Maximum 1–16 tiles per pass, interval 30–3,600 seconds. These settings trade update speed against map CPU/disk work.

## What counts as dirty

The map now records overworld player placement and break events independently of BlockLogger recording/database availability. Existing explicit dirty marks, such as rollback changes, also participate. Recording starts while the map service is enabled; changes made while it was off cannot be reconstructed from this journal. Background changes from other mods that do not emit these events or explicitly mark the map journal may require a manual update.

The map shows surfaces. Underground interior changes usually do not change the visible top-down image.

## Safety and work limits

The loaded pass never opens the world save or calls a chunk loader. It patches existing raw map tiles, updates their image/overview/zoom levels and preserves unrelated images. No full rebuild is automatically substituted for a missing/incompatible cache.

Candidate checks rotate through a bounded look-ahead (up to 2,048 dirty entries), checking loaded state in small main-thread batches. Surface capture yields between callbacks, with a 2 ms soft budget and 2,048 probe steps per slice. A single native lookup/unpack can exceed a soft budget. Chunk references are released after each slice; only small pixel/height buffers survive between slices. No terrain is pinned.

If a column unloads partway through capture, its previous pixels stay published and its dirty entry remains queued. Edits made while an export is running remain dirty even if an older capture is published. The tile cap applies to loaded-build passes too. Concurrent exports are coalesced by the existing single-export gate.

Progress distinguishes `resident-only` reads from saved terrain; main-thread surface-read time appears under **Web Map resident surface read** in Suite Timing Hotspots. PNG encoding, overview and zoom-level work run on the export worker, but still consume CPU/disk resources.

This does not change the behavior of separately requested manual rebuilds. Other mods/versions are unchanged.
