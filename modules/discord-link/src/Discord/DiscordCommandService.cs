using Discord;
using Discord.WebSocket;
using NewfiesServerEssentials.Discord.Config;
using NewfiesServerEssentials.Discord.Linking;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace NewfiesServerEssentials.Discord.Discord;

/// <summary>
/// Discord slash-command surface for Newfie's Server Essentials.
/// Public commands are read-only. Staff commands are role-gated and audited.
/// Commands that belong to Server Essentials Core (ban/mute/etc.) are emitted
/// onto the suite event bus so Core remains the authority for moderation state.
/// </summary>
public sealed class DiscordCommandService
{
    public const string StaffCommandRequestEvent = "newfiesse:discord-staff-command";

    private readonly ICoreServerAPI sapi;
    private readonly DiscordSocketClient client;
    private readonly Func<DiscordIntegrationConfig> getConfig;
    private readonly DiscordBridgeService bridge;
    private readonly DiscordLinkService linkService;
    private string lastRegisteredCommandSignature = "";
    private string staffRoleSignature = "";
    private HashSet<ulong> staffRoleIds = new();

    public DiscordCommandService(ICoreServerAPI sapi, DiscordSocketClient client, Func<DiscordIntegrationConfig> getConfig, DiscordBridgeService bridge, DiscordLinkService linkService)
    {
        this.sapi = sapi;
        this.client = client;
        this.getConfig = getConfig;
        this.bridge = bridge;
        this.linkService = linkService;
    }

    public async Task RegisterGuildCommandsAsync()
    {
        var cfg = getConfig();
        if (cfg.GuildId == 0) return;

        var guild = client.GetGuild(cfg.GuildId);
        if (guild == null)
        {
            sapi.Logger.Warning("[NSE Discord] Cannot register slash commands: guild {0} is not available.", cfg.GuildId);
            return;
        }

        string registrationSignature = BuildRegistrationSignature(cfg);
        if (string.Equals(lastRegisteredCommandSignature, registrationSignature, StringComparison.Ordinal))
        {
            sapi.Logger.Debug("[NSE Discord] Slash-command schema unchanged after reconnect; skipping redundant guild command overwrite.");
            return;
        }

        var commands = new List<ApplicationCommandProperties>();

        if (cfg.Commands.Enabled && cfg.Commands.EnablePlayerInfoCommands)
        {
            commands.Add(new SlashCommandBuilder().WithName("players").WithDescription("Shows a page of online players.").AddOption("page",ApplicationCommandOptionType.Integer,"Page number (25 players per page)",isRequired:false,minValue:1,maxValue:10000).Build());
            commands.Add(Simple("restarttime", "Shows time until the next scheduled server restart."));
            commands.Add(Simple("serverinfo", "Shows Vintage Story server information."));
            commands.Add(Simple("date", "Shows the current in-game date and time."));
            commands.Add(Simple("discord", "Shows the server Discord invite."));
            commands.Add(Simple("rules", "Shows the server rules or rules link."));

        }

        if(cfg.Commands.Enabled)commands.Add(Simple("help","Shows available Discord commands."));

        if (cfg.Commands.Enabled && cfg.Commands.EnableLinkCommands)
        {
            commands.Add(new SlashCommandBuilder()
                .WithName("link")
                .WithDescription("Link your Discord account to your Vintage Story account.")
                .AddOption("code", ApplicationCommandOptionType.String, "One-time code from /discordlink in-game", isRequired: true)
                .Build());
            commands.Add(Simple("linkstatus", "Shows your Vintage Story account link status."));
            commands.Add(Simple("unlink", "Removes your Discord-to-Vintage-Story account link."));
        }

        if (cfg.Commands.Enabled && cfg.Commands.EnableStaffCommands)
        {
            commands.Add(new SlashCommandBuilder()
                .WithName("announce")
                .WithDescription("Staff: announce a message to the game server.")
                .AddOption("message", ApplicationCommandOptionType.String, "Announcement text", isRequired: true)
                .Build());

            commands.Add(new SlashCommandBuilder()
                .WithName("kick")
                .WithDescription("Staff: kick an online player.")
                .AddOption("player", ApplicationCommandOptionType.String, "Exact player name", isRequired: true, maxLength: 64)
                .AddOption("reason", ApplicationCommandOptionType.String, "Reason", isRequired: false, maxLength: 240)
                .Build());

            commands.Add(new SlashCommandBuilder()
                .WithName("allowcharselonce")
                .WithDescription("Staff: allow a player to reopen character selection once.")
                .AddOption("player", ApplicationCommandOptionType.String, "Player name", isRequired: true, maxLength: 64)
                .Build());

            commands.Add(WhitelistBuilder());

            commands.Add(ModerationBuilder("ban", "Staff: temporarily ban a player through Server Essentials Core.", needsDuration: true));
            commands.Add(ModerationBuilder("unban", "Staff: clear a Suite temporary ban.", needsReason: false));
            commands.Add(ModerationBuilder("mute", "Staff: mute a player through Server Essentials Core.", needsDuration: true));
            commands.Add(ModerationBuilder("unmute", "Staff: unmute a player through Server Essentials Core.", needsReason: false));
        }

        if (cfg.Commands.Enabled && cfg.Commands.EnableRestartCommands)
        {
            commands.Add(RestartBuilder());
        }

        try
        {
            await guild.BulkOverwriteApplicationCommandAsync(commands.ToArray());
            lastRegisteredCommandSignature = registrationSignature;
            sapi.Logger.Notification("[NSE Discord] Registered {0} Discord slash commands in guild {1}.", commands.Count, guild.Name);
        }
        catch (Exception e)
        {
            sapi.Logger.Error("[NSE Discord] Failed to register Discord slash commands: {0}", e);
        }
    }

