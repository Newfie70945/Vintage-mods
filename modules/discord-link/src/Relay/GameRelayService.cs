using System.Net;
using Newfies.ChatProtocol;
using NewfiesServerEssentials.Discord.Config;
using NewfiesServerEssentials.Discord.Discord;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace NewfiesServerEssentials.Discord.Relay;

public sealed class GameRelayService
{
    private readonly ICoreServerAPI sapi;
    private readonly DiscordBridgeService discord;
    private readonly Func<DiscordIntegrationConfig> getConfig;
    private bool active;
    private bool suiteShowRole;
    private bool suiteBoldPlayerName;
    private long suiteChatRefreshListenerId = -1;

    private const string SuiteChatDisplayQueryEvent = "newfiesse:chat-display-query";
    private const string SuiteChatDisplayChangedEvent = "newfiesse:chat-display-changed";

    public GameRelayService(ICoreServerAPI sapi, DiscordBridgeService discord, Func<DiscordIntegrationConfig> getConfig)
    {
        this.sapi = sapi;
        this.discord = discord;
        this.getConfig = getConfig;
    }

    public void Register()
    {
        active = true;
        sapi.Event.PlayerChat += OnPlayerChat;
        sapi.Event.PlayerNowPlaying += OnPlayerJoin;
        sapi.Event.PlayerDisconnect += OnPlayerDisconnect;
        sapi.Event.PlayerDeath += OnPlayerDeath;
        sapi.Event.RegisterEventBusListener(OnSuiteChatDisplayChanged, filterByEventName: SuiteChatDisplayChangedEvent);

        // Query once at startup, then only on explicit change notifications. The slow periodic
        // refresh is a safety net for module load-order differences or an external config reload.
        RefreshSuiteChatDisplaySettings();
        suiteChatRefreshListenerId = sapi.Event.RegisterGameTickListener(_ => RefreshSuiteChatDisplaySettings(), 60_000, 5_000);
    }

    public void Unregister()
    {
        active = false;
        sapi.Event.PlayerChat -= OnPlayerChat;
        sapi.Event.PlayerNowPlaying -= OnPlayerJoin;
        sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
        sapi.Event.PlayerDeath -= OnPlayerDeath;
        if (suiteChatRefreshListenerId >= 0)
        {
            try { sapi.Event.UnregisterGameTickListener(suiteChatRefreshListenerId); } catch { }
            suiteChatRefreshListenerId = -1;
        }
    }

    private void OnPlayerChat(IServerPlayer player, int channelId, ref string message, ref string data, BoolRef consumed)
    {
        if(player==null || consumed.value)return;
        var cfg = getConfig();
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(message)) return;
        var boundary=ChatMessageBoundary.Parse(message,player.PlayerName,ResolveChatDisplayName(player),player.Role?.Name);

        if (boundary.IsCommand)
        {
            if (cfg.AdminAudit.Enabled && cfg.AdminAudit.ObserveSlashCommands && IsAdministrative(player))
            {
                RelayObservedCommand(player, boundary.Body);
            }
            return;
        }

        if (!cfg.Chat.Enabled || !cfg.Chat.GameToDiscord || cfg.Chat.ChannelId == 0) return;
        if (channelId != 0) return;

        // Vintage Story 1.22.x can pass PlayerChat.message as
        // <strong>PlayerName:</strong> actual message. Strip the engine-owned
        // sender prefix before relaying so Discord does not receive the literal
        // VTML or a duplicated player name.
        string chatBody = boundary.HasSender?boundary.Body:ExtractPlayerChatBody(message, player.PlayerName);
        var presentation = new TreeAttribute();
        presentation.SetString("playerUid", player.PlayerUID);
        presentation.SetInt("channelId", channelId);
        presentation.SetString("rawLine", message);
        presentation.SetString("body", chatBody);
        try { sapi.Event.PushEvent("newfiesse:chat-presentation-query", presentation); }
        catch { return; } // A failed moderation/presentation hook must not leak a message to Discord.
        if (presentation.GetBool("blocked", false)) return;
        if (presentation.GetBool("available", false)) chatBody = presentation.GetString("visibleText", chatBody);
        var clean = SanitizeGameToDiscord(chatBody, cfg.Safety.MaxGameToDiscordLength, cfg.Safety.StripEveryoneHereMentions);

