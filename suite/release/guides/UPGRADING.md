# Upgrading Server Essentials

Current suite: **1.2.27**  
Anti-Cheat config schema: **v49**

## Before upgrading

Back up:

```text
world save
ModConfig
BlockLogger SQLite database + WAL/SHM if copied live
suite persistent admin/report data
```

A clean server shutdown is the simplest safe point for a full backup.

## Replace the mod cleanly

1. Stop the server.
2. Remove/replace the previous Server Essentials build.
3. Install the new build.
4. Keep your existing `ModConfig` files unless you intentionally want fresh defaults. Suite 1.2.22 relocates its root-level JSON into `ModConfig/NewfiesServerEssentialsSuite/` automatically.
5. Start the server and watch startup logs for migration or config-recovery messages.

Do not mix DLLs/assets from multiple Server Essentials versions in the same mod folder.



## 1.2.22 centralized config folder

On first startup, Suite 1.2.22 moves its known root-level ModConfig JSON into `ModConfig/NewfiesServerEssentialsSuite/`. Discord 1.0.6 moves `NewfiesServerEssentialsDiscord.json` and the legacy `NewfiesDiscordLink.json` into the same folder. Each source is archived before removal and the active copy is byte-verified. If an old root copy conflicts with an existing centralized copy, the centralized copy remains active and the root copy is archived under `backups/`.

Generated world data is not moved: `essentials.db`, reports, diagnostics, Anti-Cheat history, WebMap terrain, and other world-specific data continue to live under the Suite ModData/world storage.

## Automatic config schema migrations

Suite 1.2.22 uses a central migration registry for Suite configuration files. When a saved config has an older `SchemaVersion`, Server Essentials:

1. reads the existing schema without rewriting the file;
2. creates a timestamped pre-migration backup under `ModConfig/NewfiesServerEssentialsSuite/backups/`;
3. aborts the migration if that backup cannot be created;
4. applies each schema step in order until the current version is reached;
5. writes the migrated config; and
6. keeps the five newest migration backups for that config.

Backup names look like:

```text
NewfiesWebMap.pre-migration-v12-to-v17-20260914-181500-123.json
```

Do not delete the newest pre-migration backup until the upgraded server has been verified. Corrupt JSON recovery remains separate and uses `.corrupt-*` backups.

A config with a schema newer than the running Suite is **not** rewritten or downgraded. That subsystem fails safe until a Suite version that understands the newer schema is installed. Migration failures are also kept separate from corrupt-JSON recovery so a valid older config is never renamed as corrupt merely because a migration step failed.

Current Suite configs covered by the registry include BlockLogger, feature gates, Animal Limiter, Login Protection, Anti-Cheat, Automated Restarts, Chat Display, Player Commands, AFK, Starter Kit, Warps, Player List, Web Server, Web Admin Accounts, Compatibility Ignores, Web Map, and Announcements.


## Integrated AFK migration

Newfie's AFK Camera 1.0.30 is integrated into Suite 1.2.22. Remove the standalone `newfiesafk` mod from the server and clients before relying on the integrated subsystem. While the standalone mod is detected, the Suite intentionally leaves its root `NewfiesAfk.json` in place and disables the integrated AFK subsystem to avoid duplicate commands, network channels, or Harmony patches. After the standalone mod is removed, the next startup relocates the legacy AFK config into `ModConfig/NewfiesServerEssentialsSuite/NewfiesAfk.json` and applies normal schema migration/backup rules.

The Suite remains client-optional: server AFK protection works for players without the Suite client, while the orbit camera requires the matching Suite client capability.

## Anti-Cheat migrations

The current Anti-Cheat schema is **v47**. Older configs are normalized/migrated on load.

Important behavior from recent versions includes:

- conservative PacketGuard movement sampling;
- unknown/modded mount hardening;
- teleport-like movement re-baselining/suppression logic;
- stale PacketGuard risk expiry;
- per-family live risk for Movement, Reach, Mining, Packet Abuse, and Client Integrity;
- block-collision/noclip protection defaulting conservatively on migrated installs;
- explicit WebPanel/Shift+L saves stamping the current schema before normalization so legacy migrations do not overwrite operator choices.

After an upgrade, verify Packet Protection settings instead of assuming old values are still correct.

## Version 1.2.15 checks

After upgrading to 1.2.15:

1. Open **Shift+L → Anti-Cheat → Packet Protection**.
2. Toggle a harmless setting, save, reopen the page, and confirm it persists.
3. Verify button text is upright/readable in EN/DE/FR/RU if you use those locales.
4. Check `/anticheat status`.
5. If you use WebMap, verify its runtime status and terrain-layer selections.
6. If you use the WebPanel, verify the displayed version/build now reports 1.2.15.

## Companion versions

The Server Essentials version does not force the Player List version to match.

Current bundle companions:

```text
AntiCheat Client 1.1.26
Player List 1.1.1
```

When Client Integrity is configured to require the AntiCheat Client, use the matching companion package expected by your Server Essentials build/policy.

## Rolling back

If an upgrade introduces a serious problem:

1. Stop the server.
2. Restore the previous mod build.
3. Restore the matching `ModConfig` backup if the newer build wrote settings your older build does not understand.
4. Restore persistent data only when necessary; do not blindly overwrite newer BlockLogger/admin history with an older copy unless that loss is acceptable.

## Validation after any upgrade

Use this minimum checklist:

```text
/nsefeatures list
/blocklogger status
/anticheat status
/serverutils web status
```

Then test the specific subsystem you changed before putting the server back into normal operation.
