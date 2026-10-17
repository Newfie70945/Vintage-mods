namespace BlockLogger.ModUpdates;

public sealed class ModUpdateEntry
{
	public string ModId { get; set; } = "";

	public string InstalledVersion { get; set; } = "";

	public string RecommendedVersion { get; set; } = "";

	public string Status { get; set; } = "Unknown";

	public string Detail { get; set; } = "";

	public string ModPageUrl { get; set; } = "";

	public bool AutoUpdateSupported { get; set; }

	public string AutoUpdateReason { get; set; } = "";
}
