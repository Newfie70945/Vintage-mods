using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace BlockLogger.Integrations;

public sealed class DiscordControlSnapshot
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public bool TransportAvailable { get; set; }
    public bool TransportEnabled { get; set; }
    public bool TransportConnected { get; set; }
    public bool BotTokenConfigured { get; set; }
    public string BotName { get; set; } = "";
    public string GuildId { get; set; } = "";
    public string InviteUrl { get; set; } = "";
    public bool ChatEnabled { get; set; }
    public string ChatChannelId { get; set; } = "";
    public bool GameToDiscord { get; set; }
    public bool DiscordToGame { get; set; }
    public bool GameBoldSenderLabel { get; set; } = true;
    public bool GameUseRoleColor { get; set; }
    public string DiscordNameColor { get; set; } = "#5865F2";
    public bool DiscordShowDcTag { get; set; }
    public bool DiscordBoldSenderName { get; set; }
    public bool MotdEnabled { get; set; }
    public string MotdAnnouncementsChannelId { get; set; } = "0";
    public string MotdUpdatesChannelId { get; set; } = "0";
    public string MotdEventsChannelId { get; set; } = "0";
    public int MotdMaxItemsPerFeed { get; set; } = 8;
    public bool AdminAuditEnabled { get; set; }
    public string AdminAuditChannelId { get; set; } = "";
    public bool AntiCheatDiscordEnabled { get; set; }
    public string AntiCheatChannelId { get; set; } = "";
    public bool CommandsEnabled { get; set; }
    public bool PlayerInfoCommandsEnabled { get; set; }
    public bool StaffCommandsEnabled { get; set; }
    public bool RestartCommandsEnabled { get; set; }
    public bool LinkCommandsEnabled { get; set; }
    public string Channels { get; set; } = "";

    public DiscordAnnouncementSnapshot PlayerJoin { get; set; } = new();
    public DiscordAnnouncementSnapshot PlayerLeave { get; set; } = new();
    public DiscordAnnouncementSnapshot PlayerDeath { get; set; } = new();
    public DiscordAnnouncementSnapshot ServerStarted { get; set; } = new();
    public DiscordAnnouncementSnapshot ServerStopping { get; set; } = new();
    public DiscordAnnouncementSnapshot DiscordLinked { get; set; } = new();
    public DiscordAnnouncementSnapshot DiscordUnlinked { get; set; } = new();

    public bool LinkAvailable { get; set; }
    public bool LinkEnabled { get; set; }
    public int LinkCodeLength { get; set; }
    public int LinkCodeExpirySeconds { get; set; }
    public int LinkCodeCooldownSeconds { get; set; }
    public bool LinkAllowMultipleVs { get; set; }
    public bool LinkLogChanges { get; set; }
    public int LinkedAccountCount { get; set; }
    public int PendingLinkCodeCount { get; set; }
}

public sealed class DiscordAnnouncementSnapshot
{
    public bool Enabled { get; set; }
    public string ChannelId { get; set; } = "";
    public string Format { get; set; } = "";
}

public static class DiscordControlBridge
{
    public const string EventName = "newfiesse:discord-control";

    public static DiscordControlSnapshot Request(
        ICoreServerAPI api,
        string operation = "get",
        string key = "",
        bool boolValue = false,
        int intValue = 0,
        string stringValue = "",
        string target = "")
    {
        string op = operation ?? "get";
        var tree = new TreeAttribute();
        tree.SetString("operation", op);
        tree.SetString("key", key ?? "");
        tree.SetBool("boolValue", boolValue);
        tree.SetInt("intValue", intValue);
        tree.SetString("stringValue", stringValue ?? "");
        tree.SetString("target", target ?? "");
        tree.SetBool("success", op.Equals("get", StringComparison.OrdinalIgnoreCase));
        tree.SetString("message", "The unified Discord module is not installed or did not respond.");

        api.Event.PushEvent(EventName, tree);
        return FromTree(tree);
    }

