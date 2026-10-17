namespace BlockLogger.ServerHealth;

public sealed class ServerHealthSample
{
	public long UnixMilliseconds { get; set; }

	public double MainThreadDelayMs { get; set; }

	public double CpuPercent { get; set; }

	public double WorkingSetMb { get; set; }

	public double PrivateMemoryMb { get; set; }

	public double ManagedHeapMb { get; set; }

	public double GcCommittedMb { get; set; }

	public double GcFragmentedMb { get; set; }

	public double EstimatedNativeMb { get; set; }

	public int Gen0Collections { get; set; }

	public int Gen1Collections { get; set; }

	public int Gen2Collections { get; set; }

	public int Players { get; set; }

	public int LoadedEntities { get; set; }

	public int LoadedChunks { get; set; }

	public int BlockLoggerQueue { get; set; }

	public long BlockLoggerWriterMs { get; set; }

	public long BlockLoggerDropped { get; set; }

	public int AnimalTrackedEntities { get; set; }

	public int AnimalTrackedRegions { get; set; }
}
