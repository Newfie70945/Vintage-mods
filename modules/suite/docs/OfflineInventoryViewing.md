# Read-only offline inventories

The web panel's Player Administration profile and Anti-Cheat inventory drawer show saved inventories for offline players. Existing panel authentication and ReadOnly account restrictions apply. The offline view contains item names/codes, quantities, slot positions, durability and temperature where the saved format supports them. Its item editing, give, snapshot creation and restore controls are unavailable. All server mutation methods still require an online player; bypassing the browser controls cannot edit an offline inventory.

Each view includes its source, capture time when known, read time, and a warning when decoding is incomplete. The sources are:

- `live`: current server inventory. Existing online editing and conflict fingerprints continue to work.
- `disconnect-capture`: player-owned slot metadata captured immediately before disconnect cleanup.
- `server-save-capture`: player-owned metadata captured at the latest server world save or service shutdown.
- `world-save`: the existing world's saved player inventory, used when this release has no capture. Vintage Story does not record its per-player save timestamp; the panel shows the actual read time and states that the capture time is unknown. Player Last Seen is never substituted for capture time.
- `snapshot`: the newest retained historical inventory snapshot when native data is unavailable. It contains occupied slots only and may predate the last session.

The latest captured metadata is persisted separately in `essentials.db`, one record per player. Captures do not consume or prune rollback snapshot history. Metadata captures intentionally do not persist full serialized item attribute blobs and do not enable offline Shift+L stack editing/view reconstruction. Existing in-game inventory viewing remains online-only.

The native save reader uses its own SQLite read-only connection on a worker and reads only the inventory protobuf field. It does not load player entities, register live inventories, or write to the game save. Standard backpack contents are decoded into detached inventories. Unsupported layouts, missing items and decoding failures are labeled incomplete instead of silently reporting a complete empty inventory.

Requests share a 30-second saved-data cache capped at 64 entries and 32 MiB of retained inventory bytes. Detached decoded views have a separate limit of 32 players and 16,384 slots, and hold weak references to raw data so they do not defeat the raw byte limit. Per-record reads are capped at 16 MiB, 64 inventories and 8,192 decoded slots, with bag slot counts checked before expansion. Reconnects and new captures invalidate cached evidence. Database writes are handled by the existing background persistence worker.

Inventory-only browser refreshes use `/api/admin/inventory`, avoiding a full profile and BlockLogger history query. Player-list requests coalesce while one is in flight; late inventory responses for a previously selected player are discarded.

Validation: the fixture tests exercise protobuf field isolation, missing/corrupt/oversized data, UID parameter isolation, unchanged database bytes, concurrent request sharing, cache invalidation and aggregate cache eviction. Browser-function tests exercise live/ReadOnly/offline access, source labels, visible saved slots, suppression of offline mutation calls and late-response isolation. Validate in a staging game server with ordinary and modded bags before production use; game-world behavior was not exercised by these fixture tests.
