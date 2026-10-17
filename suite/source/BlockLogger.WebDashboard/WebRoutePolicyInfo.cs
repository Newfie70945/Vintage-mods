namespace BlockLogger.WebDashboard;

public sealed class WebRoutePolicyInfo
{
	public string Method { get; set; } = "";

	public string Path { get; set; } = "";

	public string Access { get; set; } = "";

	public string Risk { get; set; } = "";

	public string Safety { get; set; } = "";

	public int CooldownSeconds { get; set; }

	public bool ExclusiveHeavyOperation { get; set; }
}