    public async Task HandleAsync(SocketSlashCommand command)
    {
        var cfg = getConfig();
        if (!cfg.Commands.Enabled)
        {
            if (!command.HasResponded)
                await ReplyAsync(command,"Discord commands are currently disabled in Server Essentials.", ephemeral: true);
            return;
        }

        if(command.GuildId!=cfg.GuildId || cfg.GuildId==0) {
            await ReplyAsync(command,"Use this command inside the configured Discord server.",ephemeral:true);return;
        }
        try
        {
            bool publicInfo=new[]{"players","restarttime","serverinfo","date","discord","rules"}.Contains(command.Data.Name)&&cfg.Commands.EnablePlayerInfoCommands;
            if(!command.HasResponded)await command.DeferAsync(ephemeral:!publicInfo);
            switch (command.Data.Name)
            {
                case "players": await HandlePlayers(command); break;
                case "restarttime": await HandleRestartTime(command); break;
                case "serverinfo": await HandleServerInfo(command); break;
                case "date": await HandleDate(command); break;
                case "discord": await HandleDiscord(command); break;
                case "rules": await HandleRules(command); break;
                case "help": await HandleHelp(command); break;
                case "link": await HandleLinkTransport(command, "link"); break;
                case "linkstatus": await HandleLinkTransport(command, "status"); break;
                case "unlink": await HandleLinkTransport(command, "unlink"); break;
                case "announce": await HandleAnnounce(command); break;
                case "kick": await HandleKick(command); break;
                case "allowcharselonce":
                case "allowcharsel": // name used before 1.x; still answered while Discord refreshes its command list
                    await HandleAllowCharSel(command); break;
                case "whitelist": await HandleWhitelist(command); break;
                case "restart": await HandleRestartControl(command); break;
                case "ban":
                case "unban":
                case "mute":
                case "unmute":
                    await HandleSuiteModeration(command);
                    break;
                default:
                    await ReplyAsync(command,"This Discord command is no longer available. The guild command list may still be refreshing; try again after the bot reconnects.", ephemeral: true);
                    break;
            }
        }
        catch (Exception e)
        {
            sapi.Logger.Error("[NSE Discord] Slash command /{0} failed: {1}", command.Data.Name, e);
            await ReplyAsync(command,e is TimeoutException ? "The server did not respond within 8 seconds. Expired queued work is skipped; work already started may still complete. Check the server before retrying a staff action." : "The command failed on the server. Check the Server Essentials logs.", ephemeral: true);
        }
    }

