namespace BlockLogger.ServerHealth;

public sealed class ServerHealthLiveState
{
	public bool Enabled { get; set; }

	public ServerHealthSample Current { get; set; } = new ServerHealthSample();

	public bool ProfileRunning { get; set; }

	public int ProfileDurationSeconds { get; set; }

	public int ProfileRemainingSeconds { get; set; }
}
