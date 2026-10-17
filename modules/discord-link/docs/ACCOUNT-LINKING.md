# Account linking

Account linking is built directly into **Newfie's Server Essentials - Discord 1.0.0+**. There is no separate Discord Link mod.

## In-game commands

```text
/discordlink
/discordlink status
/discordlink cancel
/discordlink unlink confirm
```

Admin commands (`controlserver`):

```text
/discordlinkadmin stats
/discordlinkadmin status <player>
/discordlinkadmin discord <discordUserId>
/discordlinkadmin unlink <player> confirm
```

## Discord commands

When `Commands.Enabled`, `Commands.EnableLinkCommands`, and `Linking.Enabled` are enabled:

```text
/link <code>
/linkstatus
/unlink
```

Slash-command responses are ephemeral.

## Identity and persistence

Persistent identity is the Vintage Story `PlayerUID`, not player name. The module stores:

```text
Vintage Story PlayerUID ↔ Discord user ID
```

The database remains in the world save under the legacy-compatible key:

```text
newfiesdiscordlink:database-v1
```

Keeping this key is intentional so upgrading from the old standalone `NewfiesDiscordLink` module does not discard existing account links.

Pending link codes are memory-only, cryptographically generated, and indexed by SHA-256 hash. They are not persisted across server restarts.

## Suite event contracts

The unified module still publishes/handles these server event-bus contracts so other suite modules can query account identity without a hard DLL reference:

```text
newfiesse:discord-link-query
newfiesse:discord-link-changed
```

The older `newfiesse:discord-link-request` event is accepted for backward compatibility, but the unified bot no longer uses it internally.

## Optional role synchronization

Starting in Discord Integration 1.0.9, a linked account can optionally use selected Discord roles to assign a Vintage Story privilege role. Account linking remains the identity authority; role synchronization is a separate opt-in setting under `Linking.RoleSync`.

The role system records only assignments it made itself. Existing manual roles are not claimed, and a later manual role change suspends automation for that player. See `ROLE-SYNC.md` for configuration and admin controls.
