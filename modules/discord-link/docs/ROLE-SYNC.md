# Discord -> Vintage Story Role Sync

Discord Integration 1.0.9 can automatically assign a Vintage Story privilege role to a linked player when that Discord account has one of the explicitly configured Discord roles.

The feature is **disabled by default** and only operates on accounts linked through Newfie's Discord Link system.

## Safety model: manual roles always win

Role Sync tracks whether it actually assigned a player's in-game role.

- It only takes initial ownership from an `EligibleBaseRoleCodes` role (default: `suplayer`).
- If the player already has the mapped target role before Role Sync ever manages them, it is treated as a manual role and is not claimed.
- If an administrator manually changes a player away from a Discord-managed role, Role Sync detects the difference and permanently suspends automatic role changes for that player.
- Removing a mapped Discord role restores the exact role the player had before Role Sync took ownership, but only if the player is still on the role Role Sync assigned.
- If a manual role change happened first, Role Sync leaves that role untouched.
- `/discordrolesync resume <player>` clears a detected manual-override suspension. The next automatic assignment can happen only if the player's current role is eligible for automatic management.
- `/discordrolesync release <player>` relinquishes Discord ownership while leaving the current in-game role unchanged. This is useful when staff want a Discord-assigned role to become permanently manual.

This means Discord does not become the ultimate authority over Vintage Story roles.

## Configuration

The settings live under `Linking.RoleSync` in `NewfiesServerEssentialsDiscord.json`.

```json
"RoleSync": {
  "Enabled": true,
  "SyncIntervalSeconds": 60,
  "EligibleBaseRoleCodes": [
    "suplayer"
  ],
  "NotifyPlayers": true,
  "LogChanges": true,
  "Mappings": [
    {
      "Enabled": true,
      "DiscordRoleId": 111111111111111111,
      "GameRoleCode": "supporter",
      "Priority": 100
    },
    {
      "Enabled": true,
      "DiscordRoleId": 222222222222222222,
      "GameRoleCode": "supporterplus",
      "Priority": 200
    }
  ]
}
```

Only Discord roles listed in `Mappings` participate.

If a linked Discord member has multiple mapped roles, the mapping with the **highest `Priority`** wins. Equal priorities keep config-file order.

`GameRoleCode` must be an existing Vintage Story role code from the server configuration. Unknown role codes are ignored and logged instead of changing the player.

## Discord permissions / intents

Role Sync uses Discord's REST `Get Guild Member` endpoint to read the linked member's role IDs. It does **not** require enabling the privileged Guild Members gateway intent and does not download the entire member list.

The bot must be a member of the configured `GuildId`.

## When synchronization happens

A linked online player is checked:

- when the Discord bot becomes ready;
- when the player finishes joining the server;
- immediately after linking their Discord account;
- periodically using `SyncIntervalSeconds` (minimum 30 seconds);
- when an administrator runs `/discordrolesync sync <player>`.

Offline players are not modified. Their Discord role state is reconciled the next time they join.

## Admin command

Requires the Vintage Story `controlserver` privilege.

```text
/discordrolesync status
/discordrolesync status <player>
/discordrolesync sync <player>
/discordrolesync resume <player>
/discordrolesync release <player>
```

`status` shows whether the feature is enabled, mapping counts, managed players and detected manual overrides.

`resume` does not force a role. It only clears the manual-override lock; normal safe-base-role rules still apply afterward.

`release` removes Discord's ownership record without changing the current role, making that role manual.
