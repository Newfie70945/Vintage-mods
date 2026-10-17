namespace BlockLogger.WebDashboard;

public sealed class DatabaseStorageRecommendation
{
	public string Severity { get; set; } = "Info";

	public string Database { get; set; } = "";

	public string Area { get; set; } = "";

	public string Finding { get; set; } = "";

	public string Improvement { get; set; } = "";

	public string Safety { get; set; } = "Safe";
}
