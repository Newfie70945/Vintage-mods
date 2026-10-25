2.1.1b Modern button polish
- Replaced brown button surfaces with rounded dashboard-style controls.
- Added blue active tabs, slate inactive tabs, Discord purple, Web Map blue, gold OK, and subdued close/utility controls.

2.1.1a HTML shell polish
- Removed the visible brown dialog look in favor of a darker custom rounded dashboard shell.
- Added rounded-corner panels for header, tab tray, info strip, content areas, and footer.
- Improved the visual hierarchy to better follow the provided HTML mockup.

2.1.1 Modern dashboard and branding polish
- Upgraded the in-game MOTD shell toward the supplied modern community-dashboard design.
- Added dark slate panels, gold identity accents, blue navigation/status accents, and a live SERVER ONLINE header block.
- Improved native dialog-frame coverage and responsive containment.
- Added the dedicated NewfiesCommunityMOTD/branding/ server-data folder for server-owned logo files.
- Improved footer/content spacing, status-strip placement, entity display, Server Info presentation, and scrollbar behavior.
- Preserved free-form Home/Rules VTML, Discord provider independence, logo document flow, and existing tab functionality.

Newfies Community MOTD 2.0 - Revamp Foundation

New in this build:
- Native tabbed community hub layout
- Live player roster supplied by the server
- Staff-first player grouping
- Optional player roles and ping display
- Online / max player count in the footer
- Native local PNG logo support
- Configurable subtitle, accent color and footer
- Existing VTML/Markdown, link button, scrolling, /motd, /motdreload and /motdresetall retained
- Version metadata normalized to 2.0.0

LOGO SETUP
Place a PNG at:
assets/newfiescommunitymotd/textures/gui/logo.png

Config:
"EnableLogo": true,
"LogoAsset": "newfiescommunitymotd:textures/gui/logo.png",
"LogoWidth": 180,
"LogoHeight": 90

Important: logo assets are part of the mod package. Every client receives the same mod asset, so the server config selects which packaged logo to display.

2.0 companion integration foundation
------------------------------------
MOTD now exposes NewfiesMotdIntegration protocol 1.
Newfie's Discord can register INewfiesDiscordMotdProvider to supply Announcements, Updates and Events.
Newfie's Server Suite can register INewfiesSuiteMotdProvider to supply player-facing operational status (for example scheduled restart notices).
MOTD never receives Discord credentials/tokens; the Discord mod remains the Discord authority.
Tabs can be independently enabled in newfiescommunitymotd.json.
The Suite WebPanel should edit the same MOTD config through its companion integration; WebPanel UI code belongs in Server Suite, not this standalone MOTD archive.

2.1.0 Server Suite companion control
-------------------------------------
When Newfie's Server Suite is installed, Community MOTD exposes a small server-side event-bus control bridge.
The Suite can show a Server Tools > Community MOTD page for editing/applying this mod's configuration,
reloading the config from disk, and resetting client seen-state. Community MOTD remains independently
installable and does not require Server Suite to run.

MS5 UI note: Players and Server tabs are hidden. An optional bottom Web Map button can be configured with EnableWebMapButton, WebMapButtonText, and WebMapUrl (also exposed in the Suite WebPanel).
MS5 footer polish: the MOTD footer now uses a contained separator/action area, centered footer text, balanced external-link/Web Map buttons, and a consistent primary OK action on the right.

Bundled sample logo
-------------------
The mod now includes assets/newfiescommunitymotd/textures/gui/logo.png using the Newfie's Server Suite branding as a ready-to-see sample/default packaged logo. sample-logo.png preserves the same sample for reference. Server owners can still use the server-provided PNG workflow for custom branding without rebuilding the client mod.

Logo safety (2.1.7 audit build): custom PNG logos are limited to 1 MiB encoded, 4,096 pixels per side and 4,194,304 decoded pixels in total. Both server packaging and client decoding check the header. Resize oversized logos; they are skipped rather than decoded.
