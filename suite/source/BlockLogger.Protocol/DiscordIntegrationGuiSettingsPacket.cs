using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class DiscordIntegrationGuiSettingsPacket
{
	[ProtoMember(1)]
	public bool TransportAvailable { get; set; }

	[ProtoMember(2)]
	public bool TransportEnabled { get; set; }

	[ProtoMember(3)]
	public bool TransportConnected { get; set; }

	[ProtoMember(4)]
	public bool BotTokenConfigured { get; set; }

	[ProtoMember(5)]
	public string BotName { get; set; } = "";

	[ProtoMember(6)]
	public string GuildId { get; set; } = "";

	[ProtoMember(7)]
	public string InviteUrl { get; set; } = "";

	[ProtoMember(8)]
	public bool ChatEnabled { get; set; }

	[ProtoMember(9)]
	public string ChatChannelId { get; set; } = "0";

	[ProtoMember(10)]
	public bool GameToDiscord { get; set; }

	[ProtoMember(11)]
	public bool DiscordToGame { get; set; }

	[ProtoMember(12)]
	public bool AdminAuditEnabled { get; set; }

	[ProtoMember(13)]
	public string AdminAuditChannelId { get; set; } = "0";

	[ProtoMember(14)]
	public bool AntiCheatDiscordEnabled { get; set; }

	[ProtoMember(15)]
	public string AntiCheatChannelId { get; set; } = "0";

	[ProtoMember(16)]
	public bool LinkCommandsEnabled { get; set; }

	[ProtoMember(17)]
	public string Channels { get; set; } = "";

	[ProtoMember(18)]
	public DiscordAnnouncementGuiPacket PlayerJoin { get; set; } = new DiscordAnnouncementGuiPacket();

	[ProtoMember(19)]
	public DiscordAnnouncementGuiPacket PlayerLeave { get; set; } = new DiscordAnnouncementGuiPacket();

	[ProtoMember(20)]
	public DiscordAnnouncementGuiPacket PlayerDeath { get; set; } = new DiscordAnnouncementGuiPacket();

	[ProtoMember(21)]
	public DiscordAnnouncementGuiPacket ServerStarted { get; set; } = new DiscordAnnouncementGuiPacket();

	[ProtoMember(22)]
	public DiscordAnnouncementGuiPacket ServerStopping { get; set; } = new DiscordAnnouncementGuiPacket();

	[ProtoMember(23)]
	public DiscordAnnouncementGuiPacket DiscordLinked { get; set; } = new DiscordAnnouncementGuiPacket();

	[ProtoMember(24)]
	public DiscordAnnouncementGuiPacket DiscordUnlinked { get; set; } = new DiscordAnnouncementGuiPacket();

	[ProtoMember(25)]
	public bool LinkAvailable { get; set; }

	[ProtoMember(26)]
	public bool LinkEnabled { get; set; }

	[ProtoMember(27)]
	public int LinkCodeLength { get; set; } = 8;

	[ProtoMember(28)]
	public int LinkCodeExpirySeconds { get; set; } = 300;

	[ProtoMember(29)]
	public int LinkCodeCooldownSeconds { get; set; } = 30;

	[ProtoMember(30)]
	public bool LinkAllowMultipleVs { get; set; }

	[ProtoMember(31)]
	public bool LinkLogChanges { get; set; } = false;

	[ProtoMember(32)]
	public int LinkedAccountCount { get; set; }

	[ProtoMember(33)]
	public int PendingLinkCodeCount { get; set; }

	[ProtoMember(34)]
	public bool CommandsEnabled { get; set; }

	[ProtoMember(35)]
	public bool PlayerInfoCommandsEnabled { get; set; }

	[ProtoMember(36)]
	public bool StaffCommandsEnabled { get; set; }

	[ProtoMember(37)]
	public bool RestartCommandsEnabled { get; set; }

	[ProtoMember(38)]
	public bool GameBoldSenderLabel { get; set; } = true;

	[ProtoMember(39)]
	public bool GameUseRoleColor { get; set; }

	[ProtoMember(40)]
	public bool MotdAvailable { get; set; }

	[ProtoMember(41)]
	public string MotdVersion { get; set; } = "";

	[ProtoMember(42)]
	public string MotdConfigJson { get; set; } = "{}";

	[ProtoMember(43)]
	public string MotdMessage { get; set; } = "";
}
