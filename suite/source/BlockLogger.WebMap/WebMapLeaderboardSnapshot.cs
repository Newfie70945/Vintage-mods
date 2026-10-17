using System;
using System.Collections.Generic;

namespace BlockLogger.WebMap;

public sealed class WebMapLeaderboardSnapshot
{
	public bool Enabled { get; set; }

	public long GeneratedUnixMilliseconds { get; set; }

	public int RefreshSeconds { get; set; } = 60;

	public int TopCount { get; set; } = 5;

	public int PlayerCount { get; set; }

	public int OfflinePlayerCount { get; set; }

	public int XSkillsPlayerCount { get; set; }

	public int XSkillsOfflinePlayerCount { get; set; }

	public bool PlaytimeEnabled { get; set; }

	public bool XSkillsEnabled { get; set; }

	public bool XSkillsDetected { get; set; }

	public string XSkillsStatus { get; set; } = "";

	public List<WebMapLeaderboardEntry> Playtime { get; set; } = new List<WebMapLeaderboardEntry>();

	public List<WebMapLeaderboardEntry> CurrentSession { get; set; } = new List<WebMapLeaderboardEntry>();

	public List<WebMapLeaderboardEntry> Veterans { get; set; } = new List<WebMapLeaderboardEntry>();

	public List<WebMapLeaderboardEntry> TotalLevels { get; set; } = new List<WebMapLeaderboardEntry>();

	public List<WebMapLeaderboardEntry> TotalExperience { get; set; } = new List<WebMapLeaderboardEntry>();

	public List<WebMapSkillLeaderboard> Skills { get; set; } = new List<WebMapSkillLeaderboard>();

	public List<WebMapLeaderboardPlayerProfile> Players { get; set; } = new List<WebMapLeaderboardPlayerProfile>();

	public static WebMapLeaderboardSnapshot Disabled(string status)
	{
		return new WebMapLeaderboardSnapshot
		{
			Enabled = false,
			GeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			XSkillsStatus = (status ?? "")
		};
	}
}
