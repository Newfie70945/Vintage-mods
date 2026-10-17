using System.Collections.Generic;

namespace BlockLogger.WebMap;

public sealed class WebMapSkillLeaderboard
{
	public string Name { get; set; } = "";

	public string DisplayName { get; set; } = "";

	public string Icon { get; set; } = "★";

	public string Accent { get; set; } = "#60a5fa";

	public List<WebMapLeaderboardEntry> Entries { get; set; } = new List<WebMapLeaderboardEntry>();
}
