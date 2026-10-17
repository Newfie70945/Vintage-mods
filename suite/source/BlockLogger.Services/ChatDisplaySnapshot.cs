using System;
using System.Collections.Generic;

namespace BlockLogger.Services;

public sealed class ChatDisplaySnapshot
{
	public bool Available { get; set; } = true;

	public bool ShowRoleBeforeName { get; set; }

	public bool BoldPlayerName { get; set; }

	public bool ShowTimestampBeforeMessage { get; set; }

	public string ChatFormat { get; set; } = "{role} {name}: {timestamp} {message}";

	public string TimestampFormat { get; set; } = "HH:mm";

	public bool UseUtcTimestamp { get; set; }

	public bool FormatGeneralChat { get; set; } = true;

	public bool FormatOtherChatGroups { get; set; }

	public List<int> AdditionalFormattedChatGroupIds { get; set; } = new List<int>();

	public bool MarkdownEnabled { get; set; } = true;

	public bool MarkdownAllowColors { get; set; } = false;

	public bool MarkdownAllowLinks { get; set; } = false;

	public List<string> MarkdownAllowedRoleCodes { get; set; } = new List<string>();

	public bool UseRoleNameColors { get; set; }

	public string? RoleColorRevision { get; set; }

	public Dictionary<string, string> RoleNameColors { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	public string Preview { get; set; } = "Player: Hello world";

	public string ConfigFile { get; set; } = "NewfiesChatDisplay.json";
}
