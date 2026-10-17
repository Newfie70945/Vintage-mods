# Author-source restoration validation

Validated on 17 October 2026 against official Vintage Story 1.22.7 with .NET 10 SDK.

## Completed

- All seven regular mod projects build successfully: Suite, MOTD, AntiCheat Client, ChatGuard, Discord Link, Website Connector and Player List. Each build reported 0 errors and 0 warnings.
- The central build script produced installable ZIPs using current manifest versions. Native and managed game dependencies are not copied into the mod roots; Discord Link's required third-party managed dependencies are included.
- 22 C# checks passed for stable UID selection, duplicate/renamed display names, feed markup, dates, unread identifiers and local/Discord merge behavior.
- 7 JavaScript post-builder checks passed. Embedded editor declarations and bindings pass syntax/fixture consistency checks.
- Restored Website Connector tests passed: authentication, read-only routing, account-link expiry and replay protection, bounded input, cache coalescing and shutdown.
- Restored offline-inventory tests passed: UID isolation, missing/corrupt/oversized data, immutable database access and cache invalidation.
- A disposable dedicated 1.22.7 server loaded the newly built Suite and MOTD. Feature-list, MOTD reload, stats, save and stop commands worked. The run exited with code 0, logged no server errors and passed SQLite world-save integrity checking.
- A credential-literal scan found no embedded credentials. The signing-key generation utility contains PEM boundary strings, not a private key.
- The active Git tree excludes the standalone website, ModDB description files, generated build output, binaries, world saves and live configuration.

## Scope and limits

The dedicated-server check used no authenticated clients and is not a performance benchmark. No graphical client was available: final GUI appearance and complete compatibility with the roleplay-name mod still need a staging check with a real client and the full modpack.

The entire historical recovered test collection has not been run. Tests requiring the game need `VINTAGE_STORY` set; older runtime probes may need environment-specific fixtures. The optimizer remains its previously delivered experimental snapshot and was not changed or newly benchmarked during this restoration.

After the owner-requested removal of Website Connector, GitHub Actions runs the standalone Suite/MOTD checks without distributing proprietary game assemblies. Full module builds require an owner-provided game installation.

The uploaded archive and pre-restoration Git bundle are kept outside the active repository as backups. Source provenance and archive SHA-256 are recorded in SOURCE-PROVENANCE.json.

## Subsequent owner-requested exclusions

The standalone Website Connector project was removed after the restoration checks above. Six regular mod projects remain active, plus the independent optimizer preview. Its earlier build/test results are historical; the Connector is not included in the current solution, package manifest or CI. Shared storage, tests, build tools and CI remain because they support the retained projects.
