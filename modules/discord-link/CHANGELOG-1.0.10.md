# Newfie's Server Essentials - Discord 1.0.10

- Acknowledge slash interactions before waiting on the game thread; edit the deferred reply afterward.
- Paginate `/players` in groups of 25 and bound response length safely.
- Register `/help` when any Discord command groups are enabled and list only enabled groups.
- Restrict requests/staff identity to the configured guild and suppress mentions in replies.
- Time out game-thread requests after eight seconds and skip queued work that has not started before expiry.
- Preserve existing moderation, account linking, role sync, restart and bridge behavior.
