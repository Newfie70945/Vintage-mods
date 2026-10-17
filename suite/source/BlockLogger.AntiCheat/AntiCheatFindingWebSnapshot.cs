namespace BlockLogger.AntiCheat;

public sealed class AntiCheatFindingWebSnapshot
{
	public string Scanner { get; set; } = "";

	public string Code { get; set; } = "";

	public int Severity { get; set; }

	public string Summary { get; set; } = "";

	public string Subject { get; set; } = "";

	public string Evidence { get; set; } = "";

	public string Category { get; set; } = "";

	public string EvidenceDomain { get; set; } = "Other";

	public bool ServerAuthoritative { get; set; }

	public int RiskPoints { get; set; }

	public long ObservedUtc { get; set; }

	public string Confidence { get; set; } = "Low";

	public int ConfidenceScore { get; set; }

	public int CorroboratingSignals { get; set; }

	public string Why { get; set; } = "";

	public string RecommendedAction { get; set; } = "";
}
