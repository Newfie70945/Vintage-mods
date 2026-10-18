# Current release changes

## Server Essentials Suite 1.2.49

- Added Block ID Repair to the webpanel and `/nseblockrepair` commands.
- Scans existing saved chunks for missing or unregistered numeric IDs in solid, fluid and decor data, with exact occurrence counts, sample chunks, progress and cancellation.
- Previews old ID → replacement ID, including IDs without a usable block code. World ID and registry binding prevent applying a plan to a different mapping.
- Stages repair for the next startup, before terrain loads. Creates and verifies a full world backup before edits; preserves unrelated chunk fields and uses the official database writer.
- Refuses invalid targets, inappropriate layer changes, legacy/corrupt data and source blocks hosting block entities. Air removal and valid-source replacement need explicit options.
- Owner controls, CSRF, Safe Diagnostics restrictions, command privileges and audit records apply. Backups and last-job results remain available.
- NOS and companion mod versions are unchanged. The prior generation/save performance report and limitations remain in source.
