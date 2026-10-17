using System;
using System.Collections.Generic;

namespace BlockLogger.WebMap;

public sealed class WebMapTerrainManifest
{
	public int Version { get; set; }

	public string WorldName { get; set; } = "";

	public string SavegameIdentifier { get; set; } = "";

	public long GeneratedUnixMilliseconds { get; set; }

	public int TileSize { get; set; }

	public int TileCount { get; set; }

	public long ChunkColumnCount { get; set; }

	public int MinTileX { get; set; }

	public int MaxTileX { get; set; }

	public int MinTileZ { get; set; }

	public int MaxTileZ { get; set; }

	public int MinWorldX { get; set; }

	public int MaxWorldXExclusive { get; set; }

	public int MinWorldZ { get; set; }

	public int MaxWorldZExclusive { get; set; }

	public string Style { get; set; } = "";

	public bool OverviewAvailable { get; set; }

	public int OverviewWidth { get; set; }

	public int OverviewHeight { get; set; }

	public int OverviewTileStride { get; set; }

	public int OverviewCellSizeBlocks { get; set; } = 32;

	public int[] LodFactors { get; set; } = Array.Empty<int>();

	public int LodTilePixelSize { get; set; } = 256;

	public Dictionary<string, WebMapTerrainTileMetadata> Tiles { get; set; } = new Dictionary<string, WebMapTerrainTileMetadata>(StringComparer.Ordinal);
}
