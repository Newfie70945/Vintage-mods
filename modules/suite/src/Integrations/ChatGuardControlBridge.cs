using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace BlockLogger.Integrations;

public sealed class ChatGuardControlSnapshot
{
    public bool StateStorageReady { get; set; } = true;
    public string StateStorageError { get; set; } = "";
    public long AuditDroppedEntries { get; set; }
    public string AuditStorageError { get; set; } = "";
    public int RetainedSamplePlayers { get; set; }
    public bool Available { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "";
    public bool SuiteIntegrated { get; set; }
    public long ScannedMessages { get; set; }
    public long MatchedRules { get; set; }
    public long BlockedMessages { get; set; }
    public long WarningsIssued { get; set; }
    public long MutesIssued { get; set; }
    public long KicksIssued { get; set; }
    public int ActiveMutes { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public string RecentJson { get; set; } = "[]";
    public string MutesJson { get; set; } = "[]";
    public string WarningsJson { get; set; } = "[]";
    public string RuleStatsJson { get; set; } = "[]";
    public string OnlinePlayersJson { get; set; } = "[]";
    public string TestJson { get; set; } = "{}";
}

public sealed class ChatGuardModerationDecision
{
    public bool Available { get; set; }
    public bool Success { get; set; }
    public bool Blocked { get; set; }
    public string Rule { get; set; } = "";
    public string Action { get; set; } = "Allow";
    public string Detail { get; set; } = "";
}

public static class ChatGuardControlBridge
{
    public const string EventName = "newfieschatguard:control";

    public static ChatGuardControlSnapshot Request(
        ICoreServerAPI api,
        string operation = "get",
        string configJson = "",
        string action = "",
        string targetUid = "",
        string targetName = "",
        int minutes = 10,
        bool permanent = false,
        string reason = "",
        string actor = "Server Suite",
        string testMessage = "")
    {
        var tree = new TreeAttribute();
        tree.SetString("operation", operation ?? "get");
        tree.SetString("configJson", configJson ?? "");
        tree.SetString("action", action ?? "");
        tree.SetString("targetUid", targetUid ?? "");
        tree.SetString("targetName", targetName ?? "");
        tree.SetInt("minutes", minutes);
        tree.SetBool("permanent", permanent);
        tree.SetString("reason", reason ?? "");
        tree.SetString("actor", actor ?? "Server Suite");
        tree.SetString("testMessage", testMessage ?? "");
        tree.SetBool("available", false);
        tree.SetBool("success", false);
        tree.SetString("message", "Newfie's ChatGuard is not installed or did not respond.");
        api.Event.PushEvent(EventName, tree);
        return FromTree(tree);
    }

    public static ChatGuardModerationDecision Moderate(ICoreServerAPI api, IServerPlayer player, int channelId, string message)
    {
        var tree = new TreeAttribute();
        tree.SetString("operation", "moderate");
        tree.SetString("playerUid", player?.PlayerUID ?? "");
        tree.SetString("playerName", player?.PlayerName ?? "");
        tree.SetInt("channelId", channelId);
        tree.SetString("message", message ?? "");
        tree.SetBool("available", false);
        tree.SetBool("success", false);
        tree.SetBool("blocked", false);
        api.Event.PushEvent(EventName, tree);
        return new ChatGuardModerationDecision
        {
            Available = tree.GetBool("available", false),
            Success = tree.GetBool("success", false),
            Blocked = tree.GetBool("blocked", false),
            Rule = tree.GetString("rule", ""),
            Action = tree.GetString("action", "Allow"),
            Detail = tree.GetString("detail", "")
        };
    }

    private static ChatGuardControlSnapshot FromTree(ITreeAttribute tree) => new()
    {
        StateStorageReady = tree.GetBool("stateStorageReady", true),
        StateStorageError = tree.GetString("stateStorageError", ""),
        AuditDroppedEntries = tree.GetLong("auditDroppedEntries", 0),
        AuditStorageError = tree.GetString("auditStorageError", ""),
        RetainedSamplePlayers = tree.GetInt("retainedSamplePlayers", 0),
        Available = tree.GetBool("available", false),
        Success = tree.GetBool("success", false),
        Message = tree.GetString("message", ""),
        Version = tree.GetString("version", ""),
        Enabled = tree.GetBool("enabled", false),
        Mode = tree.GetString("mode", ""),
        SuiteIntegrated = tree.GetBool("suiteIntegrated", false),
        ScannedMessages = tree.GetLong("scannedMessages", 0),
        MatchedRules = tree.GetLong("matchedRules", 0),
        BlockedMessages = tree.GetLong("blockedMessages", 0),
        WarningsIssued = tree.GetLong("warningsIssued", 0),
        MutesIssued = tree.GetLong("mutesIssued", 0),
        KicksIssued = tree.GetLong("kicksIssued", 0),
        ActiveMutes = tree.GetInt("activeMutes", 0),
        ConfigJson = tree.GetString("configJson", "{}"),
        RecentJson = tree.GetString("recentJson", "[]"),
        MutesJson = tree.GetString("mutesJson", "[]"),
        WarningsJson = tree.GetString("warningsJson", "[]"),
        RuleStatsJson = tree.GetString("ruleStatsJson", "[]"),
        OnlinePlayersJson = tree.GetString("onlinePlayersJson", "[]"),
        TestJson = tree.GetString("testJson", "{}")
    };
}
