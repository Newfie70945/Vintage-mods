using System;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatConsequenceRule
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N");

	public string Name { get; set; } = "New consequence";

	public bool Enabled { get; set; } = true;

	public string TriggerType { get; set; } = "FindingCode";

	public string TriggerValue { get; set; } = "FORBIDDEN_MOD";

	public string Subject { get; set; } = "";

	public string Action { get; set; } = "Log";

	public string Duration { get; set; } = "30m";

	public string Message { get; set; } = "Anti-cheat policy matched: {code} {subject}.";
}
