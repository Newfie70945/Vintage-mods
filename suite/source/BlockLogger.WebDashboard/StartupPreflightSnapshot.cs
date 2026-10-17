using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class StartupPreflightSnapshot
{
	public long GeneratedUnixMilliseconds { get; set; }

	public string Status { get; set; } = "Ready";

	public string TargetGameVersion { get; set; } = "";

	public string LoadedGameVersion { get; set; } = "";

	public int LoadedModCount { get; set; }

	public int Critical { get; set; }

	public int High { get; set; }

	public int Watch { get; set; }

	public List<PreflightCheck> Checks { get; set; } = new List<PreflightCheck>();

	public ModChangeSummary ModChanges { get; set; } = new ModChangeSummary();
}
