using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatHistoryWebSnapshot : AntiCheatHistorySummaryWebSnapshot
{
	public bool StorageReady { get; set; }

	public string StorageError { get; set; } = "";

	public string PlayerFilter { get; set; } = "";

	public string FindingFilter { get; set; } = "";

	public List<string> FindingOptions { get; set; } = new List<string>();

	public List<AntiCheatIncidentWebSnapshot> Incidents { get; set; } = new List<AntiCheatIncidentWebSnapshot>();
}
