# Current release changes

## Server Essentials Suite 1.2.51

- Added Owner-only, read-only saved block and inventory inspection by absolute block coordinates/dimension or exact saved chunk key. Runs in the background without generating/loading terrain or executing mod block-entity/attribute code.
- Reports retained classes, original block codes, current block mismatches, inventory paths, serialized stack IDs/quantities and payload hashes. Unknown formats and inspection limits are clearly marked; this does not restore inventories or invent lost items.
- Added searchable registered replacement blocks. Inventory-bearing targets are labelled and disabled for generic remapping.
- Added BlockLogger replacement suggestions using bounded, indexed history at an exact world/dimension/location, an optional incident cutoff, confidence and source record IDs. Recent placements outrank older activity; removals, conflicts and capped history are downgraded. Suggestions only fill the target and require a fresh preview.
- Shows up to three historical container snapshots with slot codes/quantities and validity checks. Existing snapshots do not contain full item attributes; later transfers must be reviewed before recovery. No automatic inventory restoration is attempted.
- Added downloadable JSON diagnostic reports with scan, plan and retained-data evidence. Inventory summaries are omitted from non-Owner web responses.
- Bounded saved attribute parsing by size, depth, node/array/string count and output limits. Generic repair uses the same non-executing parser for source block-entity checks and refuses unverifiable data.
- Read-only inspection/search remain available in Safe Diagnostics Mode; world-changing repair controls remain locked. Default/optional backup behavior is unchanged.
- Suite version advanced to 1.2.51. NOS and companion mods are unchanged.
