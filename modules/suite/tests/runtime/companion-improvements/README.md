# Companion improvement checks

Runs against the compiled Discord, ChatGuard, and PlayerList assemblies and official Vintage Story references. It uses isolated save-data proxies, fake Discord REST results, real engine GUI composers with a fake texture disposer, and system Cairo on Linux. It never connects to Discord or a running server.

Set `VINTAGE_STORY`, `DISCORD_ASSEMBLY`, `CHATGUARD_ASSEMBLY`, and `PLAYERLIST_ASSEMBLY` to absolute paths, then run:

```sh
dotnet run --project tests/runtime/companion-improvements -c Release
```

Checks cover rollback of failed account/role writes, malformed or unsupported saved-data preservation, disabled reconciliation before player access, feed caching/coalescing/retry/shutdown behavior, GUI texture disposal and unregistering, stale/late response rejection, and reuse of unchanged supporter-light colors. In-game appearance and lighting with third-party mods still need an ordinary client smoke test.
