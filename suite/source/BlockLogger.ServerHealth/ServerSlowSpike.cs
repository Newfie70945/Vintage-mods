namespace BlockLogger.ServerHealth;

public sealed class ServerSlowSpike
{
	public long UnixMilliseconds { get; set; }

	public double DelayMs { get; set; }

	public double CpuPercent { get; set; }

	public double WorkingSetMb { get; set; }

	public int Players { get; set; }

	public int LoadedEntities { get; set; }

	public int LoadedChunks { get; set; }

	public int BlockLoggerQueue { get; set; }

	public long BlockLoggerWriterMs { get; set; }

	public int Gen0Delta { get; set; }

	public int Gen1Delta { get; set; }

	public int Gen2Delta { get; set; }
}
