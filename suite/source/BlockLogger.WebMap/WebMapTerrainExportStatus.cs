namespace BlockLogger.WebMap;

public sealed class WebMapTerrainExportStatus
{
	public string State { get; set; } = "pending";

	public string Phase { get; set; } = "waiting";

	public double ProgressPercent { get; set; }

	public string Message { get; set; } = "Waiting for startup terrain export.";

	public string LastError { get; set; } = "";

	public string ExportMode { get; set; } = "pending";

	public string FullRebuildReason { get; set; } = "";

	public string Trigger { get; set; } = "startup";

	public long StartedUnixMilliseconds { get; set; }

	public long CompletedUnixMilliseconds { get; set; }

	public long LastSuccessfulUnixMilliseconds { get; set; }

	public long TotalChunkColumns { get; set; }

	public long ProcessedChunkColumns { get; set; }

	public int TotalTiles { get; set; }

	public int WrittenTiles { get; set; }

	public int ReusedTiles { get; set; }

	public int DirtyChunkCount { get; set; }

	public int TileSize { get; set; } = 256;

	public bool ServerLockActive { get; set; }

	public long CoverageScanMilliseconds { get; set; }

	public long WorldReadMilliseconds { get; set; }

	public long RenderMilliseconds { get; set; }

	public long PngWriteMilliseconds { get; set; }

	public long OverviewMilliseconds { get; set; }

	public long LodWriteMilliseconds { get; set; }

	public long TotalElapsedMilliseconds { get; set; }

	public long EstimatedRemainingMilliseconds { get; set; } = -1L;

	public double AverageMillisecondsPerTile { get; set; }

	public long PeakWorkingBufferBytes { get; set; }

	public int BlockColorCacheEntries { get; set; }

	public int OverviewCellSizeBlocks { get; set; }

	public string TerrainReadMode { get; set; } = "world-api";

	public bool ExportResourcesReleased { get; set; }

	public long ManagedHeapBytesAtStart { get; set; }

	public long ManagedHeapBytesAtEnd { get; set; }

	public long ProcessWorkingSetBytesAtStart { get; set; }

	public long ProcessWorkingSetBytesAtEnd { get; set; }

	public long WorldLoaderChunkColumns { get; set; }

	public WebMapTerrainExportStatus Clone()
	{
		return (WebMapTerrainExportStatus)MemberwiseClone();
	}
}
