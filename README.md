# Newfies Server Suite

Source repository for Newfie's Vintage Story server tools, maintained by [Newfie70945](https://github.com/Newfie70945).

| Project | Version | Source | Status |
| --- | --- | --- | --- |
| Server Essentials Suite | 1.2.44 | [`suite/source`](suite/source) | Reconstructed public source plus MOTD/roleplay-name fixes |
| Community MOTD | 2.1.6 | [`motd/source`](motd/source) | Reconstructed public source plus news/update formatting improvements |
| Server Optimizer | 0.1.0-preview | [`modules/optimizer`](modules/optimizer) | Previously delivered experimental snapshot; independent of the Suite |

The Suite includes its integrated anti-cheat, administration, roles, player commands, logging, webpanel, map and other subsystems. Standalone companion mods are separate projects; see [module availability](docs/MODULES.md).

## Build

Requires .NET 10 SDK and your own official Vintage Story 1.22.7 installation. Set `VINTAGE_STORY` to the installation directory, containing `VintagestoryAPI.dll`, `Lib` and `Mods`.

```sh
export VINTAGE_STORY=/path/to/vintagestory
python3 tools/build.py
```

PowerShell:

```powershell
$env:VINTAGE_STORY = 'C:\Path\To\Vintagestory'
python tools/build.py
```

The build script creates installable Suite and MOTD ZIPs in `artifacts/`. It packages only each mod's own assembly, manifest and assets/documentation. Game, Harmony and SQLite dependencies must not be copied into the mod ZIPs.

For the optimizer, use its separate [build instructions](modules/optimizer/README.md).

## Checks

```sh
dotnet run --project tests/Checks.csproj -c Release
node tests/editor-checks.js tests/web/motd-editor-Declarations.js
node --check tests/web/motd-editor-Declarations.js
node --check tests/web/motd-editor-Bindings.js
```

GitHub Actions runs the standalone C# and JavaScript checks without requiring proprietary game assemblies. Full builds require your local game installation. See [validation and limits](docs/VALIDATION.md).

## Install

Back up your world and configuration. Replace old mod ZIPs; do not leave duplicate builds installed. Install the Suite on the server, with matching optional staff-client installation for Shift+L. Install matching MOTD builds on the server and clients using its window.

See [changes](CHANGELOG.md), [installation details](docs/BUILD-AND-INSTALL.md) and the [ModDB HTML descriptions](descriptions/).

## Recovery and project history

The Suite and MOTD authoring workspace was lost. This repository starts from reconstructed public Suite 1.2.43 and MOTD 2.1.5 assemblies, with subsequent fixes applied. It does not contain the lost original Git history, original comments or unreleased changes. The optimizer directory comes from the delivered 0.1.0-preview source archive, rather than later unverified working files.

No live configuration, website accounts, tokens, world saves, server logs or proprietary game DLLs are included. Keep your own machine's private files outside this repository.

## Rights

No open-source licence has been selected for this repository. No additional licence grant is implied. Third-party and game components retain their respective terms.
