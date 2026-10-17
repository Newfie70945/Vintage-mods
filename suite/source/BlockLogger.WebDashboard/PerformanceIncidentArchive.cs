using System.Collections.Generic;
using BlockLogger.ServerHealth;

namespace BlockLogger.WebDashboard;

public sealed class PerformanceIncidentArchive
{
	public string Id { get; set; } = "";

	public long StartUnixMilliseconds { get; set; }

	public long EndUnixMilliseconds { get; set; }

	public long CapturedUnixMilliseconds { get; set; }

	public double DurationSeconds { get; set; }

	public string Severity { get; set; } = "";

	public string PrimaryCategory { get; set; } = "";

	public string Confidence { get; set; } = "";

	public string Evidence { get; set; } = "";

	public string NextAction { get; set; } = "";

	public string LikelyModId { get; set; } = "";

	public string LikelyModName { get; set; } = "";

	public string ModConfidence { get; set; } = "Low";

	public string ModEvidence { get; set; } = "";

	public string ModAttributionSource { get; set; } = "";

	public long ModProfileCompletedUnixMilliseconds { get; set; }

	public double WorstDelayMs { get; set; }

	public double MaxCpuPercent { get; set; }

	public List<ServerHealthSample> Before { get; set; } = new List<ServerHealthSample>();

	public List<ServerHealthSample> During { get; set; } = new List<ServerHealthSample>();

	public List<ServerHealthSample> After { get; set; } = new List<ServerHealthSample>();

	public DatabaseHealthSnapshot Database { get; set; } = new DatabaseHealthSnapshot();

	public WebServerDiagnosticsSnapshot WebServer { get; set; } = new WebServerDiagnosticsSnapshot();

	public List<WebAuditRecord> RecentAdminActions { get; set; } = new List<WebAuditRecord>();

	public List<IncidentCauseScore> CauseScores { get; set; } = new List<IncidentCauseScore>();
}
