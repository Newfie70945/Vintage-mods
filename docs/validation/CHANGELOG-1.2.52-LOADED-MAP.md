# Server Essentials Suite 1.2.52

Additional changes for the 1.2.52 release:

- Live terrain updates now refresh dirty, already-loaded areas so current builds can appear without waiting for a world save.
- Unloaded or partially loaded areas stay queued until naturally loaded. Automatic updates never request, generate or pin chunks.
- Added small, cooperative surface-read batches and preserved the per-pass tile limit.
- Kept newer edits queued when terrain changes during an export; abandoned partial captures retain the previous image.
- Alternated loaded-build updates with saved exploration so both continue progressing. Existing interval settings apply between passes.
- Decoupled map placement/break tracking from BlockLogger recording and database availability.
- Fixed a cache-version mismatch that could block automatic updates after a fresh export, and corrected the cache readiness check for alternate terrain styles.
- Added main-thread resident surface-read timings to Suite Timing Hotspots.

Enable under **Web Map → Export Schedule → Live terrain updates → Save Export Schedule**. Initial defaults remain off, 120 seconds and four tiles per pass. A compatible initial export is required. With both pass types active, each normally runs every other interval. Other mods are unchanged.
