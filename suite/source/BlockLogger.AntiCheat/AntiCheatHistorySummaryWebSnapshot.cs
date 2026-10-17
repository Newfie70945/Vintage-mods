namespace BlockLogger.AntiCheat;

public class AntiCheatHistorySummaryWebSnapshot
{
	public bool Enabled { get; set; }

	public int RetentionDays { get; set; }

	public int MaxRecords { get; set; }

	public int TotalRecords { get; set; }

	public int UniquePlayers { get; set; }

	public int Last24Hours { get; set; }
}
