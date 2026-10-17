using System.Collections.Generic;

namespace BlockLogger.Announcements;

public sealed class AnnouncementSnapshot
{
	public bool Enabled { get; set; }

	public string Prefix { get; set; } = "";

	public string Timezone { get; set; } = "";

	public int EnabledRules { get; set; }

	public int TotalRules { get; set; }

	public List<AnnouncementRule> Rules { get; set; } = new List<AnnouncementRule>();
}
