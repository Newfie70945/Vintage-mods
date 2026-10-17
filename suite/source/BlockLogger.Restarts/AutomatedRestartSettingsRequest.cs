using System.Collections.Generic;

namespace BlockLogger.Restarts;

public sealed class AutomatedRestartSettingsRequest
{
	public bool Enabled { get; set; }

	public string RestartTimeUtc { get; set; } = "00:00:00";

	public List<string> RestartTimesUtc { get; set; } = new List<string>();

	public List<int> WarningMinutes { get; set; } = new List<int>();

	public bool AnnounceInGame { get; set; } = true;

	public bool AnnounceToDiscord { get; set; } = true;

	public string Reason { get; set; } = "Scheduled server restart";
}
