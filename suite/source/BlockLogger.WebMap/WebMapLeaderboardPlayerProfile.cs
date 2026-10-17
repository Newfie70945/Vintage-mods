using System.Collections.Generic;

namespace BlockLogger.WebMap;

public sealed class WebMapLeaderboardPlayerProfile
{
	public string Player { get; set; } = "";

	public bool Online { get; set; }

	public long TotalPlaySeconds { get; set; }

	public long SessionSeconds { get; set; }

	public long FirstSeenUnixMilliseconds { get; set; }

	public long LastSeenUnixMilliseconds { get; set; }

	public int TotalSkillLevels { get; set; }

	public double TotalSkillExperience { get; set; }

	public int SkillsLeveled { get; set; }

	public List<WebMapPlayerSkillSummary> TopSkills { get; set; } = new List<WebMapPlayerSkillSummary>();
}