    private async Task HandlePlayers(SocketSlashCommand command)
    {
        if (!await EnsurePublic(command)) return;
        var result = await OnMainThread(() =>
        {
            var players = GetLivePlayers();
            var names = players
                .OrderBy(p => p.PlayerName, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.PlayerName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();

            int page=1;var option=command.Data.Options.FirstOrDefault(o=>o.Name=="page");
            if(option?.Value!=null)page=(int)Math.Clamp(Convert.ToInt64(option.Value),1,10000);
            return FormatPlayerPage(names,Math.Max(0,sapi.Server.Config.MaxClients),page);
        });
        await ReplyAsync(command,result);
    }

    private async Task HandleRestartTime(SocketSlashCommand command)
    {
        if (!await EnsurePublic(command)) return;
        var result = await OnMainThread(() =>
        {
            var tree = new TreeAttribute();
            tree.SetBool("handled", false);
            sapi.Event.PushEvent("newfiesse:restart-query", tree);
            return tree.GetBool("handled", false)
                ? tree.GetString("status", "Automated restart status is unavailable.")
                : "Newfie's Server Suite automated restart scheduler is not available.";
        });
        await ReplyAsync(command,result);
    }

    private async Task HandleServerInfo(SocketSlashCommand command)
    {
        if (!await EnsurePublic(command)) return;
        var result = await OnMainThread(() =>
        {
            var players = GetLivePlayers();
            var calendar = sapi.World.Calendar;
            var date = calendar == null ? "Unavailable" : $"{calendar.PrettyDate()} — {FormatGameTime(calendar.FullHourOfDay)}";
            var serverName = string.IsNullOrWhiteSpace(sapi.Server.Config.ServerName) ? "Vintage Story Server" : sapi.Server.Config.ServerName;
            return $"**{Escape(serverName)}**\nWorld: **{Escape(sapi.WorldManager.SaveGame.WorldName)}**\nPlayers: **{players.Length}/{Math.Max(0, sapi.Server.Config.MaxClients)}**\nIn-game date: **{Escape(date)}**\nDiscord bridge: **{(bridge.IsConnected ? "Connected" : "Disconnected")}**";
        });
        await ReplyAsync(command,result);
    }

    private async Task HandleDate(SocketSlashCommand command)
    {
        if (!await EnsurePublic(command)) return;
        var result = await OnMainThread(() =>
        {
            var calendar = sapi.World.Calendar;
            return calendar == null
                ? "The game calendar is not available yet."
                : $"**In-game date:** {Escape(calendar.PrettyDate())}\n**Time:** {FormatGameTime(calendar.FullHourOfDay)}";
        });
        await ReplyAsync(command,result);
    }

    private async Task HandleDiscord(SocketSlashCommand command)
    {
        if (!await EnsurePublic(command)) return;
        var invite = getConfig().InviteUrl;
        await ReplyAsync(command,string.IsNullOrWhiteSpace(invite) ? "The Discord invite has not been configured." : invite);
    }

    private async Task HandleRules(SocketSlashCommand command)
    {
        if (!await EnsurePublic(command)) return;
        var rules = getConfig().Commands.RulesText;
        await ReplyAsync(command,string.IsNullOrWhiteSpace(rules) ? "Server rules have not been configured in Server Essentials yet." : Truncate(rules, 1900));
    }

    private async Task HandleHelp(SocketSlashCommand command)
    {
        var staff = IsStaffIdentity(command, out _);
        var lines = new List<string>
        {
            "**Player commands**",
            "`/players` — online players",
            "`/restarttime` — next scheduled restart",
            "`/serverinfo` — server/world information",
            "`/date` — in-game date and time",
            "`/discord` — Discord invite",
            "`/rules` — server rules"
        };
        if(!getConfig().Commands.EnablePlayerInfoCommands)lines.Clear();
        lines.Insert(0,"`/help` — available commands");
        if (getConfig().Commands.EnableLinkCommands)
        {
            lines.Add("`/link` — link Discord to your Vintage Story account");
            lines.Add("`/linkstatus` — show link status");
            lines.Add("`/unlink` — remove the link");
        }
        if (staff && getConfig().Commands.EnableStaffCommands)
        {
            lines.Add("");
            lines.Add("**Staff commands**");
            lines.Add("`/announce` — broadcast a staff announcement in-game");
            lines.Add("`/kick` — disconnect an online player");
            lines.Add("`/allowcharselonce` — allow one character-selection reopen");
            lines.Add("`/whitelist` — add or remove a player from the Vintage Story whitelist");
            lines.Add("`/ban` / `/unban` — manage Suite temporary bans");
            lines.Add("`/mute` / `/unmute` — manage Suite mutes");
        }
        if (staff && getConfig().Commands.EnableRestartCommands)
        {
            lines.Add("");
            lines.Add("**Restart control**");
            lines.Add("`/restart` — view, schedule, cancel, skip, or trigger a restart");
        }
        await ReplyAsync(command,string.Join("\n", lines), ephemeral: true);
    }

    private async Task HandleLinkTransport(SocketSlashCommand command, string operation)
    {
        var cfg = getConfig();
        if (!cfg.Commands.EnableLinkCommands || !cfg.Linking.Enabled)
        {
            await ReplyAsync(command,"Discord account linking commands are disabled.", ephemeral: true);
            return;
        }

        var result = await OnMainThread(() => linkService.HandleDiscordOperation(
            operation,
            command.User.Id.ToString(),
            Actor(command),
            operation == "link" ? (Option(command, "code") ?? "") : ""));

        var response = string.IsNullOrWhiteSpace(result.Response)
            ? (result.Success ? "Discord linking request completed." : "Discord linking request was rejected.")
            : result.Response;

        await ReplyAsync(command,Truncate(response, 1900), ephemeral: true);
    }

    private async Task HandleAnnounce(SocketSlashCommand command)
    {
        if (!await RequireStaff(command)) return;
        var message = Option(command, "message");
        if (string.IsNullOrWhiteSpace(message))
        {
            await ReplyAsync(command,"Announcement text is required.", ephemeral: true);
            return;
        }

        message = Truncate(message.Replace("\r", " ").Replace("\n", " "), 500);
        await OnMainThread(() => sapi.BroadcastMessageToAllGroups($"[Discord Staff] {message}", EnumChatType.Notification));
        Audit(command, "Discord /announce", message, "Success");
        await ReplyAsync(command,"Announcement sent.", ephemeral: true);
    }

    private async Task HandleKick(SocketSlashCommand command)
    {
        if (!await RequireStaff(command)) return;
        var playerName = Option(command, "player");
        var reason = Option(command, "reason");
        if (string.IsNullOrWhiteSpace(playerName) || playerName.Length > 64)
        {
            await ReplyAsync(command,"A player name of at most 64 characters is required.", ephemeral: true);
            return;
        }
        if (reason != null) reason = Truncate(reason.Replace("\r", " ").Replace("\n", " "), 240);

        var result = await OnMainThread(() =>
        {
            var player = GetLivePlayers()
                .FirstOrDefault(p => p.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase));
            if (player == null) return (false, "That player is not online.");
            player.Disconnect(string.IsNullOrWhiteSpace(reason) ? "Kicked by server staff." : reason);
            return (true, $"Kicked {player.PlayerName}.");
        });

        Audit(command, "Discord /kick", $"{playerName} — {reason}", result.Item1 ? "Success" : "Failed");
        await ReplyAsync(command,result.Item2, ephemeral: true);
    }

