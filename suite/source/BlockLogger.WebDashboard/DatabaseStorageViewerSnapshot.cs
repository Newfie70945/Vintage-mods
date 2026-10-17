using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class DatabaseStorageViewerSnapshot
{
	public long CapturedUnixMilliseconds { get; set; }

	public bool DetailedAnalysisAvailable { get; set; }

	public long DetailedAnalysisUnixMilliseconds { get; set; }

	public string Message { get; set; } = "";

	public List<DatabaseStorageDatabaseSnapshot> Databases { get; set; } = new List<DatabaseStorageDatabaseSnapshot>();

	public List<DatabaseStorageRecommendation> Recommendations { get; set; } = new List<DatabaseStorageRecommendation>();
}
