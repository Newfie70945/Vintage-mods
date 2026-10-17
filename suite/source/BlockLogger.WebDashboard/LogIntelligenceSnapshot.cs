using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class LogIntelligenceSnapshot
{
	public long GeneratedUnixMilliseconds { get; set; }

	public long LastScanUnixMilliseconds { get; set; }

	public long LastArchiveScanUnixMilliseconds { get; set; }

	public string CurrentLogPath { get; set; } = "";

	public long LiveLogBacklogBytes { get; set; }

	public long OversizedLogLines { get; set; }

	public int Score { get; set; }

	public string State { get; set; } = "Healthy";

	public int Critical { get; set; }

	public int High { get; set; }

	public int Moderate { get; set; }

	public int Low { get; set; }

	public int TotalOccurrences { get; set; }

	public int UniqueIssues { get; set; }

	public bool ScanInProgress { get; set; }

	public List<LogCategorySummary> Categories { get; set; } = new List<LogCategorySummary>();

	public List<LogIssueView> Issues { get; set; } = new List<LogIssueView>();

	public StartupPreflightSnapshot Preflight { get; set; } = new StartupPreflightSnapshot();

	public BadBlockEntitySnapshot BadBlockEntities { get; set; } = new BadBlockEntitySnapshot();
}
