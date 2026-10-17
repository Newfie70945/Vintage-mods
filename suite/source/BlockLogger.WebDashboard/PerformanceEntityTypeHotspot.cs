namespace BlockLogger.WebDashboard;

public sealed class PerformanceEntityTypeHotspot
{
	public string Code { get; set; } = "";

	public string RuntimeType { get; set; } = "";

	public int Count { get; set; }

	public int ChunkCount { get; set; }
}
