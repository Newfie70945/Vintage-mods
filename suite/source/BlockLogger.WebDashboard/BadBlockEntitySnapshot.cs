using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class BadBlockEntitySnapshot
{
	public long GeneratedUnixMilliseconds { get; set; }

	public int Total { get; set; }

	public int Tracking { get; set; }

	public int Stopped { get; set; }

	public long TotalOccurrences { get; set; }

	public List<BadBlockEntityRecord> Entries { get; set; } = new List<BadBlockEntityRecord>();
}
