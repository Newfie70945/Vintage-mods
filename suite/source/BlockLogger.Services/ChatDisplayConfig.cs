using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public sealed class ChatDisplayConfig
{
	public const string ConfigFile = "NewfiesChatDisplay.json";

	public const int CurrentSchemaVersion = 4;

	public int SchemaVersion { get; set; } = 4;

	public bool ShowRoleBeforeName { get; set; } = false;

	public bool BoldPlayerName { get; set; } = true;

	public bool ShowTimestampBeforeMessage { get; set; } = false;

	public string ChatFormat { get; set; } = "{role} {name}: {timestamp} {message}";

	public string TimestampFormat { get; set; } = "HH:mm";

	public bool UseUtcTimestamp { get; set; } = false;

	public bool FormatGeneralChat { get; set; } = true;

	public bool FormatOtherChatGroups { get; set; } = false;

	public List<int> AdditionalFormattedChatGroupIds { get; set; } = new List<int>();

	public bool MarkdownEnabled { get; set; } = true;

	public bool MarkdownAllowColors { get; set; } = false;

	public bool MarkdownAllowLinks { get; set; } = false;

	public List<string> MarkdownAllowedRoleCodes { get; set; } = new List<string>();

	public void Clamp()
	{
		SchemaVersion = Math.Max(4, SchemaVersion);
		if (ChatLayout.Validate(ChatFormat) != null)
		{
			ChatFormat = "{role} {name}: {timestamp} {message}";
		}
		TimestampFormat = (string.IsNullOrWhiteSpace(TimestampFormat) ? "HH:mm" : TimestampFormat.Trim());
		if (TimestampFormat.Length > 40)
		{
			TimestampFormat = TimestampFormat.Substring(0, 40);
		}
		try
		{
			DateTime.Now.ToString(TimestampFormat);
		}
		catch
		{
			TimestampFormat = "HH:mm";
		}
		if (MarkdownAllowedRoleCodes == null)
		{
			List<string> list = (MarkdownAllowedRoleCodes = new List<string>());
		}
		MarkdownAllowedRoleCodes = (from x in MarkdownAllowedRoleCodes
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim() into x
			where x.Length <= 80
			select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList();
		if (AdditionalFormattedChatGroupIds == null)
		{
			List<int> list3 = (AdditionalFormattedChatGroupIds = new List<int>());
		}
		AdditionalFormattedChatGroupIds = (from id in AdditionalFormattedChatGroupIds.Where((int id) => id >= 0 && id != GlobalConstants.GeneralChatGroup).Distinct()
			orderby id
			select id).Take(64).ToList();
	}

	public static ChatDisplayConfig Load(ICoreServerAPI api)
	{
		ChatDisplayConfig chatDisplayConfig = ModConfigRecovery.LoadOrNull<ChatDisplayConfig>(api, "NewfiesChatDisplay.json", "ChatDisplay") ?? new ChatDisplayConfig();
		chatDisplayConfig.Clamp();
		SuiteConfigStorage.Store(api, chatDisplayConfig, "NewfiesChatDisplay.json");
		return chatDisplayConfig;
	}
}
