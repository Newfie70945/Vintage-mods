# Suite 1.2.53 / NOS 0.20.17-preview — panel, map memory and dropped items

Suite build: `ms11-panel-map-clumping1`. Existing companion versions retain their behavior; all seven packages receive the owner-supplied static feature-card icons. Animated GIFs remain separate upload assets. MOTD implementation and sailing sources were not changed.

## User-facing changes

- Sidebar lights show a neutral waiting/unavailable state instead of disappearing. Failed static checks retry on the existing background cadence; successful static checks refresh after five minutes. Cached status survives indicator initialization. Requests remain staggered.
- JSON read requests have a 30-second client timeout so a stalled read cannot permanently occupy a GET slot. Caller cancellation remains effective. Writes retain their existing behavior.
- NOS controls gain a dark teal header, actual-state summaries that filter module rows, pending-edit count, clearer groups and collapsed execution activity. Configured switches and actual patch activation remain separate. Draft preservation, failed saves, read-only roles, Safe Mode, capture locks and older NOS compatibility are retained.
- Optional NOS dropped-item clumping is exposed in the same controls. It is off by default. See `modules/optimizer/docs/dropped-items-0.20.17.md` for limits, ownership/attribute rules and age behavior.
- Removed the OreShield Beta banner without changing protection defaults or mechanics.
- Added the requested personal-server recovery note and contact information to Block ID Migration, preserving experimental/backup notices.

## Webmap export performance and memory

- PNG output streams through 64 KiB IDAT chunks instead of retaining a complete compressed tile per worker. The pooled decoder handles both legacy single-IDAT tiles and streamed tiles, compacting payloads within the same rented buffer. Pixel output and map cache format remain compatible.
- Every export path uses its own releasable byte-buffer pool. Cached buffers are capped at 16 MiB per export; disposal drops all cached references, including on failure and cancellation. Large export buffers no longer populate the process-wide shared pool. Active working buffers can exceed the cache cap while workers are rendering.
- Forced live exports now run the same palette/buffer cleanup as startup and resident-only jobs. Memory diagnostics report released cache bytes. This does not force collections, compact the process heap or trim game-owned pools.
- Direct-save-database mode no longer falls back to loading world terrain. Only resident dirty columns use current in-memory terrain; unloaded dirty edits remain queued. A failed saved-column read preserves the published cache/journal. Explicit legacy world-loader mode remains available and can still increase loaded terrain/memory.
- The panel distinguishes columns read from actual world-loader requests, and exposes export buffer cleanup. OS/container RAM need not fall immediately when references are released.

## Validation

- All 38 NOS test suites passed, including 169 engine lifecycle/compatibility checks and 29 dropped-item checks. A 3,000-drop native-slot fixture conserves every item and consolidates eligible drops near native stack limits.
- Browser checks cover sidebar initial/retry/recovery states, read timeout/cancellation, unchanged write semantics, NOS module filtering/drafts/permissions and desktop/mobile layouts, compact header requests and the existing packet page.
- PNG round trips preserve every pixel across multiple bounded IDAT chunks. Warm managed allocation for a 1 MiB incompressible PNG-write fixture measured 7,944 bytes; this excludes the preallocated input, native compression memory and decoder allocation. It is not whole-export memory or production TPS evidence.
- Map-memory fixtures verify 80 detached column reads / 200 chunk disposals, unchanged save bytes, skipped entity/mod payloads, bounded/released pools, parallel export, old-cache reuse, resident dirty updates, concurrent edits and preservation on decoder failure. Manual direct updates defer unloaded dirty work and make zero world-loader requests.
- Final official-server integration results and exact artifact hashes accompany the compiled release bundle. The test server has no authenticated gameplay clients; this is not a full-modpack player-workload benchmark.
