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