    private async Task HandleAllowCharSel(SocketSlashCommand command)
    {
        if (!await RequireStaff(command)) return;
        string playerName = (Option(command, "player") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(playerName))
        {
            await ReplyAsync(command,"Player name is required.", ephemeral: true);
            return;
        }
        if (playerName.Length > 64)
        {
            await ReplyAsync(command,"Player name is too long.", ephemeral: true);
            return;
        }

        // Online players are updated in place (same attribute the native command writes). Everyone else goes
        // through the real /player command so a missing player is reported instead of silently "queued".
        var online = await OnMainThread(() =>
        {
            var player = GetLivePlayers()
                .FirstOrDefault(p => p.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase));
            if (player?.Entity == null) return null;
            player.Entity.WatchedAttributes.SetBool("allowcharselonce", true);
            player.Entity.WatchedAttributes.MarkPathDirty("allowcharselonce");
            player.BroadcastPlayerData(true);
            return player.PlayerName;
        });

        (bool, string) result;
        if (online != null)
            result = (true, $"Character selection re-enabled once for **{Escape(online)}**. They can use `.charsel` now.");
        else
        {
            var native = await ExecuteServerCommandAsync($"/player {QuoteCommandArgument(playerName)} allowcharselonce");
            string detail = string.IsNullOrWhiteSpace(native.Message) ? "" : " " + Escape(Truncate(native.Message, 300));
            result = native.Success
                ? (true, $"Character selection re-enabled once for offline player **{Escape(playerName)}**.{detail}")
                : (false, $"Vintage Story did not accept the request for **{Escape(playerName)}**.{detail}");
        }

