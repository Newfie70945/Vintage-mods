# Newfie's Player List 1.1.2

Standalone-capable universal player list for Vintage Story 1.22.7.

## Installation modes

Newfie's Player List is declared as a universal mod with `requiredOnClient: true` and `requiredOnServer: false`.

This gives it both deployment modes:

- **Server-installed:** Put the mod on the server and Vintage Story will include it in the server's required mod list so joining players can receive it automatically.
- **Player-installed:** A player can also install it locally and use it on a server that does not have the mod installed.

The server-side instance is intentionally passive. The visible player-list HUD and key handling run only on the client.

## Behavior

- Hold the configured Player List key (Tab by default) to show the online roster.
- Release the key to hide it.
- Works without Newfie's Server Essentials Suite.
- Standalone mode shows the online player roster, visible player roles, and the current in-game date.
- No Suite-specific server packets are required for standalone operation.
- No idle roster-refresh loop runs while Tab is not held.
- Roster refresh is approximately every 2 seconds while visible.

## Optional Server Suite enhancement

When Newfie's Server Essentials Suite is installed on the server, Player List automatically uses the existing optional Player List channel for richer information and server-controlled appearance settings.

Suite 1.2.34 can also send role-based player-name colors. Administrators can map exact Vintage Story role names/codes to `#RRGGBB` colors from the Suite WebPanel. Without a Suite override, the roster uses the role color exposed by Vintage Story.

Suite-enhanced mode can provide:

- server title and maximum-player count;
- ping;
- session time;
- AFK state;
- Player Management roster information;
- server-configured layout and appearance.

If the Suite channel is missing or stops responding, the player list automatically falls back to standalone mode instead of displaying an error or becoming unusable.

## Recommended server installation

Place the compiled Newfie's Player List ZIP in the server's `Mods` folder and restart the server. Because it is a universal mod required on clients, Vintage Story will offer/require it as part of the server's mod set.

Players who want to use it independently can place the same ZIP in their local Vintage Story `Mods` folder. Because `requiredOnServer` is false, the mod can still operate when the connected server does not have it.

## GUI safety

The HUD uses one custom Cairo roster surface and does not depend on the Server Suite GUI or WebPanel.

## Optional Server Suite supporter lamp

When connected to a Server Essentials server, Player List 1.1.2 can render the optional privilege-gated `/lamp` supporter perk. The integration is passive on servers without Server Essentials and does not affect the standalone player-list HUD.


## 1.1.2 BuildFix5

- BuildFix5 adds a synchronized player watched-attribute state path for Supporter Lamp in addition to the existing Suite packet channel. This removes custom-channel handshake timing as a single point of failure while keeping `/lamp` registered and authorized exclusively by Server Suite.
- BuildFix4 strengthens Supporter Lamp visibility by using the Suite's brighter dynamic-light value and reapplying active light state more frequently on clients.
- BuildFix3 removes the compile-time dependency on `ColorUtil.MergeLightHSV`, which is absent from some Vintage Story 1.22.7 reference sets. Supporter Lamp now uses an equivalent local brightness-weighted HSV merge helper.

- Hardened `/lamp` rendering by merging the supporter light into the final `EntityPlayer.LightHsv` getter through a late Harmony postfix.
- This keeps the supporter light compatible with vanilla held lights and wearable-light mods that also patch player lighting.
- Retains the BuildFix1 `api.Network.GetChannelState(...)` compatibility correction for Vintage Story 1.22.7.
- Public Player List version remains 1.1.2.
