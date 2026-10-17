namespace BlockLogger.AntiCheat;

public sealed class AntiCheatTrustedTeleportEventWebSnapshot
{
	public long Utc { get; set; }

	public string PlayerName { get; set; } = "";

	public string PlayerUid { get; set; } = "";

	public double FromX { get; set; }

	public double FromY { get; set; }

	public double FromZ { get; set; }

	public int FromDimension { get; set; }

	public double ToX { get; set; }

	public double ToY { get; set; }

	public double ToZ { get; set; }

	public int ToDimension { get; set; }

	public string Reason { get; set; } = "";

	public string Verification { get; set; } = "";
}
