using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class LogIssueView
{
	public string Signature { get; set; } = "";

	public string Title { get; set; } = "";

	public string Category { get; set; } = "";

	public string Impact { get; set; } = "Low";

	public int Occurrences { get; set; }

	public long FirstSeenUnixMilliseconds { get; set; }

	public long LastSeenUnixMilliseconds { get; set; }

	public string LikelyModId { get; set; } = "";

	public string LikelyModName { get; set; } = "";

	public string AttributionConfidence { get; set; } = "Low";

	public string Diagnosis { get; set; } = "";

	public string RecommendedFix { get; set; } = "";

	public string HealthCorrelation { get; set; } = "";

	public string ActivityState { get; set; } = "Unknown";

	public bool SeenInArchivedLogs { get; set; }

	public List<LogIssueSample> Samples { get; set; } = new List<LogIssueSample>();
}
