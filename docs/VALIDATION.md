# Validation and limits

Built against the official Vintage Story 1.22.7 assemblies using .NET SDK 10.0.402.

- Suite: clean compile completed with 0 errors and 34 existing/recovery warnings (obsolete APIs, unawaited existing cache work and module-initializer analysis). These warnings were not silenced. The final incremental build log alone may show fewer warnings; the improvement archive’s `validation/suite-build-final.log` contains the full compile.
- MOTD: 0 errors, 0 warnings.
- 22 C# checks passed for UID targeting, duplicate display names, renamed players, formatting, escaping, date handling, unread IDs, merge/fallback behavior and the feed cap.
- 7 JavaScript post-builder checks passed. The extracted embedded editor declarations and bindings pass Node syntax checking.
- A disposable local official 1.22.7 server loaded both new installable ZIPs. `/nsefeatures list`, `/motdreload`, `/stats`, `/save` and `/stop` were exercised. The corrected run produced no server errors, exited with code 0 and passed SQLite save integrity checking.
- The first run found a real pre-existing null-player failure in SuiteFeatureCommands.Reply from the server console. This was fixed before the successful repeat.
- Archive checks confirm one own mod DLL per installable ZIP, correct versions, no duplicate archive entries and no traversal paths. Game/native SQLite assemblies are not bundled.
- Suite HTML is approximately 11 KB; MOTD HTML is approximately 5.7 KB. Both are under the requested 60,000-byte limit. Exact sizes/hashes are in the improvement archive’s `validation/summary.json`.

This is a startup/reload/save check with no authenticated clients, not a multiplayer benchmark. No graphical game client was available. The final in-game layout, live read/unread interaction and end-to-end interaction with Simple Roleplay Nametag Updated still require staging validation with a real client and your modpack. The underlying UID selection behavior is covered by the checks; this does not establish compatibility with every version of a third-party mod.

The normal startup self-test warns that integrated Anti-Cheat is disabled in the disposable test configuration. The startup/save generated isolated tick-overload warnings; this check makes no performance claims.

ModDB source for the compatibility request: https://mods.vintagestory.at/show/mod/67501 (Ambredjazz, account Valkyo / roleplay name Lorreth).
Roleplay mod: https://mods.vintagestory.at/rpnameupdated
MOTD public baseline metadata: https://mods.vintagestory.at/api/mod/newfiescommunitymotd

No ModDB descriptions or releases were published. The HTML is supplied for owner review and manual upload.
