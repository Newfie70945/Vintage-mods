using NewfiesServerEssentials.Discord.Config;
using NewfiesServerEssentials.Discord.Discord;
using NewfiesServerEssentials.Discord.Linking;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace NewfiesServerEssentials.Discord.Integration;

/// <summary>
/// Cross-module contract for Newfie's Server Essentials.
/// Other server-side modules publish TreeAttribute payloads through the VS event bus.
/// No direct DLL dependency on Discord Integration is required.
/// </summary>
public sealed class SuiteEventBridge
{
    public const string AntiCheatEvent = "newfiesse:anticheat-report";
    public const string AdminAuditEvent = "newfiesse:admin-audit";
    public const string ReloadConfigEvent = "newfiesse:discord-reload-config";
    public const string DiscordControlEvent = "newfiesse:discord-control";
    public const string DiscordLinkChangedEvent = "newfiesse:discord-link-changed";
    public const string RestartNoticeEvent = "newfiesse:restart-notice";

    private readonly ICoreServerAPI sapi;
    private readonly DiscordBridgeService discord;
    private readonly Func<DiscordIntegrationConfig> getConfig;
    private readonly DiscordLinkService linkService;
    private readonly Action reloadConfig;
    private readonly Action<bool> saveConfig;
    private bool active;

    public SuiteEventBridge(
        ICoreServerAPI sapi,
        DiscordBridgeService discord,
        Func<DiscordIntegrationConfig> getConfig,
        DiscordLinkService linkService,
        Action reloadConfig,
        Action<bool> saveConfig)
    {
        this.sapi = sapi;
        this.discord = discord;
        this.getConfig = getConfig;
        this.linkService = linkService;
        this.reloadConfig = reloadConfig;
        this.saveConfig = saveConfig;
    }

    public void Register()
    {
        active = true;
        sapi.Event.RegisterEventBusListener(OnAntiCheat, filterByEventName: AntiCheatEvent);
        sapi.Event.RegisterEventBusListener(OnAdminAudit, filterByEventName: AdminAuditEvent);
        sapi.Event.RegisterEventBusListener(OnReloadConfig, filterByEventName: ReloadConfigEvent);
        sapi.Event.RegisterEventBusListener(OnDiscordControl, filterByEventName: DiscordControlEvent);
        sapi.Event.RegisterEventBusListener(OnDiscordLinkChanged, filterByEventName: DiscordLinkChangedEvent);
        sapi.Event.RegisterEventBusListener(OnRestartNotice, filterByEventName: RestartNoticeEvent);
    }

    public void Unregister()
    {
        // Vintage Story 1.22.7 exposes no event-bus unregister method. Disable this bridge
        // so any late event during shutdown is ignored; the server event bus is then torn down.
        active = false;
    }

    private void OnAntiCheat(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (!active) return;
        var cfg = getConfig();
        if (!cfg.Enabled || !cfg.AntiCheat.Enabled || data is not ITreeAttribute t) return;

        var severity = t.GetString("severity", "Medium");
        if (!MeetsSeverity(severity, cfg.AntiCheat.MinimumSeverity)) return;

        var player = t.GetString("playerName", "Unknown");
        var detection = t.GetString("detection", "Unknown detection");
        var reportId = t.GetString("reportId", "N/A");
        var action = t.GetString("action", "Flagged");
        var confidence = t.GetFloat("confidence", -1);
        var evidence = t.GetString("evidence", "");
        var position = t.GetString("position", "");

        var lines = new List<string>
        {
            "🛡️ **CLIENT INTEGRITY / ANTI-CHEAT REPORT**",
            $"**Player:** {Escape(player)}",
            $"**Detection:** {Escape(detection)}",
            $"**Severity:** {Escape(severity)}",
            $"**Action:** {Escape(action)}",
            $"**Report ID:** `{EscapeCode(reportId)}`"
        };
        if (confidence >= 0) lines.Insert(4, $"**Confidence:** {confidence:0.#}%");
        if (cfg.AntiCheat.IncludeEvidenceSummary && !string.IsNullOrWhiteSpace(evidence)) lines.Add($"**Evidence:** {Escape(evidence)}");
        if (cfg.AntiCheat.IncludePosition && !string.IsNullOrWhiteSpace(position)) lines.Add($"**Position:** `{EscapeCode(position)}`");
        if (cfg.AntiCheat.PingOnCritical && cfg.AntiCheat.CriticalRoleId != 0 && severity.Equals("Critical", StringComparison.OrdinalIgnoreCase))
            lines.Insert(0, $"<@&{cfg.AntiCheat.CriticalRoleId}>");

        var channel = cfg.AntiCheat.ChannelId == 0 ? cfg.Chat.ChannelId : cfg.AntiCheat.ChannelId;
        discord.Enqueue(channel, string.Join("\n", lines));
    }

