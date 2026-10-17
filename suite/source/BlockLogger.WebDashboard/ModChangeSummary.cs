using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class ModChangeSummary
{
	public bool HasPreviousManifest { get; set; }

	public List<string> Added { get; set; } = new List<string>();

	public List<string> Removed { get; set; } = new List<string>();

	public List<string> Updated { get; set; } = new List<string>();
}
