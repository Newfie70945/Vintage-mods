using System.Collections.Generic;

namespace BlockLogger.AnimalLimiter;

public sealed class AnimalPopulationScanState
{
	public long ScanId { get; init; }

	public bool Running { get; init; }

	public bool Completed { get; init; }

	public int ProgressPercent { get; init; }

	public int ProcessedEntities { get; init; }

	public int TotalEntities { get; init; }

	public int ProcessedRegions { get; init; }

	public int TotalRegions { get; init; }

	public long StartedUnixMilliseconds { get; init; }

	public long DurationMilliseconds { get; init; }

	public string Mode { get; init; } = "";

	public string Error { get; init; } = "";

	public List<string> Lines { get; init; } = new List<string>();
}