    private void OnAdminAudit(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (!active) return;
        var cfg = getConfig();
        if (!cfg.Enabled || !cfg.AdminAudit.Enabled || data is not ITreeAttribute t) return;

        var actor = t.GetString("actor", "Unknown");
        var action = t.GetString("action", "Admin action");
        var detail = t.GetString("detail", "");
        var result = t.GetString("result", "Observed");
        var source = t.GetString("source", "Server Essentials");

        var lines = new List<string>
        {
            "🔨 **ADMIN AUDIT**",
            $"**Admin:** {Escape(actor)}",
            $"**Action:** {Escape(action)}",
            $"**Result:** {Escape(result)}",
            $"**Source:** {Escape(source)}"
        };
        if (!string.IsNullOrWhiteSpace(detail)) lines.Insert(3, $"**Detail:** `{EscapeCode(detail)}`");

        var channel = cfg.AdminAudit.ChannelId == 0 ? cfg.Chat.ChannelId : cfg.AdminAudit.ChannelId;
        discord.Enqueue(channel, string.Join("\n", lines));
    }

    private void OnDiscordLinkChanged(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (!active) return;
        if (data is not ITreeAttribute t) return;
        var cfg = getConfig();
        if (!cfg.Enabled) return;

        string action = t.GetString("action", "");
        string player = Escape(t.GetString("playerName", "Unknown"));
        string discordUser = Escape(t.GetString("discordUserName", t.GetString("discordUserId", "Unknown")));
        var route = action.Equals("link", StringComparison.OrdinalIgnoreCase)
            ? cfg.Events.DiscordLinked
            : action.Equals("unlink", StringComparison.OrdinalIgnoreCase)
                ? cfg.Events.DiscordUnlinked
                : null;
        if (route == null || !route.Enabled) return;

        string message = route.Format
            .Replace("{player}", player)
            .Replace("{discord}", discordUser);
        ulong channel = route.ChannelId == 0 ? cfg.Chat.ChannelId : route.ChannelId;
        discord.Enqueue(channel, message);
    }

    private void OnRestartNotice(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (!active) return;
        if (data is not ITreeAttribute tree) return;
        var cfg = getConfig();
        if (!cfg.Enabled || !cfg.Events.ServerStopping.Enabled || !discord.IsConnected) return;

        string message = tree.GetString("message", "").Trim();
        if (string.IsNullOrWhiteSpace(message)) return;

        // Restart notices are controlled by the Server Suite scheduler. Route them to
        // the configured Server Stopping channel, falling back to the normal chat channel.
        ulong channel = cfg.Events.ServerStopping.ChannelId == 0 ? cfg.Chat.ChannelId : cfg.Events.ServerStopping.ChannelId;
        if (channel == 0) return;

        // Avoid accidental mass mentions from an administrator-edited restart reason.
        message = message.Replace("@everyone", "@ everyone", StringComparison.OrdinalIgnoreCase)
                         .Replace("@here", "@ here", StringComparison.OrdinalIgnoreCase);
        discord.Enqueue(channel, "♻️ **SERVER RESTART** — " + Escape(message));
    }

