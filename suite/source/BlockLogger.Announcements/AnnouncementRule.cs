using System;
using System.Collections.Generic;

namespace BlockLogger.Announcements;

public sealed class AnnouncementRule
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 10);

	public string Name { get; set; } = "Announcement";

	public bool Enabled { get; set; } = true;

	public string Message { get; set; } = "";

	public string Mode { get; set; } = "interval";

	public int IntervalMinutes { get; set; } = 60;

	public string StartTime { get; set; } = "";

	public List<string> DailyTimes { get; set; } = new List<string>();

	public int MaxRuns { get; set; } = 0;

	public int RunsCompleted { get; set; } = 0;

	public long LastRunUnixMilliseconds { get; set; }

	public long NextRunUnixMilliseconds { get; set; }
}
