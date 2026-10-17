namespace BlockLogger.ServerHealth;

public sealed class ServerSmartSignal
{
	public string Category { get; set; } = "";

	public string Severity { get; set; } = "Watch";

	public string Confidence { get; set; } = "Low";

	public int Score { get; set; }

	public string Title { get; set; } = "";

	public string Evidence { get; set; } = "";

	public string NextAction { get; set; } = "";
}
