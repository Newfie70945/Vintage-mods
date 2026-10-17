namespace BlockLogger.ServerHealth;

public sealed class ServerProfileSlowTick
{
	public long UnixMilliseconds { get; set; }

	public double TickMs { get; set; }

	public string TopCauseSummary { get; set; } = "";
}
