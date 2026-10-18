# Current release changes

## Server Essentials Suite 1.2.52

- Added `/rollback block 2h` for the looked-at block/container, plus absolute coordinate and dimension targets for missing blocks. Includes logged container inventory automatically, previews concrete slot changes and uses the existing execution token, receipts and live-state checks. Neighboring cells are excluded in both SQL and live validation; no restart is needed for loaded compatible locations.
- Fixed unsafe rollback chat output: help placeholders, logged values and errors are escaped before chat rendering. Responses are bounded, malformed controls/Unicode are removed, and failed private sends cannot leave recovery stuck. The official game VTML parser reproduced the old markup mismatch and accepts the corrected replies followed by normal chat.

- Added a separate automatic inventory restoration workflow with a preview and explicit staging for the next restart, limited to one location and compatible retained vanilla container metadata.
- Retained mode reconnects the original bookshelf/shelf/chest block while preserving its complete saved inventory payload and item attributes byte for byte.
- Snapshot mode fills an empty compatible inventory from a selected BlockLogger snapshot, preserving other container data. Historical codes/counts produce default item attributes; missing written text, durability, freshness or bag contents cannot be reconstructed.
- Refuses occupied destinations, missing metadata/items, wrong worlds/coordinates, incompatible classes/layouts, changed saved data, changed snapshots and later logged block/item mutations, including legacy overworld dimension records. Bookshelf shape/material and usable slots are checked.
- Durable per-location restore receipts prevent replaying old snapshots or uncertain attempts. One generic repair or inventory restore can be staged at a time. Backup remains enabled by default and can be disabled only with the existing manual-copy acknowledgement.
- Added Owner webpanel and `restorepreview` / `restorestage` command controls. Safe Diagnostics Mode blocks restoration. Existing generic block-entity protection remains in place.
- Suite advances to 1.2.52; NOS and companion mods are unchanged.