    private static DiscordControlSnapshot FromTree(ITreeAttribute t)
    {
        return new DiscordControlSnapshot
        {
            Success = t.GetBool("success", false),
            Message = t.GetString("message", ""),
            TransportAvailable = t.GetBool("transportAvailable", false),
            TransportEnabled = t.GetBool("transportEnabled", false),
            TransportConnected = t.GetBool("transportConnected", false),
            BotTokenConfigured = t.GetBool("botTokenConfigured", false),
            BotName = t.GetString("botName", ""),
            GuildId = t.GetString("guildId", ""),
            InviteUrl = t.GetString("inviteUrl", ""),
            ChatEnabled = t.GetBool("chatEnabled", false),
            ChatChannelId = t.GetString("chatChannelId", ""),
            GameToDiscord = t.GetBool("gameToDiscord", false),
            DiscordToGame = t.GetBool("discordToGame", false),
            GameBoldSenderLabel = t.GetBool("gameBoldSenderLabel", true),
            GameUseRoleColor = t.GetBool("gameUseRoleColor", false),
            DiscordNameColor = t.GetString("discordNameColor", "#5865F2"),
            DiscordShowDcTag = t.GetBool("discordShowDcTag", false),
            DiscordBoldSenderName = t.GetBool("discordBoldSenderName", false),
            MotdEnabled = t.GetBool("motdEnabled", true),
            MotdAnnouncementsChannelId = t.GetString("motdAnnouncementsChannelId", "0"),
            MotdUpdatesChannelId = t.GetString("motdUpdatesChannelId", "0"),
            MotdEventsChannelId = t.GetString("motdEventsChannelId", "0"),
            MotdMaxItemsPerFeed = t.GetInt("motdMaxItemsPerFeed", 8),
            AdminAuditEnabled = t.GetBool("adminAuditEnabled", false),
            AdminAuditChannelId = t.GetString("adminAuditChannelId", ""),
            AntiCheatDiscordEnabled = t.GetBool("antiCheatDiscordEnabled", false),
            AntiCheatChannelId = t.GetString("antiCheatChannelId", ""),
            CommandsEnabled = t.GetBool("commandsEnabled", false),
            PlayerInfoCommandsEnabled = t.GetBool("playerInfoCommandsEnabled", false),
            StaffCommandsEnabled = t.GetBool("staffCommandsEnabled", false),
            RestartCommandsEnabled = t.GetBool("restartCommandsEnabled", false),
            LinkCommandsEnabled = t.GetBool("linkCommandsEnabled", false),
            Channels = t.GetString("channels", ""),
            PlayerJoin = Route(t, "playerJoin"),
            PlayerLeave = Route(t, "playerLeave"),
            PlayerDeath = Route(t, "playerDeath"),
            ServerStarted = Route(t, "serverStarted"),
            ServerStopping = Route(t, "serverStopping"),
            DiscordLinked = Route(t, "discordLinked"),
            DiscordUnlinked = Route(t, "discordUnlinked"),
            LinkAvailable = t.GetBool("linkAvailable", false),
            LinkEnabled = t.GetBool("linkEnabled", false),
            LinkCodeLength = t.GetInt("linkCodeLength", 8),
            LinkCodeExpirySeconds = t.GetInt("linkCodeExpirySeconds", 300),
            LinkCodeCooldownSeconds = t.GetInt("linkCodeCooldownSeconds", 30),
            LinkAllowMultipleVs = t.GetBool("linkAllowMultipleVs", false),
            LinkLogChanges = t.GetBool("linkLogChanges", false),
            LinkedAccountCount = t.GetInt("linkedAccountCount", 0),
            PendingLinkCodeCount = t.GetInt("pendingLinkCodeCount", 0)
        };
    }

    private static DiscordAnnouncementSnapshot Route(ITreeAttribute t, string prefix) => new()
    {
        Enabled = t.GetBool(prefix + "Enabled", false),
        ChannelId = t.GetString(prefix + "ChannelId", "0"),
        Format = t.GetString(prefix + "Format", "")
    };
}