    private void OnDiscordControl(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (!active) return;
        if (data is not ITreeAttribute tree) return;
        tree.SetBool("transportAvailable", true);

        string operation = tree.GetString("operation", "get").Trim().ToLowerInvariant();
        if (operation == "set")
        {
            string key = tree.GetString("key", "").Trim();
            bool restart = false;
            if (key.StartsWith("link.", StringComparison.OrdinalIgnoreCase))
            {
                if (!linkService.TryApplySetting(key, tree, out string linkMessage))
                {
                    tree.SetBool("success", false);
                    tree.SetString("message", linkMessage);
                    Populate(tree);
                    return;
                }

                saveConfig(false);
                tree.SetBool("success", true);
                tree.SetString("message", linkMessage);
                Populate(tree);
                return;
            }

            if (!TrySet(key, tree, out restart, out string message))
            {
                tree.SetBool("success", false);
                tree.SetString("message", message);
                Populate(tree);
                return;
            }

            saveConfig(restart);
            tree.SetBool("success", true);
            tree.SetString("message", message);
        }
        else if (operation == "test")
        {
            var cfg = getConfig();
            string target = tree.GetString("target", "chat").Trim().ToLowerInvariant();
            ulong channel = target switch
            {
                "admin" => cfg.AdminAudit.ChannelId == 0 ? cfg.Chat.ChannelId : cfg.AdminAudit.ChannelId,
                "anticheat" => cfg.AntiCheat.ChannelId == 0 ? cfg.Chat.ChannelId : cfg.AntiCheat.ChannelId,
                _ => cfg.Chat.ChannelId
            };
            bool queued = cfg.Enabled && discord.IsConnected && discord.Enqueue(channel, "✅ **Newfie's Server Suite Discord test** — integration settings are working.");
            tree.SetBool("success", queued);
            tree.SetString("message", queued ? "Test message queued for Discord." : "Could not queue the test message. Check that Discord is enabled and the selected channel is configured.");
        }
        else
        {
            tree.SetBool("success", true);
            tree.SetString("message", "Discord Integration status loaded.");
        }

        Populate(tree);
    }

    private void Populate(ITreeAttribute tree)
    {
        var cfg = getConfig();
        tree.SetBool("transportAvailable", true);
        tree.SetBool("transportEnabled", cfg.Enabled);
        tree.SetBool("transportConnected", discord.IsConnected);
        tree.SetBool("botTokenConfigured", !string.IsNullOrWhiteSpace(cfg.BotToken));
        tree.SetString("botName", discord.ConnectedBotName);
        tree.SetString("guildId", cfg.GuildId.ToString());
        tree.SetString("inviteUrl", cfg.InviteUrl ?? "");

        tree.SetBool("chatEnabled", cfg.Chat.Enabled);
        tree.SetString("chatChannelId", cfg.Chat.ChannelId.ToString());
        tree.SetBool("gameToDiscord", cfg.Chat.GameToDiscord);
        tree.SetBool("discordToGame", cfg.Chat.DiscordToGame);
        tree.SetBool("gameBoldSenderLabel", cfg.Chat.BoldGameSenderLabel);
        tree.SetBool("gameUseRoleColor", cfg.Chat.UseGameRoleColor);
        tree.SetString("discordNameColor", NormalizeColor(cfg.Chat.DiscordNameColor));
        tree.SetBool("discordShowDcTag", cfg.Chat.ShowDiscordTag);
        tree.SetBool("discordBoldSenderName", cfg.Chat.BoldDiscordSenderName);

        tree.SetBool("motdEnabled", cfg.Motd.Enabled);
        tree.SetString("motdAnnouncementsChannelId", cfg.Motd.AnnouncementsChannelId.ToString());
        tree.SetString("motdUpdatesChannelId", cfg.Motd.UpdatesChannelId.ToString());
        tree.SetString("motdEventsChannelId", cfg.Motd.EventsChannelId.ToString());
        tree.SetInt("motdMaxItemsPerFeed", Math.Clamp(cfg.Motd.MaxItemsPerFeed, 1, 25));

        PutRoute(tree, "playerJoin", cfg.Events.PlayerJoin);
        PutRoute(tree, "playerLeave", cfg.Events.PlayerLeave);
        PutRoute(tree, "playerDeath", cfg.Events.PlayerDeath);
        PutRoute(tree, "serverStarted", cfg.Events.ServerStarted);
        PutRoute(tree, "serverStopping", cfg.Events.ServerStopping);
        PutRoute(tree, "discordLinked", cfg.Events.DiscordLinked);
        PutRoute(tree, "discordUnlinked", cfg.Events.DiscordUnlinked);

        tree.SetBool("adminAuditEnabled", cfg.AdminAudit.Enabled);
        tree.SetString("adminAuditChannelId", cfg.AdminAudit.ChannelId.ToString());
        tree.SetBool("antiCheatDiscordEnabled", cfg.AntiCheat.Enabled);
        tree.SetString("antiCheatChannelId", cfg.AntiCheat.ChannelId.ToString());
        tree.SetBool("commandsEnabled", cfg.Commands.Enabled);
        tree.SetBool("playerInfoCommandsEnabled", cfg.Commands.EnablePlayerInfoCommands);
        tree.SetBool("staffCommandsEnabled", cfg.Commands.EnableStaffCommands);
        tree.SetBool("restartCommandsEnabled", cfg.Commands.EnableRestartCommands);
        tree.SetBool("linkCommandsEnabled", cfg.Commands.EnableLinkCommands);
        linkService.PopulateControl(tree);

        var channels = discord.GetTextChannels();
        tree.SetString("channels", string.Join("\n", channels.Select(x => $"{x.Id}\t{x.Name}")));
    }

