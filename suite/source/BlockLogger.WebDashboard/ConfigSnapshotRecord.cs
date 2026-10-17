namespace BlockLogger.WebDashboard;

public sealed class ConfigSnapshotRecord
{
	public string Id { get; set; } = "";

	public long UnixMilliseconds { get; set; }

	public string Label { get; set; } = "";

	public string CreatedBy { get; set; } = "";

	public string SuiteFeaturesJson { get; set; } = "";

	public string BlockLoggerJson { get; set; } = "";

	public string WebDashboardJson { get; set; } = "";

	public string AnimalLimiterJson { get; set; } = "";

	public string LoginProtectionJson { get; set; } = "";

	public string PublicPlayerListJson { get; set; } = "";
}
