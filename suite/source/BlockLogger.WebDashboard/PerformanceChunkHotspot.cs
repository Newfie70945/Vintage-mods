namespace BlockLogger.WebDashboard;

public sealed class PerformanceChunkHotspot
{
	public int ChunkX { get; set; }

	public int ChunkZ { get; set; }

	public int Dimension { get; set; }

	public int EntityCount { get; set; }

	public int PlayerCount { get; set; }

	public int UniqueEntityTypes { get; set; }

	public string TopEntityTypes { get; set; } = "";

	public int PressureScore { get; set; }
}