    private static void PutRoute(ITreeAttribute tree, string prefix, DiscordIntegrationConfig.RoutedEvent route)
    {
        tree.SetBool(prefix + "Enabled", route.Enabled);
        tree.SetString(prefix + "ChannelId", route.ChannelId.ToString());
        tree.SetString(prefix + "Format", route.Format ?? "");
    }

    private bool TrySet(string key, ITreeAttribute tree, out bool restart, out string message)
    {
        restart = false;
        var cfg = getConfig();
        bool bv = tree.GetBool("boolValue", false);
        string sv = tree.GetString("stringValue", "").Trim();

        switch (key.ToLowerInvariant())
        {
            case "transport.enabled": cfg.Enabled = bv; restart = true; break;
            case "transport.guildid": if (!TryUlong(sv, out var guild)) return Fail("Guild ID must be a valid Discord snowflake.", out message); cfg.GuildId = guild; restart = true; break;
            case "transport.inviteurl": cfg.InviteUrl = sv; break;
            case "chat.enabled": cfg.Chat.Enabled = bv; break;
            case "chat.channelid": if (!TryUlong(sv, out var chat)) return Fail("Chat channel ID must be a valid Discord snowflake.", out message); cfg.Chat.ChannelId = chat; break;
            case "chat.gametodiscord": cfg.Chat.GameToDiscord = bv; break;
            case "chat.discordtogame": cfg.Chat.DiscordToGame = bv; break;
            case "chat.boldgamesenderlabel": cfg.Chat.BoldGameSenderLabel = bv; break;
            case "chat.usegamerolecolor": cfg.Chat.UseGameRoleColor = bv; break;
            case "chat.showdctag": cfg.Chat.ShowDiscordTag = bv; break;
            case "chat.bolddiscordname": cfg.Chat.BoldDiscordSenderName = bv; break;
            case "chat.discordnamecolor":
                if (!TryNormalizeColor(sv, out var color)) return Fail("Discord sender-name color must be a #RRGGBB hex color.", out message);
                cfg.Chat.DiscordNameColor = color;
                break;
            case "adminaudit.enabled": cfg.AdminAudit.Enabled = bv; break;
            case "adminaudit.channelid": if (!TryUlong(sv, out var admin)) return Fail("Admin audit channel ID must be a valid Discord snowflake.", out message); cfg.AdminAudit.ChannelId = admin; break;
            case "anticheat.enabled": cfg.AntiCheat.Enabled = bv; break;
            case "anticheat.channelid": if (!TryUlong(sv, out var ac)) return Fail("Anti-Cheat channel ID must be a valid Discord snowflake.", out message); cfg.AntiCheat.ChannelId = ac; break;
            case "motd.enabled": cfg.Motd.Enabled = bv; break;
            case "motd.announcementschannelid": if (!TryUlong(sv, out var motdAnnouncements)) return Fail("MOTD announcements channel ID must be a valid Discord snowflake.", out message); cfg.Motd.AnnouncementsChannelId = motdAnnouncements; break;
            case "motd.updateschannelid": if (!TryUlong(sv, out var motdUpdates)) return Fail("MOTD updates channel ID must be a valid Discord snowflake.", out message); cfg.Motd.UpdatesChannelId = motdUpdates; break;
            case "motd.eventschannelid": if (!TryUlong(sv, out var motdEvents)) return Fail("MOTD events channel ID must be a valid Discord snowflake.", out message); cfg.Motd.EventsChannelId = motdEvents; break;
            case "motd.maxitemsperfeed": cfg.Motd.MaxItemsPerFeed = Math.Clamp(tree.GetInt("intValue", cfg.Motd.MaxItemsPerFeed), 1, 25); break;
            case "commands.enabled": cfg.Commands.Enabled = bv; restart = true; break;
            case "commands.playerinfoenabled": cfg.Commands.EnablePlayerInfoCommands = bv; restart = true; break;
            case "commands.staffenabled": cfg.Commands.EnableStaffCommands = bv; restart = true; break;
            case "commands.restartenabled": cfg.Commands.EnableRestartCommands = bv; restart = true; break;
            case "commands.linkenabled": cfg.Commands.EnableLinkCommands = bv; restart = true; break;
            default:
                if (TrySetRoute(key, tree, cfg, out message)) return true;
                message = $"Unknown Discord Integration setting '{key}'.";
                return false;
        }

        message = "Discord Integration setting saved.";
        return true;
    }

