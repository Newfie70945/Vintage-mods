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

	public DiscordAnnouncementSnapshot PlayerJoin { get; set; } = new DiscordAnnouncementSnapshot();

	public DiscordAnnouncementSnapshot PlayerLeave { get; set; } = new DiscordAnnouncementSnapshot();

	public DiscordAnnouncementSnapshot PlayerDeath { get; set; } = new DiscordAnnouncementSnapshot();

	public DiscordAnnouncementSnapshot ServerStarted { get; set; } = new DiscordAnnouncementSnapshot();

	public DiscordAnnouncementSnapshot ServerStopping { get; set; } = new DiscordAnnouncementSnapshot();

	public DiscordAnnouncementSnapshot DiscordLinked { get; set; } = new DiscordAnnouncementSnapshot();

	public DiscordAnnouncementSnapshot DiscordUnlinked { get; set; } = new DiscordAnnouncementSnapshot();

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
