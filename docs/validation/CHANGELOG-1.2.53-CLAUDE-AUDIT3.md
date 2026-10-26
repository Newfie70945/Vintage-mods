# Follow-up changes — Suite 1.2.53 / NOS 0.20.15

- Added optional slow block-update diagnostics with Suite controls and a configurable 10–1,000 ms threshold (default 50 ms).
- Reports pending block-update queues and separately measured block-handling/block-entity-sync durations, with remaining or unavailable time shown honestly.
- Limits warnings to one every 10 seconds and exposes observed-pass, slow-pass and observer-failure counters.
- Diagnostic failures stand down the observer without stopping native work or replacing the game's original exceptions.
- Defaults off. This helps investigate block-update stalls; it does not itself optimize simulation or identify the responsible mod.
- Preserves the preceding barrel optimization, map allocation/shading improvements and translocator default-off behavior.

The proposed disguise command was withheld after reproducing a cleanup ownership bug and identifying missing dimension propagation. Sailing and other companion mods are unchanged.
