using System;
using System.Collections.Generic;

namespace BlockLogger.Services;

public sealed class ChatDisplaySettingsRequest
{
	public bool ShowRoleBeforeName { get; set; }

	public bool BoldPlayerName { get; set; }

	public bool ShowTimestampBeforeMessage { get; set; }

	public string? ChatFormat { get; set; }

	public string TimestampFormat { get; set; } = "HH:mm";

	public bool UseUtcTimestamp { get; set; }

	public bool FormatGeneralChat { get; set; } = true;

	public bool FormatOtherChatGroups { get; set; }

	public List<int> AdditionalFormattedChatGroupIds { get; set; } = new List<int>();

	public bool? MarkdownEnabled { get; set; }

	public bool? MarkdownAllowColors { get; set; }

	public bool? MarkdownAllowLinks { get; set; }

	public List<string>? MarkdownAllowedRoleCodes { get; set; }

	public bool UseRoleNameColors { get; set; }

	public string? RoleColorRevision { get; set; }

	public Dictionary<string, string> RoleNameColors { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
