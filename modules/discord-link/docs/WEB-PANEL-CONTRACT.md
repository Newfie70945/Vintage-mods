# Web panel contract

Recommended page: **Server Essentials > Discord Integration**

## Secrets
- Bot token is write-only in the panel.
- Never return the saved bot token to the browser.
- Return `botTokenConfigured: true/false` instead.
- Never log the token.

## Recommended controls
- Integration enabled
- Bot token replace button
- Guild ID
- Invite URL
- Game chat channel
- Game -> Discord toggle
- Discord -> Game toggle
- Join channel: Same as Game Chat or a specific channel
- Leave channel: Same as Game Chat or a specific channel
- Death channel: Same as Game Chat or a specific channel
- Server start/stop channel
- Admin Audit channel
- Anti-Cheat report channel
- Minimum Anti-Cheat severity
- Critical role ping option
- Test connection / send test message

## Channel list
The module exposes `DiscordBridgeService.GetTextChannels()` internally. Server Essentials Core should surface an authenticated API endpoint that returns only `{id,name}` for the configured guild. Store IDs, display names.

## Saving
Core validates and writes `NewfiesServerEssentialsDiscord.json`, then calls:

`PushEvent("newfiesse:discord-reload-config")`

For a future shared configuration service, replace the file write while keeping the event contract stable.

## Discord Commands section

The panel should expose:

- Enable Discord slash commands
- Enable player information commands
- Enable staff commands
- Allow members with Discord Administrator
- Staff role multi-select (`StaffRoleIds`) populated from the configured guild
- Rules text / rules URL field

The Staff Role selector should store Discord role IDs, not role names, so renaming a Discord role does not break authorization.
