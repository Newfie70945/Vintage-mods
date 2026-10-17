# Suite & MOTD improvement pass

## Included

- `artifacts/NewfiesServerEssentialsSuiteV1.2.44.zip`: installable Suite mod.
- `artifacts/NewfiesCommunityMOTDV2.1.6.zip`: installable MOTD mod.
- `suite/source` and `motd/source`: reconstructed, editable C# projects.
- `suite/release` and `motd/release`: manifests, assets and original release documentation. Older guides may retain historical version headers; CHANGELOG.md describes this build.
- `descriptions/Suite-ModDB-description.html` and `MOTD-ModDB-description.html`: paste into the ModDB description editor's HTML/source view. Preview before saving; the ModDB editor may sanitise styling.
- `tests`, `docs/VALIDATION.md` and `validation`: reproducible checks and validation results.

## Install

Back up your world and ModConfig first. Stop the server, remove the old Suite/MOTD mod ZIPs, and install the new ZIPs. Do not leave duplicate builds installed.

The Suite is server-side with optional matching client installation for the Shift+L interface. Install the updated Suite on staff clients to receive the player-picker fix. Install matching MOTD versions on the server and clients that use its window. Existing content is preserved; use the news/update post builder for newly styled local posts. Existing local posts are not automatically rewritten.

Do not upload this entire source bundle to the server's Mods folder. Use only the installable ZIPs in `artifacts`. Those ZIPs contain their own mod assemblies, not copies of the game's SQLite, Harmony or API assemblies. Do not package the whole build output directory or add `e_sqlite3.dll` to a mod root.

## Build

Targets the official Vintage Story 1.22.7 assemblies and .NET 10 SDK. No proprietary game assemblies are distributed in this source bundle.

Linux/macOS:

```sh
export VINTAGE_STORY=/path/to/vintagestory
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet build suite/source/NewfiesServerEssentialsSuite.csproj -c Release
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet build motd/source/NewfiesCommunityMOTD.csproj -c Release
```

PowerShell:

```powershell
$env:VINTAGE_STORY = 'C:\Path\To\Vintagestory'
dotnet build suite/source/NewfiesServerEssentialsSuite.csproj -c Release
dotnet build motd/source/NewfiesCommunityMOTD.csproj -c Release
```

## Source provenance

These projects were reconstructed by decompiling your public Suite 1.2.43 and MOTD 2.1.5 assemblies after the workspace loss, then applying this pass. They are not the original authoring projects and do not recover unshipped work, old tests or original comments. The complete recovered source is included so you can retain a local copy.

Suite baseline ZIP SHA-256: `e0e784ff90d594013aa2a03add261eb15480ed2cabd30d0ec7ad3ab0e0732b9f`.

## Verification

```sh
dotnet run --project tests/Checks.csproj -c Release
node tests/editor-checks.js tests/web/motd-editor-Declarations.js
node --check tests/web/motd-editor-Declarations.js
```

For staging validation, use a disposable world and a real client; never test against a running production world. See VALIDATION.md for the checks already performed and their limits.
