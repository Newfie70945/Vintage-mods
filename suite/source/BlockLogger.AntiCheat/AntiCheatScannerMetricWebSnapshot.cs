namespace BlockLogger.AntiCheat;

public sealed class AntiCheatScannerMetricWebSnapshot
{
	public string Scanner { get; set; } = "";

	public long DurationMs { get; set; }

	public bool Success { get; set; }

	public int ModCount { get; set; }

	public int AssemblyCount { get; set; }

	public int FindingCount { get; set; }

	public int CacheHits { get; set; }

	public int CacheMisses { get; set; }
}
