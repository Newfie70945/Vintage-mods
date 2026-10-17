using System;
using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class LogIntelligencePersistedState
{
	public long LastScanUnixMilliseconds { get; set; }

	public long LastArchiveScanUnixMilliseconds { get; set; }

	public List<LogIssueState> Issues { get; set; } = new List<LogIssueState>();

	public List<string> ArchivedScanFingerprints { get; set; } = new List<string>();

	public Dictionary<string, long> FileOffsets { get; set; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
}
