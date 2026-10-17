using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace BlockLogger.Integrations;

public static class ChatGuardControlBridge
{
	public const string EventName = "newfieschatguard:control";

	public static ChatGuardControlSnapshot Request(ICoreServerAPI api, string operation = "get", string configJson = "", string action = "", string targetUid = "", string targetName = "", int minutes = 10, bool permanent = false, string reason = "", string actor = "Server Suite", string testMessage = "")
	{
		TreeAttribute treeAttribute = new TreeAttribute();
		treeAttribute.SetString("operation", operation ?? "get");
		treeAttribute.SetString("configJson", configJson ?? "");
		treeAttribute.SetString("action", action ?? "");
		treeAttribute.SetString("targetUid", targetUid ?? "");
		treeAttribute.SetString("targetName", targetName ?? "");
		treeAttribute.SetInt("minutes", minutes);
		treeAttribute.SetBool("permanent", permanent);
		treeAttribute.SetString("reason", reason ?? "");
		treeAttribute.SetString("actor", actor ?? "Server Suite");
		treeAttribute.SetString("testMessage", testMessage ?? "");
		treeAttribute.SetBool("available", value: false);
		treeAttribute.SetBool("success", value: false);
		treeAttribute.SetString("message", "Newfie's ChatGuard is not installed or did not respond.");
		api.Event.PushEvent("newfieschatguard:control", treeAttribute);
		return FromTree(treeAttribute);
	}

	public static ChatGuardModerationDecision Moderate(ICoreServerAPI api, IServerPlayer player, int channelId, string message)
	{
		TreeAttribute treeAttribute = new TreeAttribute();
		treeAttribute.SetString("operation", "moderate");
		treeAttribute.SetString("playerUid", player?.PlayerUID ?? "");
		treeAttribute.SetString("playerName", player?.PlayerName ?? "");
		treeAttribute.SetInt("channelId", channelId);
		treeAttribute.SetString("message", message ?? "");
		treeAttribute.SetBool("available", value: false);
		treeAttribute.SetBool("success", value: false);
		treeAttribute.SetBool("blocked", value: false);
		api.Event.PushEvent("newfieschatguard:control", treeAttribute);
		return new ChatGuardModerationDecision
		{
			Available = treeAttribute.GetBool("available"),
			Success = treeAttribute.GetBool("success"),
			Blocked = treeAttribute.GetBool("blocked"),
			Rule = treeAttribute.GetString("rule", ""),
			Action = treeAttribute.GetString("action", "Allow"),
			Detail = treeAttribute.GetString("detail", "")
		};
	}

	private static ChatGuardControlSnapshot FromTree(ITreeAttribute tree)
	{
		return new ChatGuardControlSnapshot
		{
			StateStorageReady = tree.GetBool("stateStorageReady", defaultValue: true),
			StateStorageError = tree.GetString("stateStorageError", ""),
			AuditDroppedEntries = tree.GetLong("auditDroppedEntries", 0L),
			AuditStorageError = tree.GetString("auditStorageError", ""),
			RetainedSamplePlayers = tree.GetInt("retainedSamplePlayers"),
			Available = tree.GetBool("available"),
			Success = tree.GetBool("success"),
			Message = tree.GetString("message", ""),
			Version = tree.GetString("version", ""),
			Enabled = tree.GetBool("enabled"),
			Mode = tree.GetString("mode", ""),
			SuiteIntegrated = tree.GetBool("suiteIntegrated"),
			ScannedMessages = tree.GetLong("scannedMessages", 0L),
			MatchedRules = tree.GetLong("matchedRules", 0L),
			BlockedMessages = tree.GetLong("blockedMessages", 0L),
			WarningsIssued = tree.GetLong("warningsIssued", 0L),
			MutesIssued = tree.GetLong("mutesIssued", 0L),
			KicksIssued = tree.GetLong("kicksIssued", 0L),
			ActiveMutes = tree.GetInt("activeMutes"),
			ConfigJson = tree.GetString("configJson", "{}"),
			RecentJson = tree.GetString("recentJson", "[]"),
			MutesJson = tree.GetString("mutesJson", "[]"),
			WarningsJson = tree.GetString("warningsJson", "[]"),
			RuleStatsJson = tree.GetString("ruleStatsJson", "[]"),
			OnlinePlayersJson = tree.GetString("onlinePlayersJson", "[]"),
			TestJson = tree.GetString("testJson", "{}")
		};
	}
}
