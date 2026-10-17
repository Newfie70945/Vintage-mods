namespace BlockLogger.Integrations;

public sealed class CommunityMotdControlSnapshot
{
	public bool Available { get; set; }

	public bool Success { get; set; }

	public string Message { get; set; } = "";

	public string Version { get; set; } = "";

	public string ConfigJson { get; set; } = "{}";
}
