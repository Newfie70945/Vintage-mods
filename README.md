# Newfies Vintage Story Mods

Author source for Newfie's Server Essentials Suite and companion modules, maintained by [Newfie70945](https://github.com/Newfie70945).

| Module | Version | Source |
| --- | --- | --- |
| Server Essentials Suite | 1.2.52 | [modules/suite](modules/suite) |
| Community MOTD | 2.1.6 | [modules/motd](modules/motd) |
| AntiCheat Client / Client Integrity | 1.1.28 | [modules/anticheat-client](modules/anticheat-client) |
| ChatGuard | 2.1.4 | [modules/chatguard](modules/chatguard) |
| Discord Link | 1.0.13 | [modules/discord-link](modules/discord-link) |
| Player List | 1.1.2 | [modules/player-list](modules/player-list) |
| Server Optimizer | 0.5.0-preview | [modules/optimizer](modules/optimizer) |

Shared storage helpers live in [modules/shared](modules/shared). The Suite includes its integrated administration, anti-cheat, roles, player commands, logging, webpanel, map and diagnostics code.

**The standalone website and ModDB HTML descriptions are maintained separately and are excluded from this source repository.** The standalone Website Connector project is also excluded at the owner’s request.

## Build and package

Requires .NET 10 SDK, Python 3 and your own official Vintage Story 1.22.7 installation. Set `VINTAGE_STORY` to the directory containing `VintagestoryAPI.dll`, `Lib` and `Mods`.

```sh
export VINTAGE_STORY=/path/to/vintagestory
python3 tools/build.py
```

PowerShell:

```powershell
$env:VINTAGE_STORY = 'C:\Path\To\Vintagestory'
python tools/build.py
```

To build selected modules:

```sh
python3 tools/build.py --module suite --module motd
```

Installable ZIPs appear in the ignored `artifacts/` directory. The build script excludes game-provided assemblies and includes required third-party managed dependencies, such as Discord Link's Discord.Net assemblies. Do not zip an entire build output manually or put native SQLite DLLs in a mod's root.

The experimental optimizer retains its independent [build and validation instructions](modules/optimizer/README.md).

## Source checks

```sh
dotnet run --project tests/Checks.csproj -c Release
node tests/editor-checks.js tests/web/motd-editor-Declarations.js
python3 tools/check_sources.py
```

GitHub Actions runs these standalone checks without proprietary game DLLs. Additional recovered tests remain with their owning module. See [validation](docs/VALIDATION.md) for the checks run during restoration and their limits.

## Install

Back up your world and configuration, stop the server, and replace the old mod ZIPs without leaving duplicate builds installed. Install the Suite on the server, with a matching optional client installation for staff using Shift+L. Install matching MOTD builds on the server and participating clients.

Each module has its own manifest, configuration and installation requirements. Check its README before deploying.

## Source recovery

The original author sources were restored from `NSS-1.2.43-MS11-CENTER1-source-with-Website-2.7.5.zip`. They replace the temporary decompiled projects initially imported into this repository. Original comments, helper scripts and available tests are preserved; the newer Suite UID-targeting/console fixes and MOTD/editor formatting changes were ported onto them.

Earlier reconstructed files remain in Git history and the local backup bundle. The optimizer continues the reconstructed 0.1.0 project with independently guarded API patches in 0.2.0; it is not a restoration of the lost later optimizer project. See [module provenance](docs/MODULES.md).

No live configuration, credentials, world saves or proprietary game binaries belong in this repository. No open-source licence has been selected; no additional licence grant is implied. Third-party components retain their respective terms.

## Suite and optimizer together

Install **both inner mod ZIPs** from the paired download into the game server's Mods folder; do not install the outer download ZIP as a mod. Requires the inspected official Vintage Story 1.22.7 build. Open **Suite → Optimizations → Newfies Server Optimizer** to configure NOS, inspect module activation/conflicts, and start or stop its bounded profiler. Stop the NOS capture before editing settings. Read-only panel accounts cannot edit; Suite Safe Diagnostics Mode blocks module changes and capture starts, while still allowing a running capture to stop.

Build the pair with `python tools/build.py --module suite --with-optimizer` after setting VINTAGE_STORY and installing the documented .NET SDK. NOS remains independently disableable and works without the Suite; the Suite reports when the matching NOS bridge is unavailable.
