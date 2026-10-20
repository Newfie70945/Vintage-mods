# Suite 1.2.44 — Migration UI 2

Build: `ms11-migrationui2`. NOS and companion versions are unchanged.

## Problem and resulting workflow

The previous page separated rule inputs/save from backup and inventory acknowledgements, silently returned for invalid saves, and did not restore saved backup settings when loading/editing a rule. A user could see no outcome or retry with different backup options. The new compact editor keeps source, replacement, inventory, backup choice and Save & enable together. The primary editor and saved rules cannot be hidden by saved workspace-card collapse preferences.

New webpanel rules start with automatic backup off and require the manual-world-copy acknowledgement. Existing configurations restore their actual stored policy, including automatic backup if previously selected. Enter submits the form. Missing input, acknowledgement failures and server/API errors appear beside the action; unsuccessful writes retain draft fields. A successful write followed by a failed status refresh is explicitly reported as saved, and the returned saved state prevents stale backup options from replacing it. Search accepts a partial replacement code and uses the existing source-aware server validation. Inventory handling applies to the entire saved rule set, as stated in the editor.

Saved rules remain visible and editable, with labelled mobile rows. Optional terrain scanning, batch review and diagnostics are collapsed. Batch review still requires a completed clean scan and pausing the current migration; it replaces the saved rule set. Individual rules can be saved while a scan is running. Pause stops future loads; resume/retry uses the saved backup/inventory policy, not unsaved form changes. The server revalidates saved mappings before resuming and uses the existing preparation/backup/activation path.

No chunk codec, replacement policy, inventory evidence requirements or recovery algorithm was relaxed. The in-game `replaceheld` command still requests automatic backup; the page says to use the web editor for manual-backup rules. Already loaded chunks must reload, and normal saves persist changes. Migration remains experimental and does not undo previously saved edits when paused.

## Verification

- Built Suite against the inspected official Vintage Story 1.22.7 / .NET 10: zero warnings and errors.
- Chromium page checks exercise empty/invalid saves, manual-copy acknowledgement, API failure visibility, draft preservation, Enter submission, source-aware replacement search, refresh/edit policy restoration, saved-rule resume, no-scan save during a scan, batch review invalidation, inventory preparation, roles, Safe Mode, untrusted text and mobile overflow. The primary editor is not collapsible; preserve mode hides the recovery cutoff.
- Existing optimizer, automatic-profiler/updater-control and performance-timeline browser regressions passed.
- Block repair checks cover official serialization/preservation, recommendations, migration inventory payload/receipts and live backup integrity/concurrency/cancellation.
- The disposable official-server integration test covers normal load-time replacement, immediate save/activation without restart, ID-wide held rules during a scan, preservation of other rules and healthy blocks, pause/cancellation, explicit manual-backup web saves, resume with that policy, all 32768 solid cells after normal saving, SQLite integrity and disabled state across restart.
- Source/version consistency and whitespace checks passed. No production server or world was accessed.

Browser API fixtures and the dedicated-server test are separate checks. No authenticated game client was used. Retained inventories and damaged production worlds may have missing/incompatible evidence; this UI pass does not promise that every bookshelf or item can be recovered. Preview images use synthetic test IDs and replacement results.