        string displayName = EscapeDiscord(player.PlayerName);
        var suiteChat = GetSuiteChatDisplaySettings();
        if (suiteChat.ShowRole)
        {
            string roleName = player.Role?.Name ?? player.Role?.Code ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(roleName))
                displayName = $"[{EscapeDiscord(roleName.Trim())}] {displayName}";
        }

        // The Discord-side sender label has its own explicit toggle. Keep honoring
        // the Suite BoldPlayerName setting as a compatibility fallback, but do not
        // require it for Discord styling. New installs/configs default this ON.
        if (cfg.Chat.BoldGameSenderLabel || suiteChat.BoldPlayerName)
            displayName = $"**{displayName}**";

        // Historical stock **{player}** templates are normalized so the explicit
        // sender-label toggle remains authoritative and cannot double-wrap Markdown.
        string relayFormat = (cfg.Chat.GameToDiscordFormat ?? "{player}: {message}")
            .Replace("**{player}**", "{player}", StringComparison.Ordinal);
        var formatted = relayFormat
            .Replace("{player}", displayName)
            .Replace("{message}", clean);

        if (cfg.Chat.UseGameRoleColor && player.Role != null)
        {
            var c = player.Role.Color;
            discord.EnqueueRoleColorEmbed(cfg.Chat.ChannelId, formatted, c.R, c.G, c.B);
        }
        else
        {
            discord.Enqueue(cfg.Chat.ChannelId, formatted);
        }
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        var e = getConfig().Events.PlayerJoin;
        int online = CountOnlineIncluding(player);
        RelayEvent(e, ApplyPlayerCountPlaceholders(e.Format, player, online));
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        var e = getConfig().Events.PlayerLeave;
        int online = CountOnlineExcluding(player);
        RelayEvent(e, ApplyPlayerCountPlaceholders(e.Format, player, online));
    }

    private void OnPlayerDeath(IServerPlayer player, DamageSource damageSource)
    {
        var e = getConfig().Events.PlayerDeath;
        var source = damageSource?.Source.ToString() ?? "Unknown";
        var msg = e.Format
            .Replace("{player}", EscapeDiscord(player.PlayerName))
            .Replace("{source}", EscapeDiscord(source));
        RelayEvent(e, msg);
    }

    private void RelayEvent(DiscordIntegrationConfig.RoutedEvent e, string message)
    {
        var cfg = getConfig();
        if (!cfg.Enabled || !e.Enabled) return;
        var channel = e.ChannelId == 0 ? cfg.Chat.ChannelId : e.ChannelId;
        discord.Enqueue(channel, message);
    }

    private void RelayObservedCommand(IServerPlayer player, string raw)
    {
        var cfg = getConfig();
        var commandName = raw.TrimStart('/').Split(' ', 2)[0].ToLowerInvariant();
        var output = cfg.AdminAudit.RedactedCommands.Any(c => c.Equals(commandName, StringComparison.OrdinalIgnoreCase))
            ? $"/{commandName} [REDACTED]"
            : (cfg.AdminAudit.IncludeArguments ? raw : $"/{commandName}");

        var channel = cfg.AdminAudit.ChannelId == 0 ? cfg.Chat.ChannelId : cfg.AdminAudit.ChannelId;
        discord.Enqueue(channel,
            $"🔨 **ADMIN AUDIT**\n**Admin:** {EscapeDiscord(player.PlayerName)}\n**Command observed:** `{EscapeCode(output)}`\n**Role:** {EscapeDiscord(player.Role?.Code ?? "unknown")}\n**Source:** In game");
    }

    private (bool ShowRole, bool BoldPlayerName) GetSuiteChatDisplaySettings()
        => (suiteShowRole, suiteBoldPlayerName);

    private void OnSuiteChatDisplayChanged(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (!active) return;
        if (data is ITreeAttribute tree)
        {
            suiteShowRole = tree.GetBool("showRoleBeforeName", false);
            suiteBoldPlayerName = tree.GetBool("boldPlayerName", false);
            return;
        }

        RefreshSuiteChatDisplaySettings();
    }

    private void RefreshSuiteChatDisplaySettings()
    {
        if (!active) return;
        try
        {
            var tree = new TreeAttribute();
            tree.SetBool("available", false);
            sapi.Event.PushEvent(SuiteChatDisplayQueryEvent, tree);
            if (!tree.GetBool("available", false))
            {
                suiteShowRole = false;
                suiteBoldPlayerName = false;
                return;
            }

            suiteShowRole = tree.GetBool("showRoleBeforeName", false);
            suiteBoldPlayerName = tree.GetBool("boldPlayerName", false);
        }
        catch
        {
            // Server Suite is optional. If it is absent or cannot answer, Discord chat
            // remains name-only and unbolded without adding work to every chat event.
            suiteShowRole = false;
            suiteBoldPlayerName = false;
        }
    }

    private string ApplyPlayerCountPlaceholders(string format, IServerPlayer player, int online)
    {
        return (format ?? string.Empty)
            .Replace("{player}", EscapeDiscord(player.PlayerName))
            .Replace("{online}", Math.Max(0, online).ToString())
            .Replace("{max}", Math.Max(0, sapi.Server.Config.MaxClients).ToString());
    }

    private int CountOnlineIncluding(IServerPlayer player)
    {
        var players = sapi.World.AllOnlinePlayers;
        bool alreadyIncluded = players.Any(p => p != null && string.Equals(p.PlayerUID, player.PlayerUID, StringComparison.Ordinal));
        return players.Length + (alreadyIncluded ? 0 : 1);
    }

    private int CountOnlineExcluding(IServerPlayer player)
    {
        return sapi.World.AllOnlinePlayers.Count(p => p != null && !string.Equals(p.PlayerUID, player.PlayerUID, StringComparison.Ordinal));
    }

    private string ResolveChatDisplayName(IServerPlayer player)
    {
        var tree=new TreeAttribute();tree.SetString("playerUid",player.PlayerUID);
        try {sapi.Event.PushEvent("newfiesse:chat-sender-query",tree);}
        catch {return player.PlayerName;}
        return tree.GetBool("available",false)?tree.GetString("displayName",player.PlayerName):player.PlayerName;
    }

    private static string ExtractPlayerChatBody(string message, string? playerName)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;

        string value = message.TrimStart();

        // Supporter/name-color formatting may wrap the normal VS sender prefix:
        // <font color="#FEC500"><strong>Name:</strong></font> body
        // Strip the complete sender wrapper before Discord sanitization so VTML never
        // leaks into the Discord message body or duplicates the player's name.
        if (TryExtractColoredSenderPrefix(value, playerName, out string coloredBody))
            return coloredBody;

        const string strongOpen = "<strong>";
        const string strongClose = "</strong>";

        if (value.StartsWith(strongOpen, StringComparison.OrdinalIgnoreCase))
        {
            int close = value.IndexOf(strongClose, StringComparison.OrdinalIgnoreCase);
            if (close >= 0)
            {
                string sender = WebUtility.HtmlDecode(value[strongOpen.Length..close]).Trim();
                if (SenderMatchesPlayer(sender, playerName))
                {
                    int bodyStart = close + strongClose.Length;
                    return bodyStart < value.Length ? value[bodyStart..].TrimStart() : string.Empty;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(playerName))
        {
            string plainPrefix = playerName.Trim() + ":";
            if (value.StartsWith(plainPrefix, StringComparison.OrdinalIgnoreCase))
                return value[plainPrefix.Length..].TrimStart();
        }

        return value;
    }

    private static bool TryExtractColoredSenderPrefix(string value, string? playerName, out string body)
    {
        body = string.Empty;
        if (!value.StartsWith("<font", StringComparison.OrdinalIgnoreCase)) return false;

        int fontOpenEnd = value.IndexOf('>');
        if (fontOpenEnd < 0) return false;
        int fontClose = value.IndexOf("</font>", fontOpenEnd + 1, StringComparison.OrdinalIgnoreCase);
        if (fontClose < 0) return false;

        string inner = value[(fontOpenEnd + 1)..fontClose].Trim();
        const string strongOpen = "<strong>";
        const string strongClose = "</strong>";
        if (!inner.StartsWith(strongOpen, StringComparison.OrdinalIgnoreCase)) return false;
        int strongEnd = inner.IndexOf(strongClose, StringComparison.OrdinalIgnoreCase);
        if (strongEnd < 0) return false;
        if (!string.IsNullOrWhiteSpace(inner[(strongEnd + strongClose.Length)..])) return false;

        string sender = WebUtility.HtmlDecode(inner[strongOpen.Length..strongEnd]).Trim();
        if (!SenderMatchesPlayer(sender, playerName)) return false;

        int bodyStart = fontClose + "</font>".Length;
        body = bodyStart < value.Length ? value[bodyStart..].TrimStart() : string.Empty;
        return true;
    }

    private static bool SenderMatchesPlayer(string senderWithColon, string? playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return false;
        string sender = senderWithColon.Trim();
        if (sender.EndsWith(':')) sender = sender[..^1].TrimEnd();
        return sender.Equals(playerName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAdministrative(IServerPlayer player)
    {
        // Server Essentials can publish authoritative audit events for exact command results.
        // This fallback catches commands typed by users with elevated server privileges.
        var privileges = player.Privileges ?? Array.Empty<string>();
        return privileges.Contains("controlserver") || privileges.Contains("commandplayer") || privileges.Contains("gamemode") ||
               string.Equals(player.Role?.Code, "admin", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeGameToDiscord(string value, int maxLen, bool stripMassMentions)
    {
        value = value.Replace("\r", " ").Replace("\n", " ").Trim();
        if (stripMassMentions)
            value = value.Replace("@everyone", "@ everyone", StringComparison.OrdinalIgnoreCase)
                         .Replace("@here", "@ here", StringComparison.OrdinalIgnoreCase);
        value = EscapeDiscord(value);
        if (value.Length > Math.Max(50, maxLen)) value = value[..Math.Max(50, maxLen)] + "…";
        return value;
    }

    private static string EscapeDiscord(string s) => s.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`");
    private static string EscapeCode(string s) => s.Replace("`", "ˋ").Replace("\r", " ").Replace("\n", " ");
}
