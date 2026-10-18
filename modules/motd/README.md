# Newfie's Community MOTD 2.1.6

Current release: [current release notes](../../CHANGELOG.md).
Target: **Vintage Story 1.22.7 / .NET 10**

Community MOTD is the in-game community information hub for Newfie's Server Suite. It remains independently installable; when Server Suite is present, Suite can edit/apply MOTD settings through its companion control bridge.

## Current behavior

- Modern dark community-dashboard shell with responsive containment.
- Home, News, Updates, Events, Rules, Server, Commands and Community content surfaces as configured.
- Free-form VTML remains supported for Home and Rules.
- Discord feed content is supplied by Discord Integration; MOTD never owns or receives Discord credentials.
- Server-owned PNG branding is supported under `NewfiesCommunityMOTD/branding/` in the server data directory.
- Player/server information is generated server-side.
- No periodic all-player refresh broadcast: live content is supplied on MOTD open or explicit refresh paths.
- Open-dialog refreshes do not force a closed MOTD back open.
- Discord feed channel configuration persists through the Suite integration bridge.
- Current no-scrollbar-regression layout behavior should be preserved in future UI work.

## Commands

- `/motd` — open the MOTD.
- `/motdreload` — reload MOTD configuration from disk (controlserver privilege).
- `/motdresetall` — clear client seen-MOTD caches (controlserver privilege).

## Configuration

The server creates/loads the Community MOTD config through Vintage Story's mod-config system. When Server Suite is installed, Shift+L/WebPanel companion controls can edit and apply the same configuration.

Server-provided branding files are constrained to the Community MOTD data directory. The current default logo location is:

```text
NewfiesCommunityMOTD/branding/motd-logo.png
```

## Build

```text
dotnet build NewfiesCommunityMOTD.csproj -c Release
```

Earlier development notes remain available in Git history.
