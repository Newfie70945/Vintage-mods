namespace BlockLogger.ServerHealth;

public sealed class ServerDiagnosticIncident
{
	public long UnixMilliseconds { get; set; }

	public string Severity { get; set; } = "Watch";

	public double DelayMs { get; set; }

	public double CpuPercent { get; set; }

	public double ManagedHeapMb { get; set; }

	public double PrivateMemoryMb { get; set; }

	public double EstimatedNativeMb { get; set; }

	public int LoadedEntities { get; set; }

	public int EntityDelta { get; set; }

	public int LoadedChunks { get; set; }

	public int ChunkDelta { get; set; }

	public int BlockLoggerQueue { get; set; }

	public long BlockLoggerWriterMs { get; set; }

	public long BlockLoggerDroppedDelta { get; set; }

	public int Gen0Delta { get; set; }

	public int Gen1Delta { get; set; }

	public int Gen2Delta { get; set; }

	public string Evidence { get; set; } = "";
}
