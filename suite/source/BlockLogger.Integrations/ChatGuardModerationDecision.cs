namespace BlockLogger.Integrations;

public sealed class ChatGuardModerationDecision
{
	public bool Available { get; set; }

	public bool Success { get; set; }

	public bool Blocked { get; set; }

	public string Rule { get; set; } = "";

	public string Action { get; set; } = "Allow";

	public string Detail { get; set; } = "";
}