        Audit(command, "Discord /allowcharselonce", playerName, result.Item1 ? "Success" : "Failed");
        await ReplyAsync(command,result.Item2, ephemeral: true);
    }

    private async Task HandleWhitelist(SocketSlashCommand command)
    {
        if (!await RequireStaff(command)) return;

        string action = (Option(command, "action") ?? "").Trim().ToLowerInvariant();
        string playerName = (Option(command, "player") ?? "").Trim();
        if (action is not ("add" or "remove"))
        {
            await ReplyAsync(command,"Whitelist action must be `Add` or `Remove`.", ephemeral: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(playerName))
        {
            await ReplyAsync(command,"Player name is required.", ephemeral: true);
            return;
        }
        if (playerName.Length > 64)
        {
            await ReplyAsync(command,"Player name is too long.", ephemeral: true);
            return;
        }

        string state = action == "add" ? "on" : "off";
        string commandLine = $"/player {QuoteCommandArgument(playerName)} whitelist {state}";
        var execution = await ExecuteServerCommandAsync(commandLine);

        string detail = $"action={action}; player={playerName}";
        if (!string.IsNullOrWhiteSpace(execution.Message))
            detail += $"; server={execution.Message}";
        Audit(command, "Discord /whitelist", detail, execution.Success ? "Success" : "Failed");

        if (!execution.Success)
        {
            string failure = string.IsNullOrWhiteSpace(execution.Message)
                ? $"Vintage Story rejected the whitelist request for **{Escape(playerName)}**."
                : $"Vintage Story rejected the whitelist request: {Escape(Truncate(execution.Message, 1500))}";
            await ReplyAsync(command,failure, ephemeral: true);
            return;
        }

        string response = action == "add"
            ? $"Added **{Escape(playerName)}** to the Vintage Story whitelist."
            : $"Removed **{Escape(playerName)}** from the Vintage Story whitelist.";
        await ReplyAsync(command,response, ephemeral: true);
    }

    private async Task HandleRestartControl(SocketSlashCommand command)
    {
        if (!await RequireStaff(command, getConfig().Commands.EnableRestartCommands, "Discord restart commands are disabled.")) return;

        string action = (Option(command, "action") ?? "status").Trim().ToLowerInvariant();
        long minutes = OptionLong(command, "minutes") ?? 0;
        string reason = (Option(command, "reason") ?? "").Trim();
        if (reason.Length > 240) reason = reason[..240];

        if (action == "schedule" && (minutes < 1 || minutes > 1440))
        {
            await ReplyAsync(command,"For `schedule`, minutes must be between 1 and 1440.", ephemeral: true);
            return;
        }

        var tree = new TreeAttribute();
        tree.SetString("operation", action);
        tree.SetString("actor", Actor(command));
        tree.SetString("discordUserId", command.User.Id.ToString());
        tree.SetString("discordInteractionId", command.Id.ToString());
        tree.SetString("reason", reason);
        tree.SetInt("minutes", (int)Math.Clamp(minutes, 0, 1440));

        await OnMainThread(() => sapi.Event.PushEvent("newfiesse:restart-control", tree));

        bool handled = tree.GetBool("handled", false);
        bool success = tree.GetBool("success", false);
        string response = tree.GetString("response", "");
        Audit(command, "Discord /restart", $"action={action}; minutes={minutes}; reason={reason}", handled ? (success ? "Success" : "Failed") : "Not handled");

        if (!handled)
        {
            await ReplyAsync(command,"Server Essentials restart control is not available.", ephemeral: true);
            return;
        }

        await ReplyAsync(command,
            string.IsNullOrWhiteSpace(response) ? (success ? "Restart command completed." : "Restart command was rejected.") : Truncate(response, 1800),
            ephemeral: true);
    }

    private async Task HandleSuiteModeration(SocketSlashCommand command)
    {
        if (!await RequireStaff(command)) return;

        var player = Option(command, "player");
        var duration = Option(command, "duration");
        var reason = Option(command, "reason");
        if (string.IsNullOrWhiteSpace(player))
        {
            await ReplyAsync(command,"Player name is required.", ephemeral: true);
            return;
        }
        if (player.Length > 64 || (duration?.Length ?? 0) > 32 || (reason?.Length ?? 0) > 240)
        {
            await ReplyAsync(command,"Player name (64), duration (32) or reason (240) is too long.", ephemeral: true);
            return;
        }
        if ((command.Data.Name == "ban" || command.Data.Name == "mute") && string.IsNullOrWhiteSpace(duration))
        {
            await ReplyAsync(command,command.Data.Name == "ban"
                ? "A ban duration is required, for example `30m`, `6h`, `3d`, or `2w`."
                : "A mute duration is required, for example `30m`, `6h`, `3d`, `2w`, or `permanent`.", ephemeral: true);
            return;
        }

        var actor = Actor(command);
        var tree = new TreeAttribute();
        tree.SetString("command", command.Data.Name);
        tree.SetString("player", player);
        tree.SetString("duration", duration ?? "");
        tree.SetString("reason", reason ?? "");
        tree.SetString("discordUserId", command.User.Id.ToString());
        tree.SetString("discordUser", actor);
        tree.SetString("discordInteractionId", command.Id.ToString());

        await OnMainThread(() => sapi.Event.PushEvent(StaffCommandRequestEvent, tree));

        var handled = tree.GetBool("handled", false);
        var success = tree.GetBool("success", false);
        var response = tree.GetString("response", "");
        var result = handled ? (success ? "Success" : "Failed") : "Requested";
        Audit(command, $"Discord /{command.Data.Name}", $"{player} — {duration} — {reason}", result);

        if (handled)
        {
            var text = string.IsNullOrWhiteSpace(response)
                ? (success ? $"`/{command.Data.Name}` completed for **{Escape(player)}**." : $"`/{command.Data.Name}` was rejected by Server Essentials Core.")
                : Truncate(response, 1800);
            await ReplyAsync(command,text, ephemeral: true);
        }
        else
        {
            await ReplyAsync(command,$"`/{command.Data.Name}` request sent to Server Essentials Core for **{Escape(player)}**. Core did not return an immediate result.", ephemeral: true);
        }
    }

    private IServerPlayer[] GetLivePlayers()
    {
        // World.AllOnlinePlayers is the engine's list of connected clients and is what the join/leave relay,
        // MOTD and Suite player list count. It is the primary source and is NOT filtered by connection state:
        // filtering it dropped most players (a 20-player server reported 1/100). Server.Players also lists
        // known offline players, so only its Connected/Playing entries are merged in. De-duplicated by UID.
        var byUid = new Dictionary<string, IServerPlayer>(StringComparer.OrdinalIgnoreCase);

        void Add(IServerPlayer? player)
        {
            if (player == null) return;
            var key = string.IsNullOrWhiteSpace(player.PlayerUID) ? player.PlayerName : player.PlayerUID;
            if (!string.IsNullOrWhiteSpace(key)) byUid[key] = player;
        }

        try { foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>()) Add(player); }
        catch (Exception e) { sapi.Logger.Debug("[NSE Discord] AllOnlinePlayers unavailable: {0}", e.Message); }

        try
        {
            foreach (var player in sapi.Server.Players ?? Array.Empty<IServerPlayer>())
                if (IsLiveConnection(player)) Add(player);
        }
        catch (Exception e) { sapi.Logger.Debug("[NSE Discord] Server.Players unavailable: {0}", e.Message); }

        return byUid.Values.ToArray();
    }

    private static bool IsLiveConnection(IServerPlayer? player)
    {
        if (player == null) return false;
        return player.ConnectionState == EnumClientState.Connected || player.ConnectionState == EnumClientState.Playing;
    }

    private static string FormatGameTime(double fullHourOfDay)
    {
        if (double.IsNaN(fullHourOfDay) || double.IsInfinity(fullHourOfDay)) return "Unavailable";
        double wrapped = fullHourOfDay % 24d;
        if (wrapped < 0) wrapped += 24d;
        int hour = (int)Math.Floor(wrapped);
        int minute = (int)Math.Floor((wrapped - hour) * 60d + 0.5d);
        if (minute >= 60)
        {
            minute = 0;
            hour = (hour + 1) % 24;
        }
        return $"{hour:00}:{minute:00}";
    }

    private async Task<bool> EnsurePublic(SocketSlashCommand command)
    {
        var cfg = getConfig();
        if (cfg.Commands.Enabled && cfg.Commands.EnablePlayerInfoCommands) return true;
        if (!command.HasResponded)
            await ReplyAsync(command,"Discord player-information commands are currently disabled.", ephemeral: true);
        return false;
    }

    private Task<bool> RequireStaff(SocketSlashCommand command) =>
        RequireStaff(command, getConfig().Commands.EnableStaffCommands, "Discord staff commands are disabled.");

    private async Task<bool> RequireStaff(SocketSlashCommand command, bool featureEnabled, string disabledReason)
    {
        if (!featureEnabled)
        {
            Audit(command, $"Discord /{command.Data.Name}", "Feature disabled", "Denied");
            await ReplyAsync(command,disabledReason, ephemeral: true);
            return false;
        }
        if (IsStaffIdentity(command, out var reason)) return true;
        Audit(command, $"Discord /{command.Data.Name}", "Permission check", "Denied");
        await ReplyAsync(command,reason, ephemeral: true);
        return false;
    }

    private bool IsStaffIdentity(SocketSlashCommand command, out string reason)
    {
        var cfg = getConfig().Commands;
        if (command.User is not SocketGuildUser user || user.Guild.Id!=getConfig().GuildId)
        {
            reason = "Staff commands can only be used inside the configured Discord server.";
            return false;
        }
        if (cfg.AllowDiscordAdministrators && user.GuildPermissions.Administrator)
        {
            reason = "";
            return true;
        }
        var allowedRoles = GetStaffRoleIds(cfg);
        if (allowedRoles.Count > 0 && user.Roles.Any(role => allowedRoles.Contains(role.Id)))
        {
            reason = "";
            return true;
        }
        reason = "You do not have a Server Essentials staff role for this command.";
        return false;
    }

    private void Audit(SocketSlashCommand command, string action, string detail, string result)
    {
        var cfg = getConfig();
        if (!cfg.AdminAudit.Enabled) return;
        var channel = cfg.AdminAudit.ChannelId == 0 ? cfg.Chat.ChannelId : cfg.AdminAudit.ChannelId;
        if (channel == 0) return;
        bridge.Enqueue(channel,
            $"🔨 **ADMIN AUDIT**\n**Admin:** {Escape(Actor(command))}\n**Action:** {Escape(action)}\n**Detail:** `{EscapeCode(Truncate(detail, 800))}`\n**Result:** {Escape(result)}\n**Source:** Discord");
    }

    private HashSet<ulong> GetStaffRoleIds(DiscordIntegrationConfig.DiscordCommandsConfig cfg)
    {
        string signature = cfg.StaffRoleIds == null || cfg.StaffRoleIds.Length == 0
            ? ""
            : string.Join(",", cfg.StaffRoleIds.OrderBy(x => x));
        if (!string.Equals(signature, staffRoleSignature, StringComparison.Ordinal))
        {
            staffRoleSignature = signature;
            staffRoleIds = cfg.StaffRoleIds == null
                ? new HashSet<ulong>()
                : new HashSet<ulong>(cfg.StaffRoleIds);
        }
        return staffRoleIds;
    }

    private static string BuildRegistrationSignature(DiscordIntegrationConfig cfg)
    {
        var c = cfg.Commands;
        return $"v3|{cfg.GuildId}|{c.Enabled}|{c.EnablePlayerInfoCommands}|{c.EnableLinkCommands}|{c.EnableStaffCommands}|{c.EnableRestartCommands}";
    }

    private static SlashCommandProperties Simple(string name, string description) =>
        new SlashCommandBuilder().WithName(name).WithDescription(description).Build();

    private static SlashCommandProperties WhitelistBuilder()
    {
        var action = new SlashCommandOptionBuilder()
            .WithName("action")
            .WithDescription("Whitelist action")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true)
            .AddChoice("Add", "add")
            .AddChoice("Remove", "remove");

        return new SlashCommandBuilder()
            .WithName("whitelist")
            .WithDescription("Staff: add or remove a player from the Vintage Story whitelist.")
            .AddOption(action)
            .AddOption("player", ApplicationCommandOptionType.String, "Exact Vintage Story player name", isRequired: true, maxLength: 64)
            .Build();
    }

    private static SlashCommandProperties RestartBuilder()
    {
        var action = new SlashCommandOptionBuilder()
            .WithName("action")
            .WithDescription("Restart action")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true)
            .AddChoice("Status", "status")
            .AddChoice("Schedule", "schedule")
            .AddChoice("Restart now", "now")
            .AddChoice("Cancel manual restart", "cancel")
            .AddChoice("Skip next automated restart", "skipnext");

        return new SlashCommandBuilder()
            .WithName("restart")
            .WithDescription("Staff: control Server Essentials restarts.")
            .AddOption(action)
            .AddOption("minutes", ApplicationCommandOptionType.Integer, "Delay for Schedule (1-1440 minutes)", isRequired: false, minValue: 1, maxValue: 1440)
            .AddOption("reason", ApplicationCommandOptionType.String, "Optional restart reason", isRequired: false, maxLength: 240)
            .Build();
    }

    private static SlashCommandProperties ModerationBuilder(string name, string description, bool needsReason = true, bool needsDuration = false)
    {
        var b = new SlashCommandBuilder()
            .WithName(name)
            .WithDescription(description)
            .AddOption("player", ApplicationCommandOptionType.String, "Player name", isRequired: true, maxLength: 64);
        if (needsDuration)
            b.AddOption("duration", ApplicationCommandOptionType.String, name == "ban" ? "30m, 6h, 3d, or 2w" : "30m, 6h, 3d, 2w, or permanent", isRequired: true, maxLength: 32);
        if (needsReason)
            b.AddOption("reason", ApplicationCommandOptionType.String, "Reason", isRequired: false, maxLength: 240);
        return b.Build();
    }

    private static string? Option(SocketSlashCommand command, string name) =>
        command.Data.Options.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.ToString();

    private static long? OptionLong(SocketSlashCommand command, string name)
    {
        object? value = command.Data.Options.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
        if (value == null) return null;
        try { return Convert.ToInt64(value); }
        catch { return null; }
    }

    private static string QuoteCommandArgument(string value)
    {
        value = (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        return "\"" + value + "\"";
    }

    private static string Actor(SocketSlashCommand command) => command.User is SocketGuildUser gu ? gu.DisplayName : command.User.Username;
    internal static string Truncate(string value,int max) {
        if(value.Length<=max)return value;int take=Math.Max(0,max-1);
        if(take>0&&char.IsHighSurrogate(value[take-1]))take--;return value[..take]+"…";
    }
    internal static string FormatPlayerPage(string[] names,int capacity,int requestedPage) {
        int pages=Math.Max(1,(names.Length+24)/25),page=Math.Clamp(requestedPage,1,pages);
        string list=names.Length==0?"Nobody is online.":string.Join(", ",names.Skip((page-1)*25).Take(25).Select(n=>Escape(Truncate(n,64))));
        return Truncate($"**Players online:** {names.Length}/{capacity} · Page {page}/{pages}\n{list}",1900);
    }
    private static async Task ReplyAsync(SocketSlashCommand command,string text,bool ephemeral=false) {
        text=Truncate(text??"",1900);
        if(command.HasResponded)await command.ModifyOriginalResponseAsync(p=>{p.Content=text;p.AllowedMentions=AllowedMentions.None;});
        else await command.RespondAsync(text,ephemeral:ephemeral,allowedMentions:AllowedMentions.None);
    }
    private static string Escape(string s) => s.Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`").Replace("\r", " ").Replace("\n", " ");
    private static string EscapeCode(string s) => s.Replace("`", "ˋ").Replace("\r", " ").Replace("\n", " ");

    private async Task<(bool Success, string Message)> ExecuteServerCommandAsync(string commandLine)
    {
        var tcs = new TaskCompletionSource<(bool Success, string Message)>(TaskCreationOptions.RunContinuationsAsynchronously);
        long deadline=Environment.TickCount64+8000;
        sapi.Event.EnqueueMainThreadTask(() =>
        {
            if(Environment.TickCount64>=deadline){tcs.TrySetResult((false,"The queued command expired without running."));return;}
            try
            {
                var caller = new Caller
                {
                    Type = EnumCallerType.Console,
                    CallerPrivileges = new[] { "*" },
                    CallerRole = "admin",
                    FromChatGroupId = 0
                };
                var callArgs = new TextCommandCallingArgs
                {
                    Caller = caller,
                    LanguageCode = "en"
                };

                sapi.ChatCommands.ExecuteUnparsed(commandLine, callArgs, result =>
                {
                    try
                    {
                        bool success = result != null &&
                            (result.Status == EnumCommandStatus.Success || result.Status == EnumCommandStatus.UnknownLegacy);
                        string message = result?.StatusMessage ?? "";
                        if (result?.MessageParams != null && result.MessageParams.Length > 0 && !string.IsNullOrWhiteSpace(message))
                        {
                            try { message = string.Format(message, result.MessageParams); }
                            catch { }
                        }
                        tcs.TrySetResult((success, message));
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetResult((false, ex.Message));
                    }
                });
            }
            catch (Exception ex)
            {
                tcs.TrySetResult((false, ex.Message));
            }
        }, "nse-discord-server-command");

        Task completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(8)));
        if (completed == tcs.Task) return await tcs.Task;
        return (false, "The Vintage Story command did not return within 8 seconds. Check the server log; it may still have executed.");
    }

    private Task OnMainThread(Action action) => OnMainThread(()=>{action();return true;});
    private async Task<T> OnMainThread<T>(Func<T> action)
    {
        var tcs=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        long deadline=Environment.TickCount64+8000;
        sapi.Event.EnqueueMainThreadTask(()=>{
            if(Environment.TickCount64>=deadline){tcs.TrySetException(new TimeoutException());return;}
            try{tcs.TrySetResult(action());}catch(Exception ex){tcs.TrySetException(ex);}
        },"nse-discord-slash");
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }
}
