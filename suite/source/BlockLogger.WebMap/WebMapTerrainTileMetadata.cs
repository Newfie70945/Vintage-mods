using System;

namespace BlockLogger.WebMap;

public sealed class WebMapTerrainTileMetadata
{
	public int X { get; set; }

	public int Z { get; set; }

	public byte R { get; set; }

	public byte G { get; set; }

	public byte B { get; set; }

	public byte[] ChunkRgb { get; set; } = Array.Empty<byte>();

	public int GeneratedChunkCount { get; set; }

	public long LastGeneratedUnixMilliseconds { get; set; }
}
