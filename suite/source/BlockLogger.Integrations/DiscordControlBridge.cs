using System;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace BlockLogger.Integrations;

public static class DiscordControlBridge
{
	public const string EventName = "newfiesse:discord-control";

	public static DiscordControlSnapshot Request(ICoreServerAPI api, string operation = "get", string key = "", bool boolValue = false, int intValue = 0, string stringValue = "", string target = "")
	{
		string text = operation ?? "get";
		TreeAttribute treeAttribute = new TreeAttribute();
		treeAttribute.SetString("operation", text);
		treeAttribute.SetString("key", key ?? "");
		treeAttribute.SetBool("boolValue", boolValue);
		treeAttribute.SetInt("intValue", intValue);
		treeAttribute.SetString("stringValue", stringValue ?? "");
		treeAttribute.SetString("target", target ?? "");
		treeAttribute.SetBool("success", text.Equals("get", StringComparison.OrdinalIgnoreCase));
		treeAttribute.SetString("message", "The unified Discord module is not installed or did not respond.");
		api.Event.PushEvent("newfiesse:discord-control", treeAttribute);
		return FromTree(treeAttribute);
	}

	private static DiscordControlSnapshot FromTree(ITreeAttribute t)
	{
		return new DiscordControlSnapshot
		{
			Success = t.GetBool("success"),
			Message = t.GetString("message", ""),
			TransportAvailable = t.GetBool("transportAvailable"),
			TransportEnabled = t.GetBool("transportEnabled"),
			TransportConnected = t.GetBool("transportConnected"),
			BotTokenConfigured = t.GetBool("botTokenConfigured"),
			BotName = t.GetString("botName", ""),
			GuildId = t.GetString("guildId", ""),
			InviteUrl = t.GetString("inviteUrl", ""),
			ChatEnabled = t.GetBool("chatEnabled"),
			ChatChannelId = t.GetString("chatChannelId", ""),
			GameToDiscord = t.GetBool("gameToDiscord"),
			DiscordToGame = t.GetBool("discordToGame"),
			GameBoldSenderLabel = t.GetBool("gameBoldSenderLabel", defaultValue: true),
			GameUseRoleColor = t.GetBool("gameUseRoleColor"),
			DiscordNameColor = t.GetString("discordNameColor", "#5865F2"),
			DiscordShowDcTag = t.GetBool("discordShowDcTag"),
			DiscordBoldSenderName = t.GetBool("discordBoldSenderName"),
			MotdEnabled = t.GetBool("motdEnabled", defaultValue: true),
			MotdAnnouncementsChannelId = t.GetString("motdAnnouncementsChannelId", "0"),
			MotdUpdatesChannelId = t.GetString("motdUpdatesChannelId", "0"),
			MotdEventsChannelId = t.GetString("motdEventsChannelId", "0"),
			MotdMaxItemsPerFeed = t.GetInt("motdMaxItemsPerFeed", 8),
			AdminAuditEnabled = t.GetBool("adminAuditEnabled"),
			AdminAuditChannelId = t.GetString("adminAuditChannelId", ""),
			AntiCheatDiscordEnabled = t.GetBool("antiCheatDiscordEnabled"),
			AntiCheatChannelId = t.GetString("antiCheatChannelId", ""),
			CommandsEnabled = t.GetBool("commandsEnabled"),
			PlayerInfoCommandsEnabled = t.GetBool("playerInfoCommandsEnabled"),
			StaffCommandsEnabled = t.GetBool("staffCommandsEnabled"),
			RestartCommandsEnabled = t.GetBool("restartCommandsEnabled"),
			LinkCommandsEnabled = t.GetBool("linkCommandsEnabled"),
			Channels = t.GetString("channels", ""),
			PlayerJoin = Route(t, "playerJoin"),
			PlayerLeave = Route(t, "playerLeave"),
			PlayerDeath = Route(t, "playerDeath"),
			ServerStarted = Route(t, "serverStarted"),
			ServerStopping = Route(t, "serverStopping"),
			DiscordLinked = Route(t, "discordLinked"),
			DiscordUnlinked = Route(t, "discordUnlinked"),
			LinkAvailable = t.GetBool("linkAvailable"),
			LinkEnabled = t.GetBool("linkEnabled"),
			LinkCodeLength = t.GetInt("linkCodeLength", 8),
			LinkCodeExpirySeconds = t.GetInt("linkCodeExpirySeconds", 300),
			LinkCodeCooldownSeconds = t.GetInt("linkCodeCooldownSeconds", 30),
			LinkAllowMultipleVs = t.GetBool("linkAllowMultipleVs"),
			LinkLogChanges = t.GetBool("linkLogChanges"),
			LinkedAccountCount = t.GetInt("linkedAccountCount"),
			PendingLinkCodeCount = t.GetInt("pendingLinkCodeCount")
		};
	}

	private static DiscordAnnouncementSnapshot Route(ITreeAttribute t, string prefix)
	{
		return new DiscordAnnouncementSnapshot
		{
			Enabled = t.GetBool(prefix + "Enabled"),
			ChannelId = t.GetString(prefix + "ChannelId", "0"),
			Format = t.GetString(prefix + "Format", "")
		};
	}
}
