using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class PerformanceEntityScanSnapshot
{
	public long UnixMilliseconds { get; set; }

	public int TotalLoadedEntities { get; set; }

	public int PositionedEntities { get; set; }

	public int UnpositionedEntities { get; set; }

	public List<PerformanceEntityTypeHotspot> EntityTypes { get; set; } = new List<PerformanceEntityTypeHotspot>();

	public List<PerformanceChunkHotspot> Chunks { get; set; } = new List<PerformanceChunkHotspot>();
}
