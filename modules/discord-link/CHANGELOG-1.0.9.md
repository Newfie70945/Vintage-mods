# Newfie's Server Essentials - Discord 1.0.9

## Discord-linked in-game role synchronization

- Added optional Discord role -> Vintage Story role synchronization for linked accounts.
- Only explicitly configured Discord role IDs participate.
- Added mapping priorities so the highest-priority configured Discord role wins when a member has multiple mapped roles.
- Added conservative `EligibleBaseRoleCodes` control (default `suplayer`) so automatic sync does not take over existing custom/admin/manual roles.
- Discord role assignments now keep persistent ownership/provenance state in the world save.
- Removing a mapped Discord role restores the exact previous in-game role only when the player is still using the role Discord assigned.
- Manual in-game role changes are detected and permanently suspend automatic role management for that player instead of being overwritten.
- Existing manually assigned target roles are never claimed merely because they match a Discord mapping.
- Added `/discordrolesync status [player]`, `/discordrolesync sync <player>`, `/discordrolesync resume <player>`, and `/discordrolesync release <player>` admin tools.
- Role checks run on Discord ready, player join, account link, periodic refresh, and manual request.
- Offline players are never changed; their state is reconciled when they next join.
- Discord guild-member roles are fetched through REST, so the feature does not require the privileged Guild Members gateway intent.
- Added `docs/ROLE-SYNC.md` and example configuration.

## Versioning

- Discord Integration: **1.0.9**
- No Server Essentials, Client Integrity, Player List, Community MOTD, or ChatGuard code changes are required for this feature.