    private static bool TryNormalizeColor(string value, out string color)
    {
        value = (value ?? "").Trim();
        if (value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit))
        {
            color = value.ToUpperInvariant();
            return true;
        }

        color = "#5865F2";
        return false;
    }

    private static string NormalizeColor(string value)
        => TryNormalizeColor(value, out var color) ? color : "#5865F2";

    private static bool TrySetRoute(string key, ITreeAttribute tree, DiscordIntegrationConfig cfg, out string message)
    {
        string[] parts = key.Split('.', 3);
        if (parts.Length != 3 || !parts[0].Equals("event", StringComparison.OrdinalIgnoreCase))
        {
            message = "";
            return false;
        }

        DiscordIntegrationConfig.RoutedEvent? route = parts[1].ToLowerInvariant() switch
        {
            "playerjoin" => cfg.Events.PlayerJoin,
            "playerleave" => cfg.Events.PlayerLeave,
            "playerdeath" => cfg.Events.PlayerDeath,
            "serverstarted" => cfg.Events.ServerStarted,
            "serverstopping" => cfg.Events.ServerStopping,
            "discordlinked" => cfg.Events.DiscordLinked,
            "discordunlinked" => cfg.Events.DiscordUnlinked,
            _ => null
        };
        if (route == null)
        {
            message = "Unknown Discord announcement type.";
            return false;
        }

        switch (parts[2].ToLowerInvariant())
        {
            case "enabled": route.Enabled = tree.GetBool("boolValue", false); break;
            case "channelid":
                if (!TryUlong(tree.GetString("stringValue", "").Trim(), out var channel))
                {
                    message = "Announcement channel ID must be a valid Discord snowflake (0 inherits the main chat channel).";
                    return false;
                }
                route.ChannelId = channel;
                break;
            case "format": route.Format = tree.GetString("stringValue", ""); break;
            case "route":
                // WebPanel row save: update enabled/channel/format atomically so a UI refresh
                // cannot rehydrate one stale field between three separate requests.
                if (!TryUlong(tree.GetString("stringValue", "").Trim(), out var routeChannel))
                {
                    message = "Announcement channel ID must be a valid Discord snowflake (0 inherits the main chat channel).";
                    return false;
                }
                route.Enabled = tree.GetBool("boolValue", false);
                route.ChannelId = routeChannel;
                route.Format = tree.GetString("target", "");
                break;
            default:
                message = "Unknown Discord announcement field.";
                return false;
        }
        message = "Discord announcement setting saved.";
        return true;
    }

    private static bool TryUlong(string value, out ulong result)
    {
        if (string.IsNullOrWhiteSpace(value)) { result = 0; return true; }
        return ulong.TryParse(value, out result);
    }

    private static bool Fail(string value, out string message)
    {
        message = value;
        return false;
    }

    private void OnReloadConfig(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (!active) return;
        reloadConfig();
    }

    private static bool MeetsSeverity(string actual, string minimum)
    {
        static int Rank(string s) => s.ToLowerInvariant() switch
        {
            "info" => 0,
            "low" => 1,
            "medium" => 2,
            "high" => 3,
            "critical" => 4,
            _ => 2
        };
        return Rank(actual) >= Rank(minimum);
    }

    private static string Escape(string s) => s.Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`").Replace("\r", " ").Replace("\n", " ");
    private static string EscapeCode(string s) => s.Replace("`", "ˋ").Replace("\r", " ").Replace("\n", " ");
}
