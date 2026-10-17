namespace BlockLogger.AntiCheat;

public sealed class AntiCheatPacketSuppressionWebSnapshot
{
	public string Code { get; set; } = "";

	public string Name { get; set; } = "";

	public string Category { get; set; } = "Safety";

	public string Description { get; set; } = "";

	public long Count { get; set; }

	public long LastSeenUtc { get; set; }

	public string LastPlayerName { get; set; } = "";

	public string LastDetail { get; set; } = "";
}
