using NewfiesServerEssentials.Discord.Linking;

namespace NewfiesServerEssentials.Discord.Config;

public sealed class DiscordIntegrationConfig
{
    public bool Enabled { get; set; } = false;
    public string BotToken { get; set; } = "";
    public ulong GuildId { get; set; }
    public string InviteUrl { get; set; } = "";

    public ChatBridgeConfig Chat { get; set; } = new();
    public EventRoutingConfig Events { get; set; } = new();
    public AdminAuditConfig AdminAudit { get; set; } = new();
    public AntiCheatDiscordConfig AntiCheat { get; set; } = new();
    public SafetyConfig Safety { get; set; } = new();
    public DiscordCommandsConfig Commands { get; set; } = new();
    public DiscordLinkConfig Linking { get; set; } = new();
    public MotdFeedConfig Motd { get; set; } = new();

    // Set after the unified module has checked for the legacy standalone NewfiesDiscordLink.json.
    public bool LegacyDiscordLinkConfigMigrationComplete { get; set; } = false;

    public sealed class ChatBridgeConfig
    {
        public bool Enabled { get; set; } = false;
        public ulong ChannelId { get; set; }
        public bool GameToDiscord { get; set; } = false;
        public bool DiscordToGame { get; set; } = false;
        public string GameToDiscordFormat { get; set; } = "{player}: {message}";
        public string DiscordToGameFormat { get; set; } = "{user}: {message}";
        // Game -> Discord sender styling. BoldGameSenderLabel defaults ON so the
        // Vintage Story role/name label renders as **[Role] Player** in Discord.
        // UseGameRoleColor renders the relay as a Discord embed whose accent bar
        // uses the player's actual Vintage Story role color. Discord does not
        // support arbitrary inline text colors in normal messages.
        public bool BoldGameSenderLabel { get; set; } = true;
        public bool UseGameRoleColor { get; set; } = false;

        // Applied only to the Discord sender label in Vintage Story chat.
        // The relayed message body intentionally stays at the normal chat color.
        public string DiscordNameColor { get; set; } = "#5865F2";
        public bool ShowDiscordTag { get; set; } = false;
        public bool BoldDiscordSenderName { get; set; } = false;
    }

    public sealed class MotdFeedConfig
    {
        public bool Enabled { get; set; } = true;
        public ulong AnnouncementsChannelId { get; set; }
        public ulong UpdatesChannelId { get; set; }
        public ulong EventsChannelId { get; set; }
        public int MaxItemsPerFeed { get; set; } = 8;
    }

    public sealed class EventRoutingConfig
    {
        public RoutedEvent PlayerJoin { get; set; } = new(false, 0, "📥 ***Player {player} connected ({online}/{max})***");
        public RoutedEvent PlayerLeave { get; set; } = new(false, 0, "📤 ***Player {player} disconnected ({online}/{max})***");
        public RoutedEvent PlayerDeath { get; set; } = new(false, 0, "☠️ **{message}**");
        public RoutedEvent TemporalStorm { get; set; } = new(false, 0, "🌀 **{message}**");
        public RoutedEvent ServerStarted { get; set; } = new(false, 0, "✅ **Server started**");
        public RoutedEvent ServerStopping { get; set; } = new(false, 0, "🛑 **Server stopping**");
        public RoutedEvent DiscordLinked { get; set; } = new(false, 0, "🔗 **{player} linked Discord account {discord}**");
        public RoutedEvent DiscordUnlinked { get; set; } = new(false, 0, "🔓 **{player} unlinked Discord account {discord}**");
    }

    public sealed class RoutedEvent
    {
        public RoutedEvent() { }
        public RoutedEvent(bool enabled, ulong channelId, string format)
        {
            Enabled = enabled;
            ChannelId = channelId;
            Format = format;
        }

        public bool Enabled { get; set; } = false;
        // 0 = inherit Chat.ChannelId
        public ulong ChannelId { get; set; }
        public string Format { get; set; } = "{message}";
    }

    public sealed class AdminAuditConfig
    {
        public bool Enabled { get; set; } = false;
        public ulong ChannelId { get; set; }
        public bool ObserveSlashCommands { get; set; } = false;
        public bool IncludeArguments { get; set; } = false;
        public string[] RedactedCommands { get; set; } = ["auth", "login", "token", "password"];
        public string MinimumRoleCode { get; set; } = "admin";
    }

    public sealed class AntiCheatDiscordConfig
    {
        public bool Enabled { get; set; } = false;
        public ulong ChannelId { get; set; }
        public string MinimumSeverity { get; set; } = "Medium";
        public bool IncludePosition { get; set; } = false;
        public bool IncludeEvidenceSummary { get; set; } = false;
        public bool PingOnCritical { get; set; } = false;
        public ulong CriticalRoleId { get; set; }
    }


    public sealed class DiscordCommandsConfig
    {
        public bool Enabled { get; set; } = false;
        public bool EnablePlayerInfoCommands { get; set; } = false;
        public bool EnableStaffCommands { get; set; } = false;
        public bool EnableRestartCommands { get; set; } = false;
        public bool EnableLinkCommands { get; set; } = false;
        public bool AllowDiscordAdministrators { get; set; } = false;
        public ulong[] StaffRoleIds { get; set; } = [];
        public string RulesText { get; set; } = "See the server rules in #rules.";
    }

    public sealed class SafetyConfig
    {
        public int MaxDiscordToGameLength { get; set; } = 500;
        public int MaxGameToDiscordLength { get; set; } = 1800;
        public bool StripEveryoneHereMentions { get; set; } = true;
        public bool IgnoreBots { get; set; } = true;
        public int OutboundQueueCapacity { get; set; } = 500;
    }
}
