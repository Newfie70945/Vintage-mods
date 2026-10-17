namespace BlockLogger.AntiCheat;

public sealed class AntiCheatIncidentWebSnapshot
{
	public long Id { get; set; }

	public long UnixMilliseconds { get; set; }

	public string PlayerUid { get; set; } = "";

	public string PlayerName { get; set; } = "";

	public string Scanner { get; set; } = "";

	public string Code { get; set; } = "";

	public int Severity { get; set; }

	public string Summary { get; set; } = "";

	public string Subject { get; set; } = "";

	public string Evidence { get; set; } = "";

	public int RiskBefore { get; set; }

	public int RiskAfter { get; set; }

	public string StatusAfter { get; set; } = "";

	public string Consequence { get; set; } = "";

	public double X { get; set; }

	public double Y { get; set; }

	public double Z { get; set; }

	public int Dimension { get; set; }

	public string Category { get; set; } = "";

	public string Confidence { get; set; } = "Low";

	public int ConfidenceScore { get; set; }

	public int CorroboratingSignals { get; set; }

	public string Why { get; set; } = "";

	public string RecommendedAction { get; set; } = "";
}
