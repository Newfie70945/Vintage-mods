using System.Collections.Generic;

namespace BlockLogger.Analytics;

public sealed class PlayerCountAnalyticsSnapshot
{
	public long GeneratedUnixMilliseconds { get; set; }

	public int CurrentPlayers { get; set; }

	public string ServerTimeZone { get; set; } = "Local";

	public int DailyRetentionDays { get; set; }

	public int HourlyRetentionDays { get; set; }

	public int TodayLow { get; set; }

	public int TodayHigh { get; set; }

	public double TodayAverage { get; set; }

	public List<PlayerCountDailyPoint> Daily { get; set; } = new List<PlayerCountDailyPoint>();

	public List<PlayerCountHourlyPoint> TodayHourly { get; set; } = new List<PlayerCountHourlyPoint>();

	public List<PlayerCountHourlyPoint> Last24Hours { get; set; } = new List<PlayerCountHourlyPoint>();
}
