namespace BlockLogger.AntiCheat;

public sealed class AntiCheatConsequenceRuleWebSnapshot
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public bool Enabled { get; set; }

	public string TriggerType { get; set; } = "FindingCode";

	public string TriggerValue { get; set; } = "";

	public string Subject { get; set; } = "";

	public string Action { get; set; } = "Log";

	public string Duration { get; set; } = "";

	public string Message { get; set; } = "";
}
