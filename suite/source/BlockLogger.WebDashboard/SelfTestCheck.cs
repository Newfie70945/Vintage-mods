namespace BlockLogger.WebDashboard;

public sealed class SelfTestCheck
{
	public string Key { get; set; } = "";

	public string Name { get; set; } = "";

	public string Status { get; set; } = "Unknown";

	public string Detail { get; set; } = "";

	public string Safety { get; set; } = "Safe";
}
