namespace BlockLogger.AntiCheat;

internal sealed class AntiCheatIncidentRecord
{
	public long Id { get; set; }

	public long UnixMilliseconds { get; set; }

	public string PlayerUid { get; set; } = "";

	public string PlayerName { get; set; } = "";

	public string Scanner { get; set; } = "ServerBehavior";

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
}
