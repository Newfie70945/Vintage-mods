namespace BlockLogger.AntiCheat;

internal sealed class AntiCheatFindingInsight
{
	public string Category { get; set; } = "Other";

	public string Confidence { get; set; } = "Low";

	public int ConfidenceScore { get; set; }

	public int CorroboratingSignals { get; set; }

	public string Why { get; set; } = "";

	public string RecommendedAction { get; set; } = "Review the evidence before taking action.";
}
