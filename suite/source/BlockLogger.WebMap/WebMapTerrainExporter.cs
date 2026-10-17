using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlockLogger.Services;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

internal sealed class WebMapTerrainExporter
{
	private readonly record struct TileCoord(int X, int Z);

	private readonly record struct LodTileCoord(int Factor, int X, int Z);

	private readonly record struct ChunkCoord(int X, int Z);

	private readonly record struct TerrainCoverage(HashSet<TileCoord> Tiles, HashSet<ChunkCoord> Chunks, long ChunkColumns);

	private static class TerrainPalette
	{
		public static Rgb Get(Block? block)
		{
			if (block == null || block.Id == 0)
			{
				return new Rgb(16, 22, 29);
			}
			string domain = block.Code?.Domain?.ToLowerInvariant() ?? "";
			string text = block.Code?.Path?.ToLowerInvariant() ?? "";
			string text2 = "";
			try
			{
				if (block.Variant != null)
				{
					foreach (KeyValuePair<string, string> item in block.Variant)
					{
						if (!string.Equals(item.Key, "grasscoverage", StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}
						text2 = item.Value?.ToLowerInvariant() ?? "";
						break;
					}
				}
			}
			catch
			{
			}
			if (string.IsNullOrEmpty(text2) && (text.StartsWith("soil-", StringComparison.Ordinal) || text.StartsWith("cob-", StringComparison.Ordinal)))
			{
				if (text.EndsWith("-verysparse", StringComparison.Ordinal))
				{
					text2 = "verysparse";
				}
				else if (text.EndsWith("-sparse", StringComparison.Ordinal))
				{
					text2 = "sparse";
				}
				else if (text.EndsWith("-normal", StringComparison.Ordinal))
				{
					text2 = "normal";
				}
				else if (text.EndsWith("-none", StringComparison.Ordinal))
				{
					text2 = "none";
				}
			}
			Rgb result;
			if (!string.IsNullOrEmpty(text2) && text2 != "none")
			{
				Rgb rgb = Vary(new Rgb(82, 126, 61), domain, text + ":" + text2, 8);
				Rgb a = Vary(new Rgb(111, 91, 61), domain, text, 6);
				if (1 == 0)
				{
				}
				result = text2 switch
				{
					"normal" => rgb, 
					"sparse" => Blend(a, rgb, 0.75), 
					"verysparse" => Blend(a, rgb, 0.5), 
					_ => Blend(a, rgb, 0.65), 
				};
				if (1 == 0)
				{
				}
				return result;
			}
			if (HasAny(text, "water", "lake", "ocean", "river"))
			{
				return new Rgb(54, 104, 145);
			}
			if (HasAny(text, "lava", "molten"))
			{
				return new Rgb(207, 83, 35);
			}
			if (text.Contains("snow"))
			{
				return new Rgb(226, 233, 237);
			}
			if (text.Contains("ice"))
			{
				return new Rgb(155, 205, 220);
			}
			if (HasAny(text, "grass", "meadow", "moss"))
			{
				return Vary(new Rgb(86, 122, 62), domain, text, 9);
			}
			if (HasAny(text, "leaves", "foliage", "fern", "shrub"))
			{
				return Vary(new Rgb(62, 105, 55), domain, text, 10);
			}
			if (HasAny(text, "flower", "crop", "plant", "sapling"))
			{
				return Vary(new Rgb(78, 119, 64), domain, text, 10);
			}
			if (HasAny(text, "farmland", "compost"))
			{
				return Vary(new Rgb(105, 79, 48), domain, text, 8);
			}
			if (HasAny(text, "soil", "dirt", "earth", "mud", "peat"))
			{
				return Vary(new Rgb(111, 91, 61), domain, text, 9);
			}
			if (text.Contains("clay"))
			{
				return Vary(new Rgb(139, 103, 78), domain, text, 8);
			}
			if (text.Contains("sand"))
			{
				return Vary(new Rgb(190, 174, 111), domain, text, 8);
			}
			if (text.Contains("gravel"))
			{
				return Vary(new Rgb(128, 122, 109), domain, text, 8);
			}
			if (HasAny(text, "path", "road", "packed"))
			{
				return Vary(new Rgb(130, 112, 82), domain, text, 7);
			}
			if (HasAny(text, "plank", "wood", "log", "board"))
			{
				return Vary(new Rgb(111, 80, 50), domain, text, 9);
			}
			if (HasAny(text, "brick", "tile", "cobble", "ashlar", "stonebrick"))
			{
				return Vary(new Rgb(129, 111, 96), domain, text, 10);
			}
			if (HasAny(text, "metal", "steel", "iron", "copper", "bronze", "tin", "brass"))
			{
				return Vary(new Rgb(128, 138, 145), domain, text, 9);
			}
			if (block.ForFluidsLayer)
			{
				return new Rgb(54, 104, 145);
			}
			EnumBlockMaterial blockMaterial = block.BlockMaterial;
			if (1 == 0)
			{
			}
			result = blockMaterial switch
			{
				EnumBlockMaterial.Soil => new Rgb(111, 91, 61), 
				EnumBlockMaterial.Gravel => new Rgb(128, 122, 109), 
				EnumBlockMaterial.Sand => new Rgb(190, 174, 111), 
				EnumBlockMaterial.Wood => new Rgb(104, 75, 49), 
				EnumBlockMaterial.Leaves => new Rgb(67, 105, 58), 
				EnumBlockMaterial.Stone => new Rgb(118, 120, 116), 
				EnumBlockMaterial.Ore => new Rgb(111, 108, 102), 
				EnumBlockMaterial.Snow => new Rgb(226, 233, 237), 
				EnumBlockMaterial.Ice => new Rgb(155, 205, 220), 
				EnumBlockMaterial.Metal => new Rgb(128, 138, 145), 
				EnumBlockMaterial.Mantle => new Rgb(86, 73, 69), 
				EnumBlockMaterial.Plant => new Rgb(77, 116, 64), 
				EnumBlockMaterial.Glass => new Rgb(137, 169, 174), 
				EnumBlockMaterial.Ceramic => new Rgb(151, 111, 85), 
				EnumBlockMaterial.Cloth => new Rgb(137, 126, 113), 
				EnumBlockMaterial.Lava => new Rgb(207, 83, 35), 
				EnumBlockMaterial.Brick => new Rgb(137, 84, 66), 
				EnumBlockMaterial.Fire => new Rgb(212, 116, 43), 
				EnumBlockMaterial.Meta => new Rgb(95, 94, 103), 
				_ => new Rgb(102, 108, 101), 
			};
			if (1 == 0)
			{
			}
			Rgb color = result;
			return Vary(color, domain, text, 7);
		}

		private static Rgb Blend(Rgb a, Rgb b, double amount)
		{
			amount = Math.Clamp(amount, 0.0, 1.0);
			double num = 1.0 - amount;
			return new Rgb((byte)Math.Clamp((int)Math.Round((double)(int)a.R * num + (double)(int)b.R * amount), 0, 255), (byte)Math.Clamp((int)Math.Round((double)(int)a.G * num + (double)(int)b.G * amount), 0, 255), (byte)Math.Clamp((int)Math.Round((double)(int)a.B * num + (double)(int)b.B * amount), 0, 255));
		}

		private static bool HasAny(string value, params string[] terms)
		{
			foreach (string value2 in terms)
			{
				if (value.Contains(value2, StringComparison.Ordinal))
				{
					return true;
				}
			}
			return false;
		}

		private static Rgb Vary(Rgb color, string domain, string code, int amount)
		{
			uint num = 2166136261u;
			foreach (char c in domain)
			{
				num ^= c;
				num *= 16777619;
			}
			num ^= 0x3A;
			num *= 16777619;
			foreach (char c2 in code)
			{
				num ^= c2;
				num *= 16777619;
			}
			int num2 = (int)(num % (uint)(amount * 2 + 1)) - amount;
			return new Rgb((byte)Math.Clamp(color.R + num2, 0, 255), (byte)Math.Clamp(color.G + num2, 0, 255), (byte)Math.Clamp(color.B + num2, 0, 255));
		}
	}

	private readonly record struct Rgb(byte R, byte G, byte B);

	private readonly record struct RenderedTile(Rgb Average, byte[] ChunkRgb);

	private readonly record struct RuntimeRenderedTile(RenderedTile Tile, long ProcessedColumns);

	private readonly record struct DatabaseRenderedTile(RenderedTile Tile, long ProcessedColumns, long WorldReadMilliseconds, long RenderMilliseconds, long PngWriteMilliseconds);

	private static class PngReader
	{
		private static readonly byte[] Signature = new byte[8] { 137, 80, 78, 71, 13, 10, 26, 10 };

		public static bool TryReadRgba(string path, out int width, out int height, out byte[] rgba)
		{
			width = (height = 0);
			rgba = Array.Empty<byte>();
			try
			{
				using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
				Span<byte> span = stackalloc byte[8];
				if (!ReadExactly(fileStream, span) || !((ReadOnlySpan<byte>)span).SequenceEqual((ReadOnlySpan<byte>)Signature.AsSpan()))
				{
					return false;
				}
				using MemoryStream memoryStream = new MemoryStream();
				bool flag = false;
				Span<byte> buffer = stackalloc byte[8];
				int num4;
				byte[] array3;
				checked
				{
					string text;
					do
					{
						if (!ReadExactly(fileStream, buffer))
						{
							return false;
						}
						int num = (int)ReadBe32(buffer.Slice(0, 4));
						text = Encoding.ASCII.GetString(buffer.Slice(4, 4));
						if (num < 0 || num > 268435456)
						{
							return false;
						}
						if (text == "IHDR")
						{
							if (num != 13)
							{
								return false;
							}
							byte[] array = new byte[13];
							if (!ReadExactly(fileStream, array))
							{
								return false;
							}
							width = (int)ReadBe32(array.AsSpan(0, 4));
							height = (int)ReadBe32(array.AsSpan(4, 4));
							flag = width > 0 && height > 0 && array[8] == 8 && array[9] == 6 && array[10] == 0 && array[11] == 0 && array[12] == 0;
						}
						else if (text == "IDAT")
						{
							byte[] array2 = ArrayPool<byte>.Shared.Rent(Math.Min(Math.Max(num, 1), 65536));
							try
							{
								int num2 = num;
								while (num2 > 0)
								{
									int count = Math.Min(num2, array2.Length);
									int num3 = fileStream.Read(array2, 0, count);
									if (num3 <= 0)
									{
										return false;
									}
									memoryStream.Write(array2, 0, num3);
									num2 = unchecked(num2 - num3);
								}
							}
							finally
							{
								ArrayPool<byte>.Shared.Return(array2);
							}
						}
						else
						{
							fileStream.Seek(num, SeekOrigin.Current);
						}
						fileStream.Seek(4L, SeekOrigin.Current);
					}
					while (!(text == "IEND"));
					if (!flag || memoryStream.Length == 0)
					{
						return false;
					}
					num4 = width * 4;
					array3 = new byte[num4 * height];
					memoryStream.Position = 0L;
				}
				using ZLibStream zLibStream = new ZLibStream(memoryStream, CompressionMode.Decompress, leaveOpen: true);
				for (int i = 0; i < height; i++)
				{
					int num5 = zLibStream.ReadByte();
					if (num5 != 0)
					{
						return false;
					}
					if (!ReadExactly(zLibStream, array3.AsSpan(i * num4, num4)))
					{
						return false;
					}
				}
				rgba = array3;
				return true;
			}
			catch
			{
				width = (height = 0);
				rgba = Array.Empty<byte>();
				return false;
			}
		}

		private static bool ReadExactly(Stream stream, Span<byte> buffer)
		{
			int num;
			for (int i = 0; i < buffer.Length; i += num)
			{
				num = stream.Read(buffer.Slice(i));
				if (num <= 0)
				{
					return false;
				}
			}
			return true;
		}

		private static bool ReadExactly(Stream stream, byte[] buffer)
		{
			return ReadExactly(stream, buffer.AsSpan());
		}

		private static uint ReadBe32(ReadOnlySpan<byte> bytes)
		{
			return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
		}
	}

	private static class PngWriter
	{
		private static readonly byte[] Signature = new byte[8] { 137, 80, 78, 71, 13, 10, 26, 10 };

		private static readonly uint[] CrcTable = BuildCrcTable();

		public static void WriteRgba(string path, int width, int height, byte[] rgba)
		{
			using FileStream fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
			fileStream.Write(Signature, 0, Signature.Length);
			byte[] array = new byte[13];
			WriteBe32(array, 0, (uint)width);
			WriteBe32(array, 4, (uint)height);
			array[8] = 8;
			array[9] = 6;
			array[10] = 0;
			array[11] = 0;
			array[12] = 0;
			WriteChunk(fileStream, "IHDR", array);
			using MemoryStream memoryStream = new MemoryStream();
			using (ZLibStream zLibStream = new ZLibStream(memoryStream, CompressionLevel.Fastest, leaveOpen: true))
			{
				int num = width * 4;
				for (int i = 0; i < height; i++)
				{
					zLibStream.WriteByte(0);
					zLibStream.Write(rgba, i * num, num);
				}
			}
			if (memoryStream.TryGetBuffer(out var buffer) && buffer.Array != null)
			{
				WriteChunk(fileStream, "IDAT", buffer.Array, buffer.Offset, buffer.Count);
			}
			else
			{
				WriteChunk(fileStream, "IDAT", memoryStream.ToArray());
			}
			WriteChunk(fileStream, "IEND", Array.Empty<byte>());
		}

		private static void WriteChunk(Stream stream, string type, byte[] data)
		{
			WriteChunk(stream, type, data, 0, data.Length);
		}

		private static void WriteChunk(Stream stream, string type, byte[] data, int offset, int count)
		{
			byte[] bytes = Encoding.ASCII.GetBytes(type);
			Span<byte> span = stackalloc byte[4];
			WriteBe32(span, (uint)count);
			stream.Write(span);
			stream.Write(bytes, 0, bytes.Length);
			if (count > 0)
			{
				stream.Write(data, offset, count);
			}
			uint num = uint.MaxValue;
			byte[] array = bytes;
			foreach (byte b in array)
			{
				num = CrcTable[(num ^ b) & 0xFF] ^ (num >> 8);
			}
			int num2 = offset + count;
			for (int j = offset; j < num2; j++)
			{
				num = CrcTable[(num ^ data[j]) & 0xFF] ^ (num >> 8);
			}
			num ^= 0xFFFFFFFFu;
			Span<byte> span2 = stackalloc byte[4];
			WriteBe32(span2, num);
			stream.Write(span2);
		}

		private static uint[] BuildCrcTable()
		{
			uint[] array = new uint[256];
			for (uint num = 0u; num < array.Length; num++)
			{
				uint num2 = num;
				for (int i = 0; i < 8; i++)
				{
					num2 = (((num2 & 1) != 0) ? (0xEDB88320u ^ (num2 >> 1)) : (num2 >> 1));
				}
				array[num] = num2;
			}
			return array;
		}

		private static void WriteBe32(byte[] buffer, int offset, uint value)
		{
			buffer[offset] = (byte)(value >> 24);
			buffer[offset + 1] = (byte)(value >> 16);
			buffer[offset + 2] = (byte)(value >> 8);
			buffer[offset + 3] = (byte)value;
		}

		private static void WriteBe32(Span<byte> buffer, uint value)
		{
			buffer[0] = (byte)(value >> 24);
			buffer[1] = (byte)(value >> 16);
			buffer[2] = (byte)(value >> 8);
			buffer[3] = (byte)value;
		}
	}

	private const int ChunkSize = 32;

	private const int DimensionMask = 31;

	private const long CoordMask21 = 2097151L;

	private const int ManifestVersion = 12;

	private static readonly string[] DerivedTerrainStyles = new string[2] { "sepia", "topo" };

	private readonly ICoreServerAPI api;

	private readonly string rootPath;

	private readonly Action<WebMapTerrainExportStatus> publishStatus;

	private readonly WebMapDirtyChunkJournal? dirtyJournal;

	private readonly Dictionary<int, Rgb> blockColorCache = new Dictionary<int, Rgb>();

	private Rgb[] blockColorById = Array.Empty<Rgb>();

	private int blockPalettePrimed;

	private long lastConsoleProgressUnixMilliseconds;

	private long lastStatusProgressPublishUnixMilliseconds;

	private int lastConsoleProgressPercent = -1;

	private long progressPhaseStartedUnixMilliseconds;

	private readonly Queue<double> recentTileMilliseconds = new Queue<double>();

	private long etaLastSampleUnixMilliseconds;

	private int etaLastCompletedTiles;

	private long surfaceSampleCount;

	private long resolvedSurfaceSampleCount;

	private long unresolvedSurfaceSampleCount;

	private long chunkExistenceChecks;

	private long existingChunkColumns;

	private long loadedChunkColumns;

	private long mapChunksResolved;

	private long rainHeightMapsResolved;

	private long terrainHeightMapsResolved;

	private long accessorHeightFallbackColumns;

	private long directScanFallbackColumns;

	private readonly Dictionary<int, long> sampledBlockCounts = new Dictionary<int, long>();

	private readonly object sampledBlockCountsLock = new object();

	private static void RecordTimingSummary(WebMapTerrainExportStatus status)
	{
		if (status.TotalElapsedMilliseconds > 0)
		{
			SuiteTimingRegistry.Record("Web Map terrain export", status.TotalElapsedMilliseconds);
		}
		if (status.CoverageScanMilliseconds > 0)
		{
			SuiteTimingRegistry.Record("Web Map terrain coverage scan", status.CoverageScanMilliseconds);
		}
		if (status.WorldReadMilliseconds > 0)
		{
			SuiteTimingRegistry.Record("Web Map terrain reads", status.WorldReadMilliseconds);
		}
		if (status.RenderMilliseconds > 0)
		{
			SuiteTimingRegistry.Record("Web Map terrain rendering", status.RenderMilliseconds);
		}
		if (status.PngWriteMilliseconds > 0)
		{
			SuiteTimingRegistry.Record("Web Map PNG encoding", status.PngWriteMilliseconds);
		}
		if (status.LodWriteMilliseconds > 0)
		{
			SuiteTimingRegistry.Record("Web Map pyramid generation", status.LodWriteMilliseconds);
		}
	}

	public WebMapTerrainExporter(ICoreServerAPI api, string rootPath, Action<WebMapTerrainExportStatus> publishStatus, WebMapDirtyChunkJournal? dirtyJournal)
	{
		this.api = api;
		this.rootPath = rootPath;
		this.publishStatus = publishStatus;
		this.dirtyJournal = dirtyJournal;
	}

	public bool Export(WebMapConfig config, string trigger = "startup", bool requireIncremental = false)
	{
		using (WebMapExportLogSuppressor.Begin(api))
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			WebMapTerrainExportStatus webMapTerrainExportStatus = new WebMapTerrainExportStatus
			{
				State = "exporting",
				Phase = "coverage",
				ProgressPercent = 2.0,
				StartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				Message = "Enumerating generated map chunks…",
				TileSize = config.TerrainTileSize,
				PeakWorkingBufferBytes = (long)config.TerrainTileSize * (long)config.TerrainTileSize * 5,
				Trigger = (string.IsNullOrWhiteSpace(trigger) ? "startup" : trigger.Trim().ToLowerInvariant()),
				EstimatedRemainingMilliseconds = -1L,
				ManagedHeapBytesAtStart = GC.GetTotalMemory(forceFullCollection: false),
				ProcessWorkingSetBytesAtStart = ReadProcessWorkingSet()
			};
			publishStatus(webMapTerrainExportStatus.Clone());
			lastConsoleProgressUnixMilliseconds = webMapTerrainExportStatus.StartedUnixMilliseconds;
			lastStatusProgressPublishUnixMilliseconds = webMapTerrainExportStatus.StartedUnixMilliseconds;
			lastConsoleProgressPercent = -1;
			api.Logger.Notification("[ServerEssentials/WebMap] terrain {0} export started. Enumerating already-generated map chunks; the server may remain busy while this runs.", webMapTerrainExportStatus.Trigger);
			ResetSurfaceDiagnostics();
			string text = Path.Combine(rootPath, "terrain");
			string text2 = Path.Combine(rootPath, "terrain-next");
			string backupDir = Path.Combine(rootPath, "terrain-previous");
			long snapshotMutationVersion = 0L;
			HashSet<WebMapDirtyChunkCoord> hashSet = dirtyJournal?.Snapshot(out snapshotMutationVersion) ?? new HashSet<WebMapDirtyChunkCoord>();
			webMapTerrainExportStatus.DirtyChunkCount = hashSet.Count;
			try
			{
				PrimeBlockColorCache();
				string savePath = ResolveCurrentWorldFilepath();
				Stopwatch stopwatch2 = Stopwatch.StartNew();
				TerrainCoverage coverage = EnumerateGeneratedTiles(savePath, config.TerrainTileSize);
				stopwatch2.Stop();
				webMapTerrainExportStatus.CoverageScanMilliseconds = stopwatch2.ElapsedMilliseconds;
				webMapTerrainExportStatus.TotalChunkColumns = coverage.ChunkColumns;
				webMapTerrainExportStatus.TotalTiles = coverage.Tiles.Count;
				api.Logger.Notification("[ServerEssentials/WebMap] terrain coverage scan complete: {0:n0} generated chunk columns across {1:n0} terrain tiles ({2:n0}ms).", webMapTerrainExportStatus.TotalChunkColumns, webMapTerrainExportStatus.TotalTiles, webMapTerrainExportStatus.CoverageScanMilliseconds);
				using WebMapSaveDbTerrainReader webMapSaveDbTerrainReader = OpenDirectSaveReader(config, savePath, webMapTerrainExportStatus);
				if (config.UseDirectSaveDatabaseTerrain && webMapSaveDbTerrainReader == null)
				{
					throw new InvalidOperationException("Safe save-database terrain decoding is unavailable. See the preceding reader diagnostic; the published map has been kept.");
				}
				if (!config.UseDirectSaveDatabaseTerrain)
				{
					api.Logger.Warning("[ServerEssentials/WebMap] legacy world-loader terrain export explicitly selected. It reconstructs entity/mod state and can increase server memory. Enable UseDirectSaveDatabaseTerrain for terrain-only decoding.");
				}
				ValidateStartupCoverageCoordinates(coverage);
				WebMapTerrainManifest webMapTerrainManifest = LoadManifest(text);
				string b = api.WorldManager.SaveGame.SavegameIdentifier ?? "";
				bool flag = config.UseIncrementalTerrainExport && webMapTerrainManifest != null && webMapTerrainManifest.Version == 12 && webMapTerrainManifest.TileSize == config.TerrainTileSize && string.Equals(webMapTerrainManifest.SavegameIdentifier ?? "", b, StringComparison.Ordinal) && webMapTerrainManifest.Tiles != null && Directory.Exists(text);
				bool flag2 = flag && string.Equals(webMapTerrainExportStatus.Trigger, "startup-one-shot", StringComparison.OrdinalIgnoreCase) && hashSet.Count == 0;
				if (!flag | flag2)
				{
					webMapTerrainExportStatus.ExportMode = "full";
					WebMapTerrainExportStatus webMapTerrainExportStatus2 = webMapTerrainExportStatus;
					string fullRebuildReason;
					if (flag2)
					{
						fullRebuildReason = "Manual next-start export was explicitly requested and there are no dirty chunks; escalating the no-op incremental check to a full rebuild.";
					}
					else if (config.UseIncrementalTerrainExport)
					{
						if (webMapTerrainManifest == null)
						{
							fullRebuildReason = "No existing terrain manifest.";
						}
						else if (webMapTerrainManifest.Version != 12)
						{
							fullRebuildReason = "Terrain renderer/cache version changed.";
						}
						else if (webMapTerrainManifest.TileSize != config.TerrainTileSize)
						{
							fullRebuildReason = "Terrain tile size changed.";
						}
						else
						{
							fullRebuildReason = ((!string.Equals(webMapTerrainManifest.SavegameIdentifier ?? "", b, StringComparison.Ordinal)) ? "World/save identity changed." : "Existing terrain cache is incomplete.");
						}
					}
					else
					{
						fullRebuildReason = "Incremental terrain exports are disabled by administrator.";
					}
					webMapTerrainExportStatus2.FullRebuildReason = fullRebuildReason;
					api.Logger.Notification("[ServerEssentials/WebMap] terrain export selected a full rebuild: {0}", webMapTerrainExportStatus.FullRebuildReason);
					if (requireIncremental)
					{
						stopwatch.Stop();
						webMapTerrainExportStatus.State = "ready";
						webMapTerrainExportStatus.Phase = "complete";
						webMapTerrainExportStatus.ProgressPercent = 100.0;
						webMapTerrainExportStatus.ExportMode = "scheduled-skipped";
						webMapTerrainExportStatus.TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
						webMapTerrainExportStatus.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
						webMapTerrainExportStatus.EstimatedRemainingMilliseconds = 0L;
						webMapTerrainExportStatus.Message = "Scheduled dirty export skipped: " + webMapTerrainExportStatus.FullRebuildReason + " A full rebuild is only allowed during startup.";
						publishStatus(webMapTerrainExportStatus.Clone());
						api.Logger.Warning("[ServerEssentials/WebMap] incremental-only terrain export skipped: {0} Dirty journal was preserved for the next startup rebuild.", webMapTerrainExportStatus.FullRebuildReason);
						RecordTimingSummary(webMapTerrainExportStatus);
						return false;
					}
					FullExport(config, coverage, text2, text, backupDir, webMapTerrainExportStatus, webMapSaveDbTerrainReader, savePath);
					dirtyJournal?.TryAcknowledgeSnapshot(hashSet, snapshotMutationVersion);
				}
				else
				{
					webMapTerrainExportStatus.ExportMode = "incremental";
					IncrementalExport(config, coverage, text2, text, webMapTerrainManifest, hashSet, webMapTerrainExportStatus, webMapSaveDbTerrainReader);
					dirtyJournal?.TryAcknowledgeSnapshot(hashSet, snapshotMutationVersion);
				}
				stopwatch.Stop();
				webMapTerrainExportStatus.TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
				webMapTerrainExportStatus.State = "ready";
				webMapTerrainExportStatus.Phase = "complete";
				webMapTerrainExportStatus.ProgressPercent = 100.0;
				webMapTerrainExportStatus.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				webMapTerrainExportStatus.LastSuccessfulUnixMilliseconds = webMapTerrainExportStatus.CompletedUnixMilliseconds;
				webMapTerrainExportStatus.LastError = "";
				webMapTerrainExportStatus.EstimatedRemainingMilliseconds = 0L;
				if (webMapTerrainExportStatus.ExportMode == "incremental")
				{
					webMapTerrainExportStatus.Message = ((webMapTerrainExportStatus.WrittenTiles == 0) ? $"Incremental terrain check complete: all {webMapTerrainExportStatus.ReusedTiles:n0} cached tiles reused; no terrain tiles required rendering." : $"Incremental terrain export complete: {webMapTerrainExportStatus.WrittenTiles:n0} changed/new tiles rendered, {webMapTerrainExportStatus.ReusedTiles:n0} unchanged tiles reused.");
				}
				else
				{
					webMapTerrainExportStatus.Message = ((webMapTerrainExportStatus.WrittenTiles == 0) ? "Full terrain export completed; no generated overworld tiles were present." : $"Full terrain export complete: {webMapTerrainExportStatus.WrittenTiles:n0} tiles from {webMapTerrainExportStatus.ProcessedChunkColumns:n0} chunk columns.");
				}
				publishStatus(webMapTerrainExportStatus.Clone());
				api.Logger.Notification("[ServerEssentials/WebMap] terrain {0} export complete. mode={1}, readMode={2}, rendered={3}, reused={4}, dirtyChunks={5}, elapsed={6}ms, worldRead={7}ms, render={8}ms, png={9}ms, lod={10}ms, resolvedSurface={11:n0}, unresolvedSurface={12:n0}.", webMapTerrainExportStatus.Trigger, webMapTerrainExportStatus.ExportMode, webMapTerrainExportStatus.TerrainReadMode, webMapTerrainExportStatus.WrittenTiles, webMapTerrainExportStatus.ReusedTiles, webMapTerrainExportStatus.DirtyChunkCount, webMapTerrainExportStatus.TotalElapsedMilliseconds, webMapTerrainExportStatus.WorldReadMilliseconds, webMapTerrainExportStatus.RenderMilliseconds, webMapTerrainExportStatus.PngWriteMilliseconds, webMapTerrainExportStatus.LodWriteMilliseconds, resolvedSurfaceSampleCount, unresolvedSurfaceSampleCount);
				RecordTimingSummary(webMapTerrainExportStatus);
				return true;
			}
			catch (OperationCanceledException)
			{
				stopwatch.Stop();
				try
				{
					if (Directory.Exists(text2))
					{
						Directory.Delete(text2, recursive: true);
					}
				}
				catch
				{
				}
				webMapTerrainExportStatus.State = "cancelled";
				webMapTerrainExportStatus.Phase = "cancelled";
				webMapTerrainExportStatus.ProgressPercent = 100.0;
				webMapTerrainExportStatus.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				webMapTerrainExportStatus.TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
				webMapTerrainExportStatus.EstimatedRemainingMilliseconds = -1L;
				webMapTerrainExportStatus.Message = "Forced terrain export cancelled because the Native Web Map was turned OFF. The previously published terrain cache was kept.";
				webMapTerrainExportStatus.LastError = "";
				publishStatus(webMapTerrainExportStatus.Clone());
				api.Logger.Notification("[ServerEssentials/WebMap] forced live terrain export cancelled cleanly; existing terrain cache was preserved.");
				RecordTimingSummary(webMapTerrainExportStatus);
				return false;
			}
			catch (Exception ex2)
			{
				stopwatch.Stop();
				try
				{
					if (Directory.Exists(text2))
					{
						Directory.Delete(text2, recursive: true);
					}
				}
				catch
				{
				}
				webMapTerrainExportStatus.State = "failed";
				webMapTerrainExportStatus.Phase = "failed";
				webMapTerrainExportStatus.ProgressPercent = 100.0;
				webMapTerrainExportStatus.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				webMapTerrainExportStatus.TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
				webMapTerrainExportStatus.Message = "Terrain export failed; existing successfully-written terrain cache was kept and dirty entries remain queued.";
				webMapTerrainExportStatus.LastError = ex2.GetType().Name + ": " + ex2.Message;
				publishStatus(webMapTerrainExportStatus.Clone());
				webMapTerrainExportStatus.EstimatedRemainingMilliseconds = -1L;
				api.Logger.Error("[ServerEssentials/WebMap] terrain {0} export failed: {1}: {2}. Dirty terrain journal was preserved for retry.", webMapTerrainExportStatus.Trigger, ex2.GetType().Name, ex2.Message);
				RecordTimingSummary(webMapTerrainExportStatus);
				return false;
			}
			finally
			{
				blockColorCache.Clear();
				blockColorById = Array.Empty<Rgb>();
				blockPalettePrimed = 0;
				lock (sampledBlockCountsLock)
				{
					sampledBlockCounts.Clear();
				}
				recentTileMilliseconds.Clear();
				webMapTerrainExportStatus.ExportResourcesReleased = true;
				webMapTerrainExportStatus.ManagedHeapBytesAtEnd = GC.GetTotalMemory(forceFullCollection: false);
				webMapTerrainExportStatus.ProcessWorkingSetBytesAtEnd = ReadProcessWorkingSet();
				publishStatus(webMapTerrainExportStatus.Clone());
				api.Logger.Notification("[ServerEssentials/WebMap] export resources released. managedHeap={0:n0}->{1:n0} bytes, processWorkingSet={2:n0}->{3:n0} bytes, legacyWorldColumns={4:n0}. Managed heap and OS/container memory can remain high until normal collection/cache reclaim.", webMapTerrainExportStatus.ManagedHeapBytesAtStart, webMapTerrainExportStatus.ManagedHeapBytesAtEnd, webMapTerrainExportStatus.ProcessWorkingSetBytesAtStart, webMapTerrainExportStatus.ProcessWorkingSetBytesAtEnd, webMapTerrainExportStatus.WorldLoaderChunkColumns);
			}
		}
	}

	private static long ReadProcessWorkingSet()
	{
		try
		{
			using Process process = Process.GetCurrentProcess();
			return process.WorkingSet64;
		}
		catch
		{
			return -1L;
		}
	}

	public async Task<bool> ExportRuntimeForcedAsync(WebMapConfig config, CancellationToken cancellationToken, bool fullRebuild)
	{
		using (WebMapExportLogSuppressor.Begin(api))
		{
			Stopwatch totalSw = Stopwatch.StartNew();
			WebMapTerrainExportStatus status = new WebMapTerrainExportStatus
			{
				State = "exporting",
				Phase = "coverage",
				ProgressPercent = 2.0,
				StartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				Message = (fullRebuild ? "Full live rebuild: enumerating generated map chunks…" : "Changed-terrain update: enumerating generated map chunks…"),
				TileSize = config.TerrainTileSize,
				PeakWorkingBufferBytes = (long)config.TerrainTileSize * (long)config.TerrainTileSize * 5,
				Trigger = (fullRebuild ? "forced-full" : "runtime-update"),
				ExportMode = (fullRebuild ? "forced-full" : "runtime-incremental"),
				EstimatedRemainingMilliseconds = -1L
			};
			publishStatus(status.Clone());
			lastConsoleProgressUnixMilliseconds = status.StartedUnixMilliseconds;
			lastStatusProgressPublishUnixMilliseconds = status.StartedUnixMilliseconds;
			lastConsoleProgressPercent = -1;
			ResetSurfaceDiagnostics();
			string currentDir = Path.Combine(rootPath, "terrain");
			string stageDir = Path.Combine(rootPath, "terrain-next");
			string backupDir = Path.Combine(rootPath, "terrain-previous");
			long dirtyMutationVersion = 0L;
			HashSet<WebMapDirtyChunkCoord> dirtySnapshot = dirtyJournal?.Snapshot(out dirtyMutationVersion) ?? new HashSet<WebMapDirtyChunkCoord>();
			status.DirtyChunkCount = dirtySnapshot.Count;
			try
			{
				api.Logger.Warning(fullRebuild ? "[ServerEssentials/WebMap] administrator started a FULL live terrain rebuild. The read-only save-DB terrain path will be preferred, with cooperative world loading as fallback." : "[ServerEssentials/WebMap] administrator started a changed-terrain live update. Only dirty/new/missing terrain tiles will be rendered; the read-only save-DB terrain path is preferred.");
				await PrimeBlockColorCacheOnMainThreadAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				string savePath = ResolveCurrentWorldFilepath();
				Stopwatch coverageSw = Stopwatch.StartNew();
				TerrainCoverage coverage = EnumerateGeneratedTiles(savePath, config.TerrainTileSize);
				coverageSw.Stop();
				status.CoverageScanMilliseconds = coverageSw.ElapsedMilliseconds;
				status.TotalChunkColumns = coverage.ChunkColumns;
				using WebMapSaveDbTerrainReader directReader = OpenDirectSaveReader(config, savePath, status);
				HashSet<ChunkCoord> liveDirtyChunks = new HashSet<ChunkCoord>(from d in dirtySnapshot
					where d.Dimension == 0
					select new ChunkCoord(d.ChunkX, d.ChunkZ));
				if (directReader != null && liveDirtyChunks.Count > 0)
				{
					status.TerrainReadMode = "save-db+live-dirty";
				}
				WebMapTerrainManifest previous = LoadManifest(currentDir);
				string currentSaveId = api.WorldManager.SaveGame.SavegameIdentifier ?? "";
				bool compatibleIncrementalCache = previous != null && previous.Version == 12 && previous.TileSize == config.TerrainTileSize && string.Equals(previous.SavegameIdentifier ?? "", currentSaveId, StringComparison.Ordinal) && previous.Tiles != null && Directory.Exists(currentDir);
				if (!fullRebuild && !compatibleIncrementalCache)
				{
					totalSw.Stop();
					status.State = "ready";
					status.ExportMode = "runtime-update-needs-rebuild";
					status.TotalElapsedMilliseconds = totalSw.ElapsedMilliseconds;
					status.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
					status.EstimatedRemainingMilliseconds = 0L;
					WebMapTerrainExportStatus webMapTerrainExportStatus = status;
					string fullRebuildReason;
					if (previous == null)
					{
						fullRebuildReason = "No existing terrain manifest.";
					}
					else if (previous.Version != 12)
					{
						fullRebuildReason = "Terrain renderer/cache version changed.";
					}
					else if (previous.TileSize != config.TerrainTileSize)
					{
						fullRebuildReason = "Terrain tile size changed.";
					}
					else
					{
						fullRebuildReason = ((!string.Equals(previous.SavegameIdentifier ?? "", currentSaveId, StringComparison.Ordinal)) ? "World/save identity changed." : "Existing terrain cache is incomplete.");
					}
					webMapTerrainExportStatus.FullRebuildReason = fullRebuildReason;
					status.Message = "Changed-terrain update not started: " + status.FullRebuildReason + " Use Full Rebuild Map once, then future updates can be incremental.";
					publishStatus(status.Clone());
					api.Logger.Warning("[ServerEssentials/WebMap] changed-terrain update requires a full rebuild first: {0}", status.FullRebuildReason);
					RecordTimingSummary(status);
					return false;
				}
				bool success;
				if (fullRebuild)
				{
					status.TotalTiles = coverage.Tiles.Count;
					status.Message = $"Full live rebuild: rendering {coverage.Tiles.Count:n0} tiles from {coverage.ChunkColumns:n0} generated chunk columns…";
					publishStatus(status.Clone());
					success = await FullExportRuntimeAsync(config, coverage, stageDir, currentDir, backupDir, status, directReader, liveDirtyChunks, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else
				{
					success = await IncrementalExportRuntimeAsync(config, coverage, stageDir, currentDir, previous, dirtySnapshot, status, directReader, liveDirtyChunks, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				if (!success)
				{
					return false;
				}
				if (dirtyJournal != null && !dirtyJournal.TryAcknowledgeSnapshot(dirtySnapshot, dirtyMutationVersion))
				{
					api.Logger.Notification("[ServerEssentials/WebMap] terrain changed while the live export was running; dirty journal was conservatively preserved for the next update.");
				}
				totalSw.Stop();
				status.TotalElapsedMilliseconds = totalSw.ElapsedMilliseconds;
				status.State = "ready";
				status.Phase = "complete";
				status.ProgressPercent = 100.0;
				status.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				status.LastSuccessfulUnixMilliseconds = status.CompletedUnixMilliseconds;
				status.LastError = "";
				status.EstimatedRemainingMilliseconds = 0L;
				WebMapTerrainExportStatus webMapTerrainExportStatus2 = status;
				string message;
				if (fullRebuild)
				{
					message = ((status.WrittenTiles == 0) ? "Full live terrain rebuild completed; no generated overworld tiles were present." : $"Full live terrain rebuild complete: {status.WrittenTiles:n0} tiles from {status.ProcessedChunkColumns:n0} chunk columns.");
				}
				else
				{
					message = ((status.WrittenTiles == 0) ? $"Changed-terrain update complete: {status.ReusedTiles:n0} cached tiles reused; nothing needed rendering." : $"Changed-terrain update complete: {status.WrittenTiles:n0} changed/new tiles rendered, {status.ReusedTiles:n0} cached tiles reused.");
				}
				webMapTerrainExportStatus2.Message = message;
				publishStatus(status.Clone());
				api.Logger.Notification("[ServerEssentials/WebMap] live terrain export complete. mode={0}, readMode={1}, rendered={2}, reused={3}, chunkColumns={4}, elapsed={5}ms, worldRead={6}ms, render={7}ms, png={8}ms, lod={9}ms.", status.ExportMode, status.TerrainReadMode, status.WrittenTiles, status.ReusedTiles, status.ProcessedChunkColumns, status.TotalElapsedMilliseconds, status.WorldReadMilliseconds, status.RenderMilliseconds, status.PngWriteMilliseconds, status.LodWriteMilliseconds);
				RecordTimingSummary(status);
				return true;
			}
			catch (OperationCanceledException)
			{
				totalSw.Stop();
				try
				{
					if (Directory.Exists(stageDir))
					{
						Directory.Delete(stageDir, recursive: true);
					}
				}
				catch
				{
				}
				status.State = "ready";
				status.Phase = "cancelled";
				status.ProgressPercent = 100.0;
				status.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				status.TotalElapsedMilliseconds = totalSw.ElapsedMilliseconds;
				status.EstimatedRemainingMilliseconds = 0L;
				status.Message = "Live terrain export cancelled. The previously published terrain cache was kept.";
				status.LastError = "";
				publishStatus(status.Clone());
				api.Logger.Notification("[ServerEssentials/WebMap] live terrain export cancelled; existing terrain cache and dirty journal were preserved.");
				RecordTimingSummary(status);
				return false;
			}
			catch (Exception ex2)
			{
				totalSw.Stop();
				try
				{
					if (Directory.Exists(stageDir))
					{
						Directory.Delete(stageDir, recursive: true);
					}
				}
				catch
				{
				}
				status.State = "failed";
				status.Phase = "failed";
				status.ProgressPercent = 100.0;
				status.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				status.TotalElapsedMilliseconds = totalSw.ElapsedMilliseconds;
				status.EstimatedRemainingMilliseconds = -1L;
				status.Message = "Live terrain export failed; the previously published terrain cache was kept and dirty entries remain queued.";
				status.LastError = ex2.GetType().Name + ": " + ex2.Message;
				publishStatus(status.Clone());
				api.Logger.Error("[ServerEssentials/WebMap] live terrain export failed: {0}: {1}. Existing map cache and dirty journal were preserved.", ex2.GetType().Name, ex2.Message);
				RecordTimingSummary(status);
				return false;
			}
		}
	}

	private async Task<bool> FullExportRuntimeAsync(WebMapConfig config, TerrainCoverage coverage, string stageDir, string currentDir, string backupDir, WebMapTerrainExportStatus status, WebMapSaveDbTerrainReader? directReader, HashSet<ChunkCoord> forceLiveChunks, CancellationToken cancellationToken)
	{
		if (Directory.Exists(stageDir))
		{
			Directory.Delete(stageDir, recursive: true);
		}
		Directory.CreateDirectory(stageDir);
		progressPhaseStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		ResetEtaSamples();
		status.Phase = "rendering";
		status.ProgressPercent = 5.0;
		status.EstimatedRemainingMilliseconds = -1L;
		status.AverageMillisecondsPerTile = 0.0;
		status.ReusedTiles = 0;
		publishStatus(status.Clone());
		LogConsoleProgress("forced-full", 0, coverage.Tiles.Count, 0L, status, force: true);
		WebMapTerrainManifest manifest = CreateManifest(config, coverage);
		int written = 0;
		long processedColumns = 0L;
		foreach (TileCoord tile in from t in coverage.Tiles
			orderby t.Z, t.X
			select t)
		{
			cancellationToken.ThrowIfCancellationRequested();
			RuntimeRenderedTile rendered = await RenderTileRuntimeAsync(config, tile, stageDir, coverage.Chunks, status, directReader, forceLiveChunks, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			processedColumns += rendered.ProcessedColumns;
			manifest.Tiles[TileKey(tile.X, tile.Z)] = new WebMapTerrainTileMetadata
			{
				X = tile.X,
				Z = tile.Z,
				R = rendered.Tile.Average.R,
				G = rendered.Tile.Average.G,
				B = rendered.Tile.Average.B,
				ChunkRgb = rendered.Tile.ChunkRgb,
				GeneratedChunkCount = CountGeneratedChunksForTile(config, tile, coverage.Chunks),
				LastGeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
			};
			written = (status.WrittenTiles = written + 1);
			status.ProcessedChunkColumns = processedColumns;
			UpdateEta(status, written, coverage.Tiles.Count);
			if (written == coverage.Tiles.Count || (written & 0xF) == 0)
			{
				status.Message = $"Full live rebuild: {written:n0}/{coverage.Tiles.Count:n0} tiles";
				publishStatus(status.Clone());
			}
			LogConsoleProgress("forced-full", written, coverage.Tiles.Count, processedColumns, status);
			await Task.Yield();
		}
		if (written > 0)
		{
			ValidateAndLogSurfaceDiagnostics(coverage);
		}
		SetProgressPhase(status, "overview", 92.0, "Building map overview…");
		if (config.ExportNormalTerrain)
		{
			WriteOverview(stageDir, manifest, status);
		}
		SetProgressPhase(status, "pyramid", 96.0, "Building zoom levels…");
		if (config.ExportNormalTerrain)
		{
			WriteLodPyramid(stageDir, manifest, status, config);
		}
		WriteDerivedStyleLodPyramids(stageDir, manifest, status, config);
		SetProgressPhase(status, "finalizing", 99.0, "Publishing terrain cache…");
		FinalizeManifest(manifest, coverage, written, processedColumns);
		WriteManifest(stageDir, manifest);
		AtomicSwap(stageDir, currentDir, backupDir);
		return true;
	}

	private async Task<bool> IncrementalExportRuntimeAsync(WebMapConfig config, TerrainCoverage coverage, string stageDir, string currentDir, WebMapTerrainManifest manifest, HashSet<WebMapDirtyChunkCoord> dirtySnapshot, WebMapTerrainExportStatus status, WebMapSaveDbTerrainReader? directReader, HashSet<ChunkCoord> forceLiveChunks, CancellationToken cancellationToken)
	{
		if (Directory.Exists(stageDir))
		{
			Directory.Delete(stageDir, recursive: true);
		}
		Directory.CreateDirectory(stageDir);
		Directory.CreateDirectory(currentDir);
		if (manifest.Tiles == null)
		{
			manifest.Tiles = new Dictionary<string, WebMapTerrainTileMetadata>(StringComparer.Ordinal);
		}
		HashSet<TileCoord> dirtyTiles = CollectDirtyTiles(config, coverage, currentDir, manifest, dirtySnapshot);
		Dictionary<TileCoord, HashSet<ChunkCoord>> dirtyChunksByTile = CollectDirtyChunksByTile(config, dirtySnapshot);
		List<string> staleKeys = CollectStaleTileKeys(coverage, manifest);
		bool metadataRepairNeeded = (config.ExportNormalTerrain && !File.Exists(Path.Combine(currentDir, "overview.png"))) || !File.Exists(Path.Combine(currentDir, "manifest.json")) || (config.ExportNormalTerrain && !Directory.Exists(Path.Combine(currentDir, "lod")));
		status.TotalTiles = dirtyTiles.Count;
		status.ReusedTiles = Math.Max(0, coverage.Tiles.Count - dirtyTiles.Count);
		status.Message = ((dirtyTiles.Count == 0) ? $"Changed-terrain update: no changed/new tiles; reusing {status.ReusedTiles:n0} cached tiles." : $"Changed-terrain update: rendering {dirtyTiles.Count:n0} changed/new tiles and reusing {status.ReusedTiles:n0}.");
		publishStatus(status.Clone());
		api.Logger.Notification("[ServerEssentials/WebMap] changed-terrain live update: rendering {0:n0} changed/new tiles, reusing {1:n0}; {2:n0} stale tiles pending removal.", dirtyTiles.Count, status.ReusedTiles, staleKeys.Count);
		progressPhaseStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		ResetEtaSamples();
		status.Phase = ((dirtyTiles.Count == 0) ? "finalizing" : "rendering");
		status.ProgressPercent = ((dirtyTiles.Count == 0) ? 90 : 5);
		status.EstimatedRemainingMilliseconds = ((dirtyTiles.Count != 0) ? (-1) : 0);
		status.AverageMillisecondsPerTile = 0.0;
		publishStatus(status.Clone());
		LogConsoleProgress("runtime-update", 0, dirtyTiles.Count, 0L, status, force: true);
		if (dirtyTiles.Count == 0 && staleKeys.Count == 0 && !metadataRepairNeeded)
		{
			status.WrittenTiles = 0;
			status.ProcessedChunkColumns = 0L;
			try
			{
				Directory.Delete(stageDir, recursive: true);
			}
			catch
			{
			}
			return true;
		}
		int written = 0;
		long processedColumns = 0L;
		foreach (TileCoord tile in from t in dirtyTiles
			orderby t.Z, t.X
			select t)
		{
			cancellationToken.ThrowIfCancellationRequested();
			dirtyChunksByTile.TryGetValue(tile, out HashSet<ChunkCoord> patchChunks);
			RuntimeRenderedTile rendered = await RenderTileRuntimeAsync(config, tile, stageDir, coverage.Chunks, status, directReader, forceLiveChunks, cancellationToken, currentDir, patchChunks).ConfigureAwait(continueOnCapturedContext: false);
			processedColumns += rendered.ProcessedColumns;
			manifest.Tiles[TileKey(tile.X, tile.Z)] = new WebMapTerrainTileMetadata
			{
				X = tile.X,
				Z = tile.Z,
				R = rendered.Tile.Average.R,
				G = rendered.Tile.Average.G,
				B = rendered.Tile.Average.B,
				ChunkRgb = rendered.Tile.ChunkRgb,
				GeneratedChunkCount = CountGeneratedChunksForTile(config, tile, coverage.Chunks),
				LastGeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
			};
			written = (status.WrittenTiles = written + 1);
			status.ProcessedChunkColumns = processedColumns;
			UpdateEta(status, written, dirtyTiles.Count);
			if (written == dirtyTiles.Count || (written & 0xF) == 0)
			{
				status.Message = $"Changed-terrain update: {written:n0}/{dirtyTiles.Count:n0} changed/new tiles";
				publishStatus(status.Clone());
			}
			LogConsoleProgress("runtime-update", written, dirtyTiles.Count, processedColumns, status);
			await Task.Yield();
			patchChunks = null;
		}
		if (written > 0)
		{
			ValidateAndLogSurfaceDiagnostics(coverage);
		}
		SetProgressPhase(status, "overview", 92.0, "Updating map overview…");
		if (config.ExportNormalTerrain)
		{
			WriteOverview(stageDir, manifest, status);
		}
		SetProgressPhase(status, "pyramid", 96.0, "Updating zoom levels…");
		bool replaceWholeLod = false;
		HashSet<LodTileCoord> lodTiles = (config.ExportNormalTerrain ? StageIncrementalLodTiles(stageDir, currentDir, manifest, dirtyTiles, staleKeys, status, config, out replaceWholeLod) : new HashSet<LodTileCoord>());
		bool replaceWholeSepiaLod = false;
		HashSet<LodTileCoord> sepiaLodTiles = (config.ExportSepiaTerrain ? StageIncrementalLodTiles(StyleRoot(stageDir, "sepia"), StyleRoot(currentDir, "sepia"), manifest, dirtyTiles, staleKeys, status, config, out replaceWholeSepiaLod) : new HashSet<LodTileCoord>());
		bool replaceWholeTopoLod = false;
		HashSet<LodTileCoord> topoLodTiles = (config.ExportTopographicTerrain ? StageIncrementalLodTiles(StyleRoot(stageDir, "topo"), StyleRoot(currentDir, "topo"), manifest, dirtyTiles, staleKeys, status, config, out replaceWholeTopoLod) : new HashSet<LodTileCoord>());
		SetProgressPhase(status, "finalizing", 99.0, "Publishing changed terrain…");
		FinalizeManifest(manifest, coverage, manifest.Tiles.Count, coverage.ChunkColumns);
		WriteManifest(stageDir, manifest);
		CommitIncrementalStage(stageDir, currentDir, dirtyTiles, staleKeys, lodTiles, replaceWholeLod, sepiaLodTiles, replaceWholeSepiaLod, topoLodTiles, replaceWholeTopoLod, config.ExportNormalTerrain, config.ExportSepiaTerrain, config.ExportTopographicTerrain);
		return true;
	}

	private async Task<RuntimeRenderedTile> RenderTileRuntimeAsync(WebMapConfig config, TileCoord tile, string outputDir, HashSet<ChunkCoord> generatedChunks, WebMapTerrainExportStatus status, WebMapSaveDbTerrainReader? directReader, HashSet<ChunkCoord> forceLiveChunks, CancellationToken cancellationToken, string? rawSourceDir = null, HashSet<ChunkCoord>? patchChunks = null)
	{
		int pixelBytes = checked(config.TerrainTileSize * config.TerrainTileSize * 4);
		byte[] rgba = ArrayPool<byte>.Shared.Rent(pixelBytes);
		try
		{
			bool patchMode = rawSourceDir != null && patchChunks != null && patchChunks.Count > 0 && TryReadRawTile(rawSourceDir, tile.X, tile.Z, rgba, pixelBytes);
			if (!patchMode)
			{
				FillBackground(rgba, pixelBytes);
			}
			int tileBlockX = tile.X * config.TerrainTileSize;
			int tileBlockZ = tile.Z * config.TerrainTileSize;
			int firstChunkX = FloorDiv(tileBlockX, 32);
			int firstChunkZ = FloorDiv(tileBlockZ, 32);
			int chunksWide = Math.Max(1, config.TerrainTileSize / 32);
			long processed = 0L;
			for (int dz = 0; dz < chunksWide; dz++)
			{
				for (int dx = 0; dx < chunksWide; dx++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					int chunkX = firstChunkX + dx;
					int chunkZ = firstChunkZ + dz;
					ChunkCoord chunkCoord = new ChunkCoord(chunkX, chunkZ);
					if (patchMode && !patchChunks.Contains(chunkCoord))
					{
						continue;
					}
					if (patchMode)
					{
						ClearChunkPatch(rgba, config.TerrainTileSize, dx * 32, dz * 32);
					}
					Interlocked.Increment(ref chunkExistenceChecks);
					if (generatedChunks.Contains(chunkCoord))
					{
						Interlocked.Increment(ref existingChunkColumns);
						if (!forceLiveChunks.Contains(chunkCoord) && directReader != null && directReader.IsActive && TryRenderDatabaseColumn(directReader, chunkX, chunkZ, rgba, config.TerrainTileSize, dx * 32, dz * 32, status))
						{
							processed++;
							LogConsoleTileHeartbeat(tile, processed, status);
							continue;
						}
						Stopwatch loadSw = Stopwatch.StartNew();
						await LoadAndRenderRuntimeChunkAsync(chunkX, chunkZ, rgba, config.TerrainTileSize, dx * 32, dz * 32, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						loadSw.Stop();
						status.WorldReadMilliseconds += loadSw.ElapsedMilliseconds;
						processed++;
						LogConsoleTileHeartbeat(tile, processed, status);
					}
				}
			}
			WriteRawTile(outputDir, tile.X, tile.Z, rgba, pixelBytes);
			WriteDerivedTerrainStyles(outputDir, tile.X, tile.Z, rgba, config.TerrainTileSize, config.ExportSepiaTerrain, config.ExportTopographicTerrain);
			Stopwatch shadeSw = Stopwatch.StartNew();
			ApplyHillshade(rgba, config.TerrainTileSize);
			shadeSw.Stop();
			status.RenderMilliseconds += shadeSw.ElapsedMilliseconds;
			RenderedTile summary = SummarizeTile(rgba, config.TerrainTileSize);
			if (config.ExportNormalTerrain)
			{
				string xDir = Path.Combine(outputDir, tile.X.ToString(CultureInfo.InvariantCulture));
				Directory.CreateDirectory(xDir);
				string finalPath = Path.Combine(xDir, tile.Z.ToString(CultureInfo.InvariantCulture) + ".png");
				Stopwatch pngSw = Stopwatch.StartNew();
				PngWriter.WriteRgba(finalPath, config.TerrainTileSize, config.TerrainTileSize, rgba);
				pngSw.Stop();
				status.PngWriteMilliseconds += pngSw.ElapsedMilliseconds;
			}
			status.BlockColorCacheEntries = blockColorCache.Count;
			return new RuntimeRenderedTile(summary, processed);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rgba);
		}
	}

	private bool TryRenderDatabaseColumn(WebMapSaveDbTerrainReader directReader, int chunkX, int chunkZ, byte[] rgba, int tileSize, int pixelOffsetX, int pixelOffsetZ, WebMapTerrainExportStatus status)
	{
		bool flag = TryRenderDatabaseColumnCore(directReader, chunkX, chunkZ, rgba, tileSize, pixelOffsetX, pixelOffsetZ, out var worldReadMilliseconds, out var renderMilliseconds);
		status.WorldReadMilliseconds += worldReadMilliseconds;
		status.RenderMilliseconds += renderMilliseconds;
		if (!flag && !directReader.IsActive)
		{
			status.TerrainReadMode = "save-db-unavailable";
		}
		return flag;
	}

	private bool TryRenderDatabaseColumnCore(WebMapSaveDbTerrainReader directReader, int chunkX, int chunkZ, byte[] rgba, int tileSize, int pixelOffsetX, int pixelOffsetZ, out long worldReadMilliseconds, out long renderMilliseconds)
	{
		worldReadMilliseconds = 0L;
		renderMilliseconds = 0L;
		int num = Math.Max(1, (api.WorldManager.MapSizeY + 32 - 1) / 32);
		IServerChunk[] array = ArrayPool<IServerChunk>.Shared.Rent(num);
		Array.Clear(array, 0, array.Length);
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			if (!directReader.TryLoadColumn(chunkX, chunkZ, api.WorldManager.MapSizeY, array, out ushort[] rainHeightMap))
			{
				stopwatch.Stop();
				worldReadMilliseconds += stopwatch.ElapsedMilliseconds;
				return false;
			}
			stopwatch.Stop();
			worldReadMilliseconds += stopwatch.ElapsedMilliseconds;
			Interlocked.Increment(ref loadedChunkColumns);
			Stopwatch stopwatch2 = Stopwatch.StartNew();
			RenderChunkColumn(chunkX, chunkZ, array, null, rgba, tileSize, pixelOffsetX, pixelOffsetZ, rainHeightMap);
			stopwatch2.Stop();
			renderMilliseconds += stopwatch2.ElapsedMilliseconds;
			return true;
		}
		finally
		{
			if (stopwatch.IsRunning)
			{
				stopwatch.Stop();
				worldReadMilliseconds += stopwatch.ElapsedMilliseconds;
			}
			for (int i = 0; i < num && i < array.Length; i++)
			{
				try
				{
					array[i]?.Dispose();
				}
				catch
				{
				}
				array[i] = null;
			}
			ArrayPool<IServerChunk>.Shared.Return(array, clearArray: true);
		}
	}

	private Task LoadAndRenderRuntimeChunkAsync(int chunkX, int chunkZ, byte[] rgba, int tileSize, int pixelOffsetX, int pixelOffsetZ, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		api.Event.EnqueueMainThreadTask(() =>
		{
			try
			{
				if (api.WorldManager.GetChunk(chunkX, 0, chunkZ) != null)
				{
					RenderLoadedColumn();
				}
				else
				{
					api.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ, new ChunkLoadOptions
					{
						KeepLoaded = false,
						OnLoaded = () =>
						{
							api.Event.EnqueueMainThreadTask(RenderLoadedColumn, "nses-webmap-force-render-column");
						}
					});
				}
			}
			catch (Exception exception)
			{
				tcs.TrySetException(exception);
			}
		}, "nses-webmap-force-load-column");
		return tcs.Task;
		void RenderLoadedColumn()
		{
			IServerChunk[] array = null;
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				int num = Math.Max(1, (api.WorldManager.MapSizeY + 32 - 1) / 32);
				array = ArrayPool<IServerChunk>.Shared.Rent(num);
				Array.Clear(array, 0, array.Length);
				bool flag = false;
				for (int i = 0; i < num; i++)
				{
					array[i] = api.WorldManager.GetChunk(chunkX, i, chunkZ);
					if (array[i] != null)
					{
						flag = true;
					}
				}
				if (flag)
				{
					loadedChunkColumns++;
				}
				IMapChunk mapChunk = null;
				try
				{
					mapChunk = api.WorldManager.GetMapChunk(chunkX, chunkZ);
				}
				catch
				{
				}
				if (mapChunk == null)
				{
					try
					{
						mapChunk = api.World.BlockAccessor.GetMapChunk(chunkX, chunkZ);
					}
					catch
					{
					}
				}
				if (mapChunk != null)
				{
					mapChunksResolved++;
				}
				RenderChunkColumn(chunkX, chunkZ, array, mapChunk, rgba, tileSize, pixelOffsetX, pixelOffsetZ);
				tcs.TrySetResult(result: true);
			}
			catch (Exception exception)
			{
				tcs.TrySetException(exception);
			}
			finally
			{
				if (array != null)
				{
					ArrayPool<IServerChunk>.Shared.Return(array, clearArray: true);
				}
			}
		}
	}

	private void FullExport(WebMapConfig config, TerrainCoverage coverage, string stageDir, string currentDir, string backupDir, WebMapTerrainExportStatus status, WebMapSaveDbTerrainReader? directReader, string savePath)
	{
		if (Directory.Exists(stageDir))
		{
			Directory.Delete(stageDir, recursive: true);
		}
		Directory.CreateDirectory(stageDir);
		status.Message = ((coverage.Tiles.Count == 0) ? "No generated overworld map chunks were found." : $"Full terrain seed: rendering {coverage.Tiles.Count:n0} cached tiles from {coverage.ChunkColumns:n0} generated chunk columns…");
		publishStatus(status.Clone());
		api.Logger.Notification("[ServerEssentials/WebMap] full terrain export: rendering {0:n0} tiles from {1:n0} generated chunk columns.", coverage.Tiles.Count, coverage.ChunkColumns);
		progressPhaseStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		ResetEtaSamples();
		status.Phase = "rendering";
		status.ProgressPercent = 5.0;
		status.EstimatedRemainingMilliseconds = -1L;
		status.AverageMillisecondsPerTile = 0.0;
		publishStatus(status.Clone());
		LogConsoleProgress("full", 0, coverage.Tiles.Count, 0L, status, force: true);
		WebMapTerrainManifest manifest = CreateManifest(config, coverage);
		int written = 0;
		long processedColumns = 0L;
		int num = Math.Clamp(config.TerrainBaseRenderWorkers, 1, 4);
		bool flag = directReader != null && directReader.IsActive && num > 1 && coverage.Tiles.Count > 1;
		if (flag)
		{
			List<WebMapSaveDbTerrainReader> list = new List<WebMapSaveDbTerrainReader>();
			try
			{
				for (int i = 0; i < num; i++)
				{
					WebMapSaveDbTerrainReader webMapSaveDbTerrainReader = WebMapSaveDbTerrainReader.TryOpen(api, savePath, out string reason);
					if (webMapSaveDbTerrainReader == null)
					{
						api.Logger.Warning("[ServerEssentials/WebMap] could not open save-DB terrain worker {0}: {1}", i + 1, reason);
						break;
					}
					list.Add(webMapSaveDbTerrainReader);
				}
				if (list.Count >= 2)
				{
					status.TerrainReadMode = $"save-db-parallel-{list.Count}";
					api.Logger.Notification("[ServerEssentials/WebMap] full terrain export using {0} parallel read-only save-DB terrain workers.", list.Count);
					ConcurrentQueue<WebMapSaveDbTerrainReader> readerPool = new ConcurrentQueue<WebMapSaveDbTerrainReader>(list);
					ConcurrentBag<TileCoord> failedTiles = new ConcurrentBag<TileCoord>();
					object progressGate = new object();
					ParallelOptions parallelOptions = new ParallelOptions
					{
						MaxDegreeOfParallelism = list.Count
					};
					Parallel.ForEach(from t in coverage.Tiles
						orderby t.Z, t.X
						select t, parallelOptions, (TileCoord tileCoord) =>
					{
						if (!readerPool.TryDequeue(out WebMapSaveDbTerrainReader result))
						{
							failedTiles.Add(tileCoord);
							return;
						}
						try
						{
							if (!TryRenderTileDatabaseOnly(config, tileCoord, stageDir, coverage.Chunks, result, out var rendered))
							{
								failedTiles.Add(tileCoord);
								return;
							}
							lock (progressGate)
							{
								manifest.Tiles[TileKey(tileCoord.X, tileCoord.Z)] = new WebMapTerrainTileMetadata
								{
									X = tileCoord.X,
									Z = tileCoord.Z,
									R = rendered.Tile.Average.R,
									G = rendered.Tile.Average.G,
									B = rendered.Tile.Average.B,
									ChunkRgb = rendered.Tile.ChunkRgb,
									GeneratedChunkCount = CountGeneratedChunksForTile(config, tileCoord, coverage.Chunks),
									LastGeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
								};
								int num4 = written;
								written = num4 + 1;
								processedColumns += rendered.ProcessedColumns;
								status.WorldReadMilliseconds += rendered.WorldReadMilliseconds;
								status.RenderMilliseconds += rendered.RenderMilliseconds;
								status.PngWriteMilliseconds += rendered.PngWriteMilliseconds;
								status.WrittenTiles = written;
								status.ProcessedChunkColumns = processedColumns;
								UpdateEta(status, written, coverage.Tiles.Count);
								if (written == coverage.Tiles.Count || (written & 0xF) == 0)
								{
									status.Message = $"Full terrain seed: {written:n0}/{coverage.Tiles.Count:n0} tiles";
									publishStatus(status.Clone());
								}
								LogConsoleProgress("full-db-parallel", written, coverage.Tiles.Count, processedColumns, status);
							}
						}
						finally
						{
							readerPool.Enqueue(result);
						}
					});
					TileCoord[] array = (from t in failedTiles.Distinct()
						orderby t.Z, t.X
						select t).ToArray();
					if (array.Length != 0)
					{
						status.TerrainReadMode = "save-db-parallel+serial-retry";
						api.Logger.Warning("[ServerEssentials/WebMap] {0:n0} terrain tiles missed the parallel DB fast path and will be retried through the serial save-DB path.", array.Length);
						TileCoord[] array2 = array;
						for (int num2 = 0; num2 < array2.Length; num2++)
						{
							TileCoord tile = array2[num2];
							RenderedTile renderedTile = RenderTile(config, tile, stageDir, coverage.Chunks, ref processedColumns, status, directReader);
							manifest.Tiles[TileKey(tile.X, tile.Z)] = new WebMapTerrainTileMetadata
							{
								X = tile.X,
								Z = tile.Z,
								R = renderedTile.Average.R,
								G = renderedTile.Average.G,
								B = renderedTile.Average.B,
								ChunkRgb = renderedTile.ChunkRgb,
								GeneratedChunkCount = CountGeneratedChunksForTile(config, tile, coverage.Chunks),
								LastGeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
							};
							int num3 = written;
							written = num3 + 1;
							status.WrittenTiles = written;
							status.ProcessedChunkColumns = processedColumns;
							UpdateEta(status, written, coverage.Tiles.Count);
							LogConsoleProgress("full-fallback", written, coverage.Tiles.Count, processedColumns, status);
						}
					}
				}
				else
				{
					flag = false;
				}
			}
			finally
			{
				foreach (WebMapSaveDbTerrainReader item in list)
				{
					item.Dispose();
				}
			}
		}
		if (!flag)
		{
			foreach (TileCoord item2 in from t in coverage.Tiles
				orderby t.Z, t.X
				select t)
			{
				RenderedTile renderedTile2 = RenderTile(config, item2, stageDir, coverage.Chunks, ref processedColumns, status, directReader);
				manifest.Tiles[TileKey(item2.X, item2.Z)] = new WebMapTerrainTileMetadata
				{
					X = item2.X,
					Z = item2.Z,
					R = renderedTile2.Average.R,
					G = renderedTile2.Average.G,
					B = renderedTile2.Average.B,
					ChunkRgb = renderedTile2.ChunkRgb,
					GeneratedChunkCount = CountGeneratedChunksForTile(config, item2, coverage.Chunks),
					LastGeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
				};
				written++;
				status.WrittenTiles = written;
				status.ProcessedChunkColumns = processedColumns;
				UpdateEta(status, written, coverage.Tiles.Count);
				if (written == coverage.Tiles.Count || (written & 0xF) == 0)
				{
					status.Message = $"Full terrain seed: {written:n0}/{coverage.Tiles.Count:n0} tiles";
					publishStatus(status.Clone());
				}
				LogConsoleProgress("full", written, coverage.Tiles.Count, processedColumns, status);
			}
		}
		ValidateAndLogSurfaceDiagnostics(coverage);
		SetProgressPhase(status, "overview", 92.0, "Building map overview…");
		if (config.ExportNormalTerrain)
		{
			WriteOverview(stageDir, manifest, status);
		}
		SetProgressPhase(status, "pyramid", 96.0, "Building zoom levels…");
		if (config.ExportNormalTerrain)
		{
			WriteLodPyramid(stageDir, manifest, status, config);
		}
		WriteDerivedStyleLodPyramids(stageDir, manifest, status, config);
		SetProgressPhase(status, "finalizing", 99.0, "Publishing terrain cache…");
		FinalizeManifest(manifest, coverage, written, processedColumns);
		WriteManifest(stageDir, manifest);
		AtomicSwap(stageDir, currentDir, backupDir);
		status.ReusedTiles = 0;
	}

	private void IncrementalExport(WebMapConfig config, TerrainCoverage coverage, string stageDir, string currentDir, WebMapTerrainManifest manifest, HashSet<WebMapDirtyChunkCoord> dirtySnapshot, WebMapTerrainExportStatus status, WebMapSaveDbTerrainReader? directReader)
	{
		if (Directory.Exists(stageDir))
		{
			Directory.Delete(stageDir, recursive: true);
		}
		Directory.CreateDirectory(stageDir);
		Directory.CreateDirectory(currentDir);
		if (manifest.Tiles == null)
		{
			Dictionary<string, WebMapTerrainTileMetadata> dictionary = (manifest.Tiles = new Dictionary<string, WebMapTerrainTileMetadata>(StringComparer.Ordinal));
		}
		HashSet<TileCoord> hashSet = CollectDirtyTiles(config, coverage, currentDir, manifest, dirtySnapshot);
		Dictionary<TileCoord, HashSet<ChunkCoord>> dictionary3 = CollectDirtyChunksByTile(config, dirtySnapshot);
		List<string> list = CollectStaleTileKeys(coverage, manifest);
		bool flag = !File.Exists(Path.Combine(currentDir, "overview.png")) || !File.Exists(Path.Combine(currentDir, "manifest.json")) || !Directory.Exists(Path.Combine(currentDir, "lod"));
		status.TotalTiles = hashSet.Count;
		status.ReusedTiles = Math.Max(0, coverage.Tiles.Count - hashSet.Count);
		status.Message = ((hashSet.Count == 0) ? $"Incremental terrain: no changed/new tiles; reusing {status.ReusedTiles:n0} cached tiles." : $"Incremental terrain: staging {hashSet.Count:n0} changed/new tiles and reusing {status.ReusedTiles:n0}.");
		publishStatus(status.Clone());
		api.Logger.Notification("[ServerEssentials/WebMap] incremental terrain export: staging {0:n0} changed/new tiles, reusing {1:n0}; {2:n0} stale tiles pending removal after validation.", hashSet.Count, status.ReusedTiles, list.Count);
		progressPhaseStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		ResetEtaSamples();
		status.Phase = ((hashSet.Count == 0) ? "finalizing" : "rendering");
		status.ProgressPercent = ((hashSet.Count == 0) ? 90 : 5);
		status.EstimatedRemainingMilliseconds = ((hashSet.Count != 0) ? (-1) : 0);
		status.AverageMillisecondsPerTile = 0.0;
		publishStatus(status.Clone());
		LogConsoleProgress("incremental", 0, hashSet.Count, 0L, status, force: true);
		if (hashSet.Count == 0 && list.Count == 0 && !flag)
		{
			status.WrittenTiles = 0;
			status.ProcessedChunkColumns = 0L;
			try
			{
				Directory.Delete(stageDir, recursive: true);
				return;
			}
			catch
			{
				return;
			}
		}
		int num = 0;
		long processedColumns = 0L;
		foreach (TileCoord item in from t in hashSet
			orderby t.Z, t.X
			select t)
		{
			dictionary3.TryGetValue(item, out var value);
			RenderedTile renderedTile = RenderTile(config, item, stageDir, coverage.Chunks, ref processedColumns, status, directReader, atomicReplace: false, currentDir, value);
			manifest.Tiles[TileKey(item.X, item.Z)] = new WebMapTerrainTileMetadata
			{
				X = item.X,
				Z = item.Z,
				R = renderedTile.Average.R,
				G = renderedTile.Average.G,
				B = renderedTile.Average.B,
				ChunkRgb = renderedTile.ChunkRgb,
				GeneratedChunkCount = CountGeneratedChunksForTile(config, item, coverage.Chunks),
				LastGeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
			};
			num = (status.WrittenTiles = num + 1);
			status.ProcessedChunkColumns = processedColumns;
			UpdateEta(status, num, hashSet.Count);
			if (num == hashSet.Count || (num & 0xF) == 0)
			{
				status.Message = $"Incremental terrain: {num:n0}/{hashSet.Count:n0} changed/new tiles staged";
				publishStatus(status.Clone());
			}
			LogConsoleProgress("incremental", num, hashSet.Count, processedColumns, status);
		}
		if (num > 0)
		{
			ValidateAndLogSurfaceDiagnostics(coverage);
		}
		SetProgressPhase(status, "overview", 92.0, "Updating map overview…");
		if (config.ExportNormalTerrain)
		{
			WriteOverview(stageDir, manifest, status);
		}
		SetProgressPhase(status, "pyramid", 96.0, "Updating zoom levels…");
		bool replaceWholeLod = false;
		HashSet<LodTileCoord> lodTiles = (config.ExportNormalTerrain ? StageIncrementalLodTiles(stageDir, currentDir, manifest, hashSet, list, status, config, out replaceWholeLod) : new HashSet<LodTileCoord>());
		bool replaceWholeLod2 = false;
		HashSet<LodTileCoord> sepiaLodTiles = (config.ExportSepiaTerrain ? StageIncrementalLodTiles(StyleRoot(stageDir, "sepia"), StyleRoot(currentDir, "sepia"), manifest, hashSet, list, status, config, out replaceWholeLod2) : new HashSet<LodTileCoord>());
		bool replaceWholeLod3 = false;
		HashSet<LodTileCoord> topoLodTiles = (config.ExportTopographicTerrain ? StageIncrementalLodTiles(StyleRoot(stageDir, "topo"), StyleRoot(currentDir, "topo"), manifest, hashSet, list, status, config, out replaceWholeLod3) : new HashSet<LodTileCoord>());
		SetProgressPhase(status, "finalizing", 99.0, "Publishing changed terrain…");
		FinalizeManifest(manifest, coverage, manifest.Tiles.Count, coverage.ChunkColumns);
		WriteManifest(stageDir, manifest);
		CommitIncrementalStage(stageDir, currentDir, hashSet, list, lodTiles, replaceWholeLod, sepiaLodTiles, replaceWholeLod2, topoLodTiles, replaceWholeLod3, config.ExportNormalTerrain, config.ExportSepiaTerrain, config.ExportTopographicTerrain);
	}

	private static Dictionary<TileCoord, HashSet<ChunkCoord>> CollectDirtyChunksByTile(WebMapConfig config, HashSet<WebMapDirtyChunkCoord> dirtySnapshot)
	{
		Dictionary<TileCoord, HashSet<ChunkCoord>> dictionary = new Dictionary<TileCoord, HashSet<ChunkCoord>>();
		foreach (WebMapDirtyChunkCoord item in dirtySnapshot)
		{
			if (item.Dimension == 0)
			{
				int value = item.ChunkX * 32;
				int value2 = item.ChunkZ * 32;
				TileCoord key = new TileCoord(FloorDiv(value, config.TerrainTileSize), FloorDiv(value2, config.TerrainTileSize));
				if (!dictionary.TryGetValue(key, out var value3))
				{
					value3 = (dictionary[key] = new HashSet<ChunkCoord>());
				}
				value3.Add(new ChunkCoord(item.ChunkX, item.ChunkZ));
			}
		}
		return dictionary;
	}

	private static HashSet<TileCoord> CollectDirtyTiles(WebMapConfig config, TerrainCoverage coverage, string currentDir, WebMapTerrainManifest manifest, HashSet<WebMapDirtyChunkCoord> dirtySnapshot)
	{
		HashSet<TileCoord> hashSet = new HashSet<TileCoord>();
		foreach (WebMapDirtyChunkCoord item in dirtySnapshot)
		{
			if (item.Dimension == 0)
			{
				int value = item.ChunkX * 32;
				int value2 = item.ChunkZ * 32;
				hashSet.Add(new TileCoord(FloorDiv(value, config.TerrainTileSize), FloorDiv(value2, config.TerrainTileSize)));
			}
		}
		foreach (TileCoord tile in coverage.Tiles)
		{
			string key = TileKey(tile.X, tile.Z);
			int num = CountGeneratedChunksForTile(config, tile, coverage.Chunks);
			if (!manifest.Tiles.TryGetValue(key, out WebMapTerrainTileMetadata value3) || value3.GeneratedChunkCount != num || (config.ExportNormalTerrain && !File.Exists(TilePath(currentDir, tile.X, tile.Z))) || !File.Exists(RawTilePath(currentDir, tile.X, tile.Z)) || (config.ExportSepiaTerrain && !File.Exists(StyleTilePath(currentDir, "sepia", tile.X, tile.Z))) || (config.ExportTopographicTerrain && !File.Exists(StyleTilePath(currentDir, "topo", tile.X, tile.Z))))
			{
				hashSet.Add(tile);
			}
		}
		hashSet.IntersectWith(coverage.Tiles);
		return hashSet;
	}

	private static int CountGeneratedChunksForTile(WebMapConfig config, TileCoord tile, HashSet<ChunkCoord> generatedChunks)
	{
		int value = tile.X * config.TerrainTileSize;
		int value2 = tile.Z * config.TerrainTileSize;
		int num = FloorDiv(value, 32);
		int num2 = FloorDiv(value2, 32);
		int num3 = Math.Max(1, config.TerrainTileSize / 32);
		int num4 = 0;
		for (int i = 0; i < num3; i++)
		{
			for (int j = 0; j < num3; j++)
			{
				if (generatedChunks.Contains(new ChunkCoord(num + j, num2 + i)))
				{
					num4++;
				}
			}
		}
		return num4;
	}

	private static List<string> CollectStaleTileKeys(TerrainCoverage coverage, WebMapTerrainManifest manifest)
	{
		HashSet<string> hashSet = new HashSet<string>(coverage.Tiles.Count, StringComparer.Ordinal);
		foreach (TileCoord tile in coverage.Tiles)
		{
			hashSet.Add(TileKey(tile.X, tile.Z));
		}
		List<string> list = new List<string>();
		foreach (string key in manifest.Tiles.Keys)
		{
			if (!hashSet.Contains(key))
			{
				list.Add(key);
			}
		}
		foreach (string item in list)
		{
			manifest.Tiles.Remove(item);
		}
		return list;
	}

	private static void CommitIncrementalStage(string stageDir, string currentDir, HashSet<TileCoord> dirtyTiles, List<string> staleKeys, HashSet<LodTileCoord> lodTiles, bool replaceWholeLod, HashSet<LodTileCoord> sepiaLodTiles, bool replaceWholeSepiaLod, HashSet<LodTileCoord> topoLodTiles, bool replaceWholeTopoLod, bool exportNormal, bool exportSepia, bool exportTopo)
	{
		string rollbackDir = stageDir + "-rollback";
		if (Directory.Exists(rollbackDir))
		{
			Directory.Delete(rollbackDir, recursive: true);
		}
		Directory.CreateDirectory(rollbackDir);
		Directory.CreateDirectory(currentDir);
		List<(string Target, string? Backup)> touched = new List<(string, string)>();
		string text = null;
		List<(string Current, string Backup)> styleLodBackups = new List<(string, string)>();
		try
		{
			foreach (TileCoord dirtyTile in dirtyTiles)
			{
				if (exportNormal)
				{
					string text2 = TilePath(stageDir, dirtyTile.X, dirtyTile.Z);
					if (!File.Exists(text2))
					{
						throw new FileNotFoundException("Staged incremental terrain tile is missing.", text2);
					}
					string text3 = TilePath(currentDir, dirtyTile.X, dirtyTile.Z);
					BackupTarget(text3);
					string directoryName = Path.GetDirectoryName(text3);
					if (!string.IsNullOrEmpty(directoryName))
					{
						Directory.CreateDirectory(directoryName);
					}
					File.Move(text2, text3, overwrite: true);
				}
				string text4 = RawTilePath(stageDir, dirtyTile.X, dirtyTile.Z);
				string text5 = RawTilePath(currentDir, dirtyTile.X, dirtyTile.Z);
				if (!File.Exists(text4))
				{
					throw new FileNotFoundException("Staged incremental raw terrain tile is missing.", text4);
				}
				BackupTarget(text5);
				string directoryName2 = Path.GetDirectoryName(text5);
				if (!string.IsNullOrEmpty(directoryName2))
				{
					Directory.CreateDirectory(directoryName2);
				}
				File.Move(text4, text5, overwrite: true);
				string[] derivedTerrainStyles = DerivedTerrainStyles;
				foreach (string text6 in derivedTerrainStyles)
				{
					if ((!(text6 == "sepia") || exportSepia) && (!(text6 == "topo") || exportTopo))
					{
						string text7 = StyleTilePath(stageDir, text6, dirtyTile.X, dirtyTile.Z);
						if (!File.Exists(text7))
						{
							throw new FileNotFoundException("Staged incremental " + text6 + " terrain tile is missing.", text7);
						}
						string text8 = StyleTilePath(currentDir, text6, dirtyTile.X, dirtyTile.Z);
						BackupTarget(text8);
						string directoryName3 = Path.GetDirectoryName(text8);
						if (!string.IsNullOrEmpty(directoryName3))
						{
							Directory.CreateDirectory(directoryName3);
						}
						File.Move(text7, text8, overwrite: true);
					}
				}
			}
			foreach (string staleKey in staleKeys)
			{
				if (!TryParseTileKey(staleKey, out var x, out var z))
				{
					continue;
				}
				string text9 = TilePath(currentDir, x, z);
				BackupTarget(text9);
				if (File.Exists(text9))
				{
					File.Delete(text9);
				}
				string text10 = RawTilePath(currentDir, x, z);
				BackupTarget(text10);
				if (File.Exists(text10))
				{
					File.Delete(text10);
				}
				string[] derivedTerrainStyles2 = DerivedTerrainStyles;
				foreach (string style in derivedTerrainStyles2)
				{
					string text11 = StyleTilePath(currentDir, style, x, z);
					BackupTarget(text11);
					if (File.Exists(text11))
					{
						File.Delete(text11);
					}
				}
			}
			if (exportNormal)
			{
				string text12 = Path.Combine(stageDir, "overview.png");
				if (!File.Exists(text12))
				{
					throw new FileNotFoundException("Staged incremental terrain overview is missing.", text12);
				}
				string text13 = Path.Combine(currentDir, "overview.png");
				BackupTarget(text13);
				File.Move(text12, text13, overwrite: true);
			}
			string text14 = Path.Combine(stageDir, "lod");
			string text15 = Path.Combine(currentDir, "lod");
			if (exportNormal & replaceWholeLod)
			{
				if (!Directory.Exists(text14))
				{
					throw new DirectoryNotFoundException("Staged LOD terrain directory is missing.");
				}
				if (Directory.Exists(text15))
				{
					text = Path.Combine(rollbackDir, "lod");
					if (Directory.Exists(text))
					{
						Directory.Delete(text, recursive: true);
					}
					Directory.Move(text15, text);
				}
				Directory.Move(text14, text15);
			}
			else if (exportNormal)
			{
				foreach (LodTileCoord lodTile in lodTiles)
				{
					string text16 = LodTilePath(stageDir, lodTile.Factor, lodTile.X, lodTile.Z);
					string text17 = LodTilePath(currentDir, lodTile.Factor, lodTile.X, lodTile.Z);
					BackupTarget(text17);
					if (File.Exists(text16))
					{
						string directoryName4 = Path.GetDirectoryName(text17);
						if (!string.IsNullOrEmpty(directoryName4))
						{
							Directory.CreateDirectory(directoryName4);
						}
						File.Move(text16, text17, overwrite: true);
					}
					else if (File.Exists(text17))
					{
						File.Delete(text17);
					}
				}
			}
			if (exportSepia)
			{
				CommitDerivedStyleLod("sepia", sepiaLodTiles, replaceWholeSepiaLod);
			}
			if (exportTopo)
			{
				CommitDerivedStyleLod("topo", topoLodTiles, replaceWholeTopoLod);
			}
			string text18 = Path.Combine(stageDir, "manifest.json");
			if (!File.Exists(text18))
			{
				throw new FileNotFoundException("Staged incremental terrain manifest is missing.", text18);
			}
			string text19 = Path.Combine(currentDir, "manifest.json");
			BackupTarget(text19);
			File.Move(text18, text19, overwrite: true);
		}
		catch
		{
			for (int num = touched.Count - 1; num >= 0; num--)
			{
				(string, string) tuple = touched[num];
				try
				{
					if (tuple.Item2 == null)
					{
						if (File.Exists(tuple.Item1))
						{
							File.Delete(tuple.Item1);
						}
					}
					else
					{
						string directoryName5 = Path.GetDirectoryName(tuple.Item1);
						if (!string.IsNullOrEmpty(directoryName5))
						{
							Directory.CreateDirectory(directoryName5);
						}
						File.Copy(tuple.Item2, tuple.Item1, overwrite: true);
					}
				}
				catch
				{
				}
			}
			try
			{
				if (replaceWholeLod)
				{
					string text20 = Path.Combine(currentDir, "lod");
					if (Directory.Exists(text20))
					{
						Directory.Delete(text20, recursive: true);
					}
					if (!string.IsNullOrEmpty(text) && Directory.Exists(text))
					{
						Directory.Move(text, text20);
					}
				}
				for (int num2 = styleLodBackups.Count - 1; num2 >= 0; num2--)
				{
					(string, string) tuple2 = styleLodBackups[num2];
					if (Directory.Exists(tuple2.Item1))
					{
						Directory.Delete(tuple2.Item1, recursive: true);
					}
					if (Directory.Exists(tuple2.Item2))
					{
						Directory.Move(tuple2.Item2, tuple2.Item1);
					}
				}
			}
			catch
			{
			}
			throw;
		}
		finally
		{
			try
			{
				if (Directory.Exists(stageDir))
				{
					Directory.Delete(stageDir, recursive: true);
				}
			}
			catch
			{
			}
			try
			{
				if (Directory.Exists(rollbackDir))
				{
					Directory.Delete(rollbackDir, recursive: true);
				}
			}
			catch
			{
			}
		}
		void BackupTarget(string target)
		{
			if (!touched.Any(((string Target, string Backup) tuple3) => string.Equals(tuple3.Target, target, StringComparison.OrdinalIgnoreCase)))
			{
				if (!File.Exists(target))
				{
					touched.Add((target, null));
				}
				else
				{
					string relativePath = Path.GetRelativePath(currentDir, target);
					string text21 = Path.Combine(rollbackDir, relativePath);
					string directoryName6 = Path.GetDirectoryName(text21);
					if (!string.IsNullOrEmpty(directoryName6))
					{
						Directory.CreateDirectory(directoryName6);
					}
					File.Copy(target, text21, overwrite: true);
					touched.Add((target, text21));
				}
			}
		}
		void CommitDerivedStyleLod(string text22, HashSet<LodTileCoord> affectedTiles, bool replaceWhole)
		{
			string text21 = StyleRoot(stageDir, text22);
			string text23 = StyleRoot(currentDir, text22);
			string text24 = Path.Combine(text21, "lod");
			string text25 = Path.Combine(text23, "lod");
			if (replaceWhole)
			{
				if (!Directory.Exists(text24))
				{
					throw new DirectoryNotFoundException("Staged " + text22 + " LOD terrain directory is missing.");
				}
				if (Directory.Exists(text25))
				{
					string text26 = Path.Combine(rollbackDir, "styles", text22, "lod");
					string directoryName6 = Path.GetDirectoryName(text26);
					if (!string.IsNullOrEmpty(directoryName6))
					{
						Directory.CreateDirectory(directoryName6);
					}
					if (Directory.Exists(text26))
					{
						Directory.Delete(text26, recursive: true);
					}
					Directory.Move(text25, text26);
					styleLodBackups.Add((text25, text26));
				}
				Directory.CreateDirectory(text23);
				Directory.Move(text24, text25);
				return;
			}
			foreach (LodTileCoord affectedTile in affectedTiles)
			{
				string text27 = LodTilePath(text21, affectedTile.Factor, affectedTile.X, affectedTile.Z);
				string text28 = LodTilePath(text23, affectedTile.Factor, affectedTile.X, affectedTile.Z);
				BackupTarget(text28);
				if (File.Exists(text27))
				{
					string directoryName7 = Path.GetDirectoryName(text28);
					if (!string.IsNullOrEmpty(directoryName7))
					{
						Directory.CreateDirectory(directoryName7);
					}
					File.Move(text27, text28, overwrite: true);
				}
				else if (File.Exists(text28))
				{
					File.Delete(text28);
				}
			}
		}
	}

	private bool TryRenderTileDatabaseOnly(WebMapConfig config, TileCoord tile, string outputDir, HashSet<ChunkCoord> generatedChunks, WebMapSaveDbTerrainReader directReader, out DatabaseRenderedTile rendered)
	{
		rendered = default;
		int num = checked(config.TerrainTileSize * config.TerrainTileSize * 4);
		byte[] array = ArrayPool<byte>.Shared.Rent(num);
		try
		{
			FillBackground(array, num);
			int value = tile.X * config.TerrainTileSize;
			int value2 = tile.Z * config.TerrainTileSize;
			int num2 = FloorDiv(value, 32);
			int num3 = FloorDiv(value2, 32);
			int num4 = Math.Max(1, config.TerrainTileSize / 32);
			long num5 = 0L;
			long num6 = 0L;
			long num7 = 0L;
			for (int i = 0; i < num4; i++)
			{
				for (int j = 0; j < num4; j++)
				{
					int num8 = num2 + j;
					int num9 = num3 + i;
					ChunkCoord item = new ChunkCoord(num8, num9);
					if (generatedChunks.Contains(item))
					{
						Interlocked.Increment(ref existingChunkColumns);
						if (!TryRenderDatabaseColumnCore(directReader, num8, num9, array, config.TerrainTileSize, j * 32, i * 32, out var worldReadMilliseconds, out var renderMilliseconds))
						{
							return false;
						}
						num6 += worldReadMilliseconds;
						num7 += renderMilliseconds;
						num5++;
					}
				}
			}
			WriteRawTile(outputDir, tile.X, tile.Z, array, num);
			WriteDerivedTerrainStyles(outputDir, tile.X, tile.Z, array, config.TerrainTileSize, config.ExportSepiaTerrain, config.ExportTopographicTerrain);
			Stopwatch stopwatch = Stopwatch.StartNew();
			ApplyHillshade(array, config.TerrainTileSize);
			stopwatch.Stop();
			num7 += stopwatch.ElapsedMilliseconds;
			RenderedTile tile2 = SummarizeTile(array, config.TerrainTileSize);
			long pngWriteMilliseconds = 0L;
			if (config.ExportNormalTerrain)
			{
				string text = Path.Combine(outputDir, tile.X.ToString(CultureInfo.InvariantCulture));
				Directory.CreateDirectory(text);
				string path = Path.Combine(text, tile.Z.ToString(CultureInfo.InvariantCulture) + ".png");
				Stopwatch stopwatch2 = Stopwatch.StartNew();
				PngWriter.WriteRgba(path, config.TerrainTileSize, config.TerrainTileSize, array);
				stopwatch2.Stop();
				pngWriteMilliseconds = stopwatch2.ElapsedMilliseconds;
			}
			rendered = new DatabaseRenderedTile(tile2, num5, num6, num7, pngWriteMilliseconds);
			return true;
		}
		catch
		{
			return false;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(array);
		}
	}

	private RenderedTile RenderTile(WebMapConfig config, TileCoord tile, string outputDir, HashSet<ChunkCoord> generatedChunks, ref long processedColumns, WebMapTerrainExportStatus status, WebMapSaveDbTerrainReader? directReader, bool atomicReplace = false, string? rawSourceDir = null, HashSet<ChunkCoord>? patchChunks = null)
	{
		int num = checked(config.TerrainTileSize * config.TerrainTileSize * 4);
		byte[] array = ArrayPool<byte>.Shared.Rent(num);
		try
		{
			bool flag = rawSourceDir != null && patchChunks != null && patchChunks.Count > 0 && TryReadRawTile(rawSourceDir, tile.X, tile.Z, array, num);
			if (!flag)
			{
				FillBackground(array, num);
			}
			int value = tile.X * config.TerrainTileSize;
			int value2 = tile.Z * config.TerrainTileSize;
			int num2 = FloorDiv(value, 32);
			int num3 = FloorDiv(value2, 32);
			int num4 = Math.Max(1, config.TerrainTileSize / 32);
			for (int i = 0; i < num4; i++)
			{
				for (int j = 0; j < num4; j++)
				{
					int num5 = num2 + j;
					int num6 = num3 + i;
					ChunkCoord item = new ChunkCoord(num5, num6);
					if (flag && !patchChunks.Contains(item))
					{
						continue;
					}
					if (flag)
					{
						ClearChunkPatch(array, config.TerrainTileSize, j * 32, i * 32);
					}
					if (!generatedChunks.Contains(item))
					{
						continue;
					}
					Interlocked.Increment(ref existingChunkColumns);
					if (directReader != null && directReader.IsActive && TryRenderDatabaseColumn(directReader, num5, num6, array, config.TerrainTileSize, j * 32, i * 32, status))
					{
						processedColumns++;
						LogConsoleTileHeartbeat(tile, processedColumns, status);
						continue;
					}
					if (config.UseDirectSaveDatabaseTerrain)
					{
						throw new InvalidDataException($"Safe terrain read failed at {num5},{num6}: {directReader?.LastFailure ?? "reader unavailable"}. The exporter did not load this column into the world.");
					}
					IServerChunk[] array2 = null;
					Stopwatch stopwatch = Stopwatch.StartNew();
					try
					{
						status.WorldLoaderChunkColumns++;
						array2 = api.WorldManager.BlockingLoadChunkColumn(num5, num6);
						stopwatch.Stop();
						status.WorldReadMilliseconds += stopwatch.ElapsedMilliseconds;
						if (array2 == null || array2.Length == 0)
						{
							continue;
						}
						loadedChunkColumns++;
						IMapChunk mapChunk = null;
						for (int num7 = array2.Length - 1; num7 >= 0; num7--)
						{
							if (array2[num7]?.MapChunk != null)
							{
								mapChunk = array2[num7].MapChunk;
								break;
							}
						}
						if (mapChunk == null)
						{
							try
							{
								mapChunk = api.WorldManager.GetMapChunk(num5, num6);
							}
							catch
							{
							}
						}
						if (mapChunk == null)
						{
							try
							{
								mapChunk = api.World.BlockAccessor.GetMapChunk(num5, num6);
							}
							catch
							{
							}
						}
						if (mapChunk != null)
						{
							mapChunksResolved++;
						}
						Stopwatch stopwatch2 = Stopwatch.StartNew();
						RenderChunkColumn(num5, num6, array2, mapChunk, array, config.TerrainTileSize, j * 32, i * 32);
						stopwatch2.Stop();
						status.RenderMilliseconds += stopwatch2.ElapsedMilliseconds;
						processedColumns++;
						LogConsoleTileHeartbeat(tile, processedColumns, status);
					}
					finally
					{
						if (stopwatch.IsRunning)
						{
							stopwatch.Stop();
							status.WorldReadMilliseconds += stopwatch.ElapsedMilliseconds;
						}
						if (array2 != null)
						{
							IServerChunk[] array3 = array2;
							foreach (IServerChunk serverChunk in array3)
							{
								try
								{
									serverChunk?.Dispose();
								}
								catch
								{
								}
							}
						}
					}
				}
			}
			WriteRawTile(outputDir, tile.X, tile.Z, array, num);
			WriteDerivedTerrainStyles(outputDir, tile.X, tile.Z, array, config.TerrainTileSize, config.ExportSepiaTerrain, config.ExportTopographicTerrain);
			Stopwatch stopwatch3 = Stopwatch.StartNew();
			ApplyHillshade(array, config.TerrainTileSize);
			stopwatch3.Stop();
			status.RenderMilliseconds += stopwatch3.ElapsedMilliseconds;
			RenderedTile result = SummarizeTile(array, config.TerrainTileSize);
			if (config.ExportNormalTerrain)
			{
				string text = Path.Combine(outputDir, tile.X.ToString(CultureInfo.InvariantCulture));
				Directory.CreateDirectory(text);
				string text2 = Path.Combine(text, tile.Z.ToString(CultureInfo.InvariantCulture) + ".png");
				string text3 = (atomicReplace ? (text2 + ".tmp") : text2);
				Stopwatch stopwatch4 = Stopwatch.StartNew();
				PngWriter.WriteRgba(text3, config.TerrainTileSize, config.TerrainTileSize, array);
				if (atomicReplace)
				{
					File.Move(text3, text2, overwrite: true);
				}
				stopwatch4.Stop();
				status.PngWriteMilliseconds += stopwatch4.ElapsedMilliseconds;
			}
			status.BlockColorCacheEntries = blockColorCache.Count;
			return result;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(array);
		}
	}

	private string ResolveCurrentWorldFilepath()
	{
		string text = WorldSavePathResolver.Resolve(api.WorldManager, api.DataBasePath);
		api.Logger.Notification("[ServerEssentials/WebMap] terrain export resolved active save database to '" + text + "'.");
		return text;
	}

	private TerrainCoverage EnumerateGeneratedTiles(string savePath, int tileSize)
	{
		savePath = Path.GetFullPath(savePath);
		if (!File.Exists(savePath))
		{
			throw new FileNotFoundException("The active Vintage Story save database was not found at '" + savePath + "'.", savePath);
		}
		try
		{
			return EnumerateGeneratedTilesFromDatabase(savePath, tileSize, readOnly: true);
		}
		catch (SqliteException ex)
		{
			api.Logger.Warning("[ServerEssentials/WebMap] direct read-only terrain coverage open failed for '{0}' (SQLite {1}/{2}: {3}). Retrying from a private terrain-coverage snapshot.", savePath, ex.SqliteErrorCode, ex.SqliteExtendedErrorCode, ex.Message);
		}
		string text = Path.Combine(rootPath, ".terrain-coverage-" + Guid.NewGuid().ToString("N") + ".vcdbs");
		string text2 = text + "-wal";
		string text3 = text + "-shm";
		string text4 = text + "-journal";
		try
		{
			Directory.CreateDirectory(rootPath);
			CopyDatabaseSnapshotFile(savePath, text);
			CopyDatabaseSnapshotSidecar(savePath + "-wal", text2);
			CopyDatabaseSnapshotSidecar(savePath + "-shm", text3);
			CopyDatabaseSnapshotSidecar(savePath + "-journal", text4);
			TerrainCoverage result = EnumerateGeneratedTilesFromDatabase(text, tileSize, readOnly: false);
			api.Logger.Notification("[ServerEssentials/WebMap] terrain coverage read succeeded from a private terrain-coverage snapshot ({0:n0} generated overworld map chunks).", result.ChunkColumns);
			return result;
		}
		catch (Exception ex2)
		{
			throw new InvalidOperationException("Unable to read generated terrain coverage from the active save '" + savePath + "' or from a private terrain-coverage snapshot. Check the resolved save path and filesystem permissions. " + ex2.GetType().Name + ": " + ex2.Message, ex2);
		}
		finally
		{
			TryDeleteSnapshotFile(text4);
			TryDeleteSnapshotFile(text3);
			TryDeleteSnapshotFile(text2);
			TryDeleteSnapshotFile(text);
		}
	}

	private TerrainCoverage EnumerateGeneratedTilesFromDatabase(string databasePath, int tileSize, bool readOnly)
	{
		HashSet<TileCoord> hashSet = new HashSet<TileCoord>();
		HashSet<ChunkCoord> hashSet2 = new HashSet<ChunkCoord>();
		long num = 0L;
		SqliteConnectionStringBuilder sqliteConnectionStringBuilder = new SqliteConnectionStringBuilder
		{
			DataSource = databasePath,
			Mode = ((!readOnly) ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly),
			Cache = SqliteCacheMode.Private,
			Pooling = false,
			DefaultTimeout = 10
		};
		using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionStringBuilder.ConnectionString);
		sqliteConnection.Open();
		using (SqliteCommand sqliteCommand = sqliteConnection.CreateCommand())
		{
			sqliteCommand.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=10000;";
			sqliteCommand.ExecuteNonQuery();
		}
		using SqliteCommand sqliteCommand2 = sqliteConnection.CreateCommand();
		sqliteCommand2.CommandText = "SELECT position FROM mapchunk";
		sqliteCommand2.CommandTimeout = 30;
		using SqliteDataReader sqliteDataReader = sqliteCommand2.ExecuteReader();
		while (sqliteDataReader.Read())
		{
			long @int = sqliteDataReader.GetInt64(0);
			DecodeMapChunkPosition(@int, out var x, out var z, out var dimension);
			if (dimension == 0)
			{
				int value = x * 32;
				int value2 = z * 32;
				hashSet2.Add(new ChunkCoord(x, z));
				hashSet.Add(new TileCoord(FloorDiv(value, tileSize), FloorDiv(value2, tileSize)));
				num++;
			}
		}
		return new TerrainCoverage(hashSet, hashSet2, num);
	}

	private static void CopyDatabaseSnapshotFile(string source, string destination)
	{
		Exception innerException = null;
		for (int i = 0; i < 4; i++)
		{
			try
			{
				File.Copy(source, destination, overwrite: true);
				return;
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				innerException = ex;
				if (i < 3)
				{
					Thread.Sleep(150);
				}
			}
		}
		throw new IOException("Could not copy the live save database '" + source + "' for terrain coverage enumeration.", innerException);
	}

	private static void CopyDatabaseSnapshotSidecar(string source, string destination)
	{
		if (!File.Exists(source))
		{
			return;
		}
		try
		{
			File.Copy(source, destination, overwrite: true);
		}
		catch (IOException)
		{
		}
	}

	private static void TryDeleteSnapshotFile(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}

	private void ValidateStartupCoverageCoordinates(TerrainCoverage coverage)
	{
		if (coverage.ChunkColumns <= 0 || coverage.Chunks.Count == 0)
		{
			return;
		}
		int num = 0;
		int num2 = 0;
		foreach (ChunkCoord item in coverage.Chunks.Take(16))
		{
			num++;
			if (api.WorldManager.BlockingTestMapChunkExists(item.X, item.Z))
			{
				num2++;
			}
		}
		api.Logger.Notification("[ServerEssentials/WebMap] terrain coverage coordinate sanity check: {0}/{1} sampled map chunks resolved through Vintage Story.", num2, num);
		if (num <= 0 || num2 != 0)
		{
			return;
		}
		throw new InvalidDataException("Terrain coverage coordinates decoded from the VCDBS mapchunk table did not match any Vintage Story map chunks. Refusing to render/publish terrain because the save-position decoder is likely incompatible with this world/runtime.");
	}

	private void RenderChunkColumn(int chunkX, int chunkZ, IServerChunk[] chunks, IMapChunk? mapChunk, byte[] rgba, int tileSize, int pixelOffsetX, int pixelOffsetZ, ushort[]? preferredHeights = null)
	{
		int[] array = ArrayPool<int>.Shared.Rent(1024);
		ushort[] array2 = ArrayPool<ushort>.Shared.Rent(1024);
		bool[] array3 = ArrayPool<bool>.Shared.Rent(Math.Max(1, chunks.Length));
		try
		{
			Array.Clear(array, 0, 1024);
			Array.Clear(array2, 0, 1024);
			Array.Clear(array3, 0, chunks.Length);
			ushort[] array4 = preferredHeights ?? mapChunk?.RainHeightMap;
			if (array4 != null && array4.Length >= 1024)
			{
				Interlocked.Increment(ref rainHeightMapsResolved);
			}
			else
			{
				array4 = mapChunk?.WorldGenTerrainHeightMap;
				if (array4 != null && array4.Length >= 1024)
				{
					Interlocked.Increment(ref terrainHeightMapsResolved);
				}
				else
				{
					array4 = null;
					Interlocked.Increment(ref accessorHeightFallbackColumns);
				}
			}
			int unresolved = 1024;
			if (array4 != null)
			{
				unresolved = 0;
				int mapSizeY = api.WorldManager.MapSizeY;
				for (int i = 0; i < 32; i++)
				{
					for (int j = 0; j < 32; j++)
					{
						int num = i * 32 + j;
						int y = Math.Clamp(array4[num], 0, mapSizeY - 1);
						int num2 = ResolveSurfaceBlockId(chunks, array3, j, y, i, out var resolvedY);
						if (num2 == 0)
						{
							unresolved++;
							continue;
						}
						array[num] = num2;
						array2[num] = (ushort)resolvedY;
					}
				}
			}
			if (unresolved > 0)
			{
				ResolveMissingSurfacesByColumnScan(chunks, array3, array, array2, ref unresolved);
				Interlocked.Increment(ref directScanFallbackColumns);
			}
			Interlocked.Add(ref surfaceSampleCount, 1024L);
			Interlocked.Add(ref unresolvedSurfaceSampleCount, unresolved);
			Interlocked.Add(ref resolvedSurfaceSampleCount, 1024 - unresolved);
			int num3 = Math.Max(1, api.WorldManager.MapSizeY - 1);
			for (int k = 0; k < 32; k++)
			{
				for (int l = 0; l < 32; l++)
				{
					int num4 = k * 32 + l;
					int num5 = array[num4];
					if (num5 == 0)
					{
						continue;
					}
					if ((l & 7) == 0 && (k & 7) == 0)
					{
						lock (sampledBlockCountsLock)
						{
							if (sampledBlockCounts.Count < 256 || sampledBlockCounts.ContainsKey(num5))
							{
								sampledBlockCounts[num5] = (sampledBlockCounts.TryGetValue(num5, out var value) ? (value + 1) : 1);
							}
						}
					}
					Rgb blockColor = GetBlockColor(num5);
					int num6 = pixelOffsetX + l;
					int num7 = pixelOffsetZ + k;
					if ((uint)num6 < (uint)tileSize && (uint)num7 < (uint)tileSize)
					{
						int num8 = (num7 * tileSize + num6) * 4;
						rgba[num8] = blockColor.R;
						rgba[num8 + 1] = blockColor.G;
						rgba[num8 + 2] = blockColor.B;
						rgba[num8 + 3] = (byte)Math.Clamp((int)Math.Round((double)(int)array2[num4] * 255.0 / (double)num3), 1, 255);
					}
				}
			}
		}
		finally
		{
			ArrayPool<bool>.Shared.Return(array3);
			ArrayPool<ushort>.Shared.Return(array2);
			ArrayPool<int>.Shared.Return(array);
		}
	}

	private int ResolveSurfaceBlockId(IServerChunk[] chunks, bool[] unpacked, int localX, int y, int localZ, out int resolvedY)
	{
		resolvedY = y;
		int num = Math.Max(0, y - 16);
		for (int num2 = y; num2 >= num; num2--)
		{
			int num3 = num2 / 32;
			if ((uint)num3 < (uint)chunks.Length)
			{
				IServerChunk serverChunk = chunks[num3];
				if (serverChunk != null && !serverChunk.Empty)
				{
					EnsureChunkUnpacked(serverChunk, unpacked, num3);
					if (serverChunk.Data != null)
					{
						int num4 = num2 % 32;
						int index3d = (num4 * 32 + localZ) * 32 + localX;
						int blockId = serverChunk.Data.GetBlockId(index3d, 0);
						if (blockId != 0)
						{
							resolvedY = num2;
							return blockId;
						}
					}
				}
			}
		}
		return 0;
	}

	private static void EnsureChunkUnpacked(IServerChunk chunk, bool[] unpacked, int chunkY)
	{
		if ((uint)chunkY >= (uint)unpacked.Length || !unpacked[chunkY])
		{
			chunk.Unpack_ReadOnly();
			if ((uint)chunkY < (uint)unpacked.Length)
			{
				unpacked[chunkY] = true;
			}
		}
	}

	private void ResolveMissingSurfacesByColumnScan(IServerChunk[] chunks, bool[] unpacked, int[] surfaceIds, ushort[] surfaceHeights, ref int unresolved)
	{
		if (unresolved <= 0)
		{
			return;
		}
		int num = Math.Min(chunks.Length - 1, Math.Max(0, (api.WorldManager.MapSizeY - 1) / 32));
		int num2 = num;
		while (num2 >= 0 && unresolved > 0)
		{
			IServerChunk serverChunk = chunks[num2];
			if (serverChunk != null && !serverChunk.Empty)
			{
				EnsureChunkUnpacked(serverChunk, unpacked, num2);
				IChunkBlocks data = serverChunk.Data;
				if (data != null)
				{
					int num3 = ((num2 == num) ? Math.Min(31, (api.WorldManager.MapSizeY - 1) % 32) : 31);
					int num4 = num3;
					while (num4 >= 0 && unresolved > 0)
					{
						int num5 = num4 * 32 * 32;
						int num6 = num2 * 32 + num4;
						for (int i = 0; i < 32; i++)
						{
							int num7 = num5 + i * 32;
							int num8 = i * 32;
							int num9 = 0;
							while (num9 < 32)
							{
								if (surfaceIds[num8] == 0)
								{
									int blockId = data.GetBlockId(num7 + num9, 0);
									if (blockId != 0)
									{
										surfaceIds[num8] = blockId;
										surfaceHeights[num8] = (ushort)num6;
										unresolved--;
									}
								}
								num9++;
								num8++;
							}
						}
						num4--;
					}
				}
			}
			num2--;
		}
	}

	private WebMapSaveDbTerrainReader? OpenDirectSaveReader(WebMapConfig config, string savePath, WebMapTerrainExportStatus status)
	{
		if (!config.UseDirectSaveDatabaseTerrain)
		{
			status.TerrainReadMode = "world-api";
			return null;
		}
		WebMapSaveDbTerrainReader webMapSaveDbTerrainReader = WebMapSaveDbTerrainReader.TryOpen(api, savePath, out string reason);
		if (webMapSaveDbTerrainReader == null)
		{
			status.TerrainReadMode = "save-db-unavailable";
			api.Logger.Warning("[ServerEssentials/WebMap] direct save-database terrain reader unavailable: {0}", reason);
			return null;
		}
		status.TerrainReadMode = "save-db-surface";
		api.Logger.Notification("[ServerEssentials/WebMap] direct read-only save-database terrain reader active; only RainHeightMap surface sections will be decoded for bulk map rendering.");
		return webMapSaveDbTerrainReader;
	}

	private void PrimeBlockColorCache()
	{
		if (Volatile.Read(in blockPalettePrimed) != 0)
		{
			return;
		}
		int num = 0;
		foreach (Block block in api.World.Blocks)
		{
			if (block != null && block.Id > num)
			{
				num = block.Id;
			}
		}
		Rgb[] array = new Rgb[Math.Max(1, num + 1)];
		Rgb value = new Rgb(16, 22, 29);
		Array.Fill(array, value);
		foreach (Block block2 in api.World.Blocks)
		{
			if (block2 != null)
			{
				Rgb rgb = TerrainPalette.Get(block2);
				blockColorCache[block2.Id] = rgb;
				if ((uint)block2.Id < (uint)array.Length)
				{
					array[block2.Id] = rgb;
				}
			}
		}
		blockColorById = array;
		Volatile.Write(ref blockPalettePrimed, 1);
	}

	private Task PrimeBlockColorCacheOnMainThreadAsync(CancellationToken cancellationToken)
	{
		if (Volatile.Read(in blockPalettePrimed) != 0)
		{
			return Task.CompletedTask;
		}
		cancellationToken.ThrowIfCancellationRequested();
		TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		api.Event.EnqueueMainThreadTask(() =>
		{
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				PrimeBlockColorCache();
				tcs.TrySetResult(result: true);
			}
			catch (OperationCanceledException)
			{
				tcs.TrySetCanceled(cancellationToken);
			}
			catch (Exception exception)
			{
				tcs.TrySetException(exception);
			}
		}, "nses-webmap-prime-terrain-palette");
		return tcs.Task;
	}

	private Rgb GetBlockColor(int blockId)
	{
		Rgb[] array = blockColorById;
		if ((uint)blockId < (uint)array.Length)
		{
			return array[blockId];
		}
		if (blockColorCache.TryGetValue(blockId, out var value))
		{
			return value;
		}
		if (Volatile.Read(in blockPalettePrimed) != 0)
		{
			if (array.Length != 0)
			{
				return array[0];
			}
			return new Rgb(16, 22, 29);
		}
		Block block = api.World.GetBlock(blockId);
		Rgb rgb = TerrainPalette.Get(block);
		blockColorCache[blockId] = rgb;
		return rgb;
	}

	private static RenderedTile SummarizeTile(byte[] rgba, int tileSize)
	{
		int num = Math.Max(1, tileSize / 32);
		byte[] array = new byte[num * num * 3];
		long num2 = 0L;
		long num3 = 0L;
		long num4 = 0L;
		long num5 = 0L;
		for (int i = 0; i < num; i++)
		{
			for (int j = 0; j < num; j++)
			{
				long num6 = 0L;
				long num7 = 0L;
				long num8 = 0L;
				long num9 = 0L;
				int num10 = j * 32;
				int num11 = i * 32;
				int num12 = Math.Min(tileSize, num10 + 32);
				int num13 = Math.Min(tileSize, num11 + 32);
				for (int k = num11; k < num13; k++)
				{
					int num14 = k * tileSize * 4;
					for (int l = num10; l < num12; l++)
					{
						int num15 = num14 + l * 4;
						if (rgba[num15 + 3] != 0)
						{
							num6 += rgba[num15];
							num7 += rgba[num15 + 1];
							num8 += rgba[num15 + 2];
							num9++;
						}
					}
				}
				num2 += num6;
				num3 += num7;
				num4 += num8;
				num5 += num9;
				int num16 = (i * num + j) * 3;
				if (num9 == 0)
				{
					array[num16] = 16;
					array[num16 + 1] = 22;
					array[num16 + 2] = 29;
				}
				else
				{
					array[num16] = (byte)(num6 / num9);
					array[num16 + 1] = (byte)(num7 / num9);
					array[num16 + 2] = (byte)(num8 / num9);
				}
			}
		}
		Rgb average = ((num5 <= 0) ? new Rgb(16, 22, 29) : new Rgb((byte)(num2 / num5), (byte)(num3 / num5), (byte)(num4 / num5)));
		return new RenderedTile(average, array);
	}

	private void WriteDerivedTerrainStyles(string terrainRoot, int tileX, int tileZ, byte[] sourceRgba, int size, bool exportSepia, bool exportTopo)
	{
		if (!exportSepia && !exportTopo)
		{
			return;
		}
		int num = checked(size * size * 4);
		byte[] array = (exportSepia ? ArrayPool<byte>.Shared.Rent(num) : null);
		byte[] array2 = (exportTopo ? ArrayPool<byte>.Shared.Rent(num) : null);
		try
		{
			if (array != null)
			{
				Array.Clear(array, 0, num);
			}
			if (array2 != null)
			{
				Array.Clear(array2, 0, num);
			}
			int num2 = Math.Max(1, api.WorldManager.MapSizeY - 1);
			for (int i = 0; i < num; i += 4)
			{
				byte b = sourceRgba[i + 3];
				if (b != 0)
				{
					int num3 = (sourceRgba[i] * 30 + sourceRgba[i + 1] * 59 + sourceRgba[i + 2] * 11) / 100;
					int num4 = (b * num2 + 127) / 255;
					if (array != null)
					{
						int num5 = Math.Clamp(150 + (num3 - 110) / 3 + num4 / Math.Max(8, num2 / 18), 112, 214);
						array[i] = (byte)Math.Clamp(num5 + 22, 0, 255);
						array[i + 1] = (byte)Math.Clamp(num5 + 5, 0, 255);
						array[i + 2] = (byte)Math.Clamp(num5 - 24, 0, 255);
						array[i + 3] = b;
					}
					if (array2 != null)
					{
						int num6 = Math.Clamp(226 - num4 * 34 / Math.Max(1, num2), 184, 230);
						array2[i] = (byte)(num6 + 8);
						array2[i + 1] = (byte)(num6 + 3);
						array2[i + 2] = (byte)Math.Max(0, num6 - 8);
						array2[i + 3] = b;
					}
				}
			}
			if (array != null)
			{
				ApplyHillshade(array, size);
			}
			if (array2 != null)
			{
				ApplyTopographicContours(array2, sourceRgba, size, num2);
				ApplyHillshade(array2, size);
			}
			ApplyDerivedWaterEmphasis(array, array2, sourceRgba, size);
			if (array != null)
			{
				string path = StyleTilePath(terrainRoot, "sepia", tileX, tileZ);
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				PngWriter.WriteRgba(path, size, size, array);
			}
			if (array2 != null)
			{
				string path2 = StyleTilePath(terrainRoot, "topo", tileX, tileZ);
				Directory.CreateDirectory(Path.GetDirectoryName(path2));
				PngWriter.WriteRgba(path2, size, size, array2);
			}
		}
		finally
		{
			if (array != null)
			{
				ArrayPool<byte>.Shared.Return(array);
			}
			if (array2 != null)
			{
				ArrayPool<byte>.Shared.Return(array2);
			}
		}
	}

	private static void ApplyDerivedWaterEmphasis(byte[]? sepia, byte[]? topo, byte[] sourceRgba, int size)
	{
		if (size <= 0)
		{
			return;
		}
		for (int i = 0; i < size; i++)
		{
			for (int j = 0; j < size; j++)
			{
				int num = (i * size + j) * 4;
				if (IsSourceWater(sourceRgba, num))
				{
					bool flag = j == 0 || i == 0 || j == size - 1 || i == size - 1 || !IsSourceWater(sourceRgba, (i * size + Math.Max(0, j - 1)) * 4) || !IsSourceWater(sourceRgba, (i * size + Math.Min(size - 1, j + 1)) * 4) || !IsSourceWater(sourceRgba, (Math.Max(0, i - 1) * size + j) * 4) || !IsSourceWater(sourceRgba, (Math.Min(size - 1, i + 1) * size + j) * 4);
					if (sepia != null)
					{
						sepia[num] = (byte)(flag ? 62 : 84);
						sepia[num + 1] = (byte)(flag ? 91 : 116);
						sepia[num + 2] = (byte)(flag ? 104 : 129);
					}
					if (topo != null)
					{
						topo[num] = (byte)(flag ? 66 : 101);
						topo[num + 1] = (byte)(flag ? 101 : 139);
						topo[num + 2] = (byte)(flag ? 116 : 154);
					}
				}
			}
		}
	}

	private static bool IsSourceWater(byte[] sourceRgba, int i)
	{
		if (i < 0 || i + 3 >= sourceRgba.Length || sourceRgba[i + 3] == 0)
		{
			return false;
		}
		return Math.Abs(sourceRgba[i] - 54) <= 4 && Math.Abs(sourceRgba[i + 1] - 104) <= 4 && Math.Abs(sourceRgba[i + 2] - 145) <= 4;
	}

	private static void ApplyTopographicContours(byte[] topo, byte[] sourceRgba, int size, int mapHeight)
	{
		if (size < 2)
		{
			return;
		}
		for (int i = 0; i < size; i++)
		{
			for (int j = 0; j < size; j++)
			{
				int num = (i * size + j) * 4;
				byte b = sourceRgba[num + 3];
				if (b == 0)
				{
					continue;
				}
				int num2 = (b * mapHeight + 127) / 255;
				int num3 = num2;
				int num4 = num2;
				if (j + 1 < size)
				{
					byte b2 = sourceRgba[num + 7];
					if (b2 != 0)
					{
						num3 = (b2 * mapHeight + 127) / 255;
					}
				}
				if (i + 1 < size)
				{
					byte b3 = sourceRgba[num + size * 4 + 3];
					if (b3 != 0)
					{
						num4 = (b3 * mapHeight + 127) / 255;
					}
				}
				bool flag = num2 / 10 != num3 / 10 || num2 / 10 != num4 / 10;
				bool flag2 = num2 / 50 != num3 / 50 || num2 / 50 != num4 / 50;
				if (flag)
				{
					int num5 = (flag2 ? 78 : 38);
					topo[num] = (byte)Math.Max(0, topo[num] - num5);
					topo[num + 1] = (byte)Math.Max(0, topo[num + 1] - num5);
					topo[num + 2] = (byte)Math.Max(0, topo[num + 2] - num5);
				}
			}
		}
	}

	private static string StyleTilePath(string terrainRoot, string style, int x, int z)
	{
		InlineArray5<string> buffer = default;
		buffer[0] = terrainRoot;
		buffer[1] = "styles";
		buffer[2] = style;
		buffer[3] = x.ToString(CultureInfo.InvariantCulture);
		buffer[4] = z.ToString(CultureInfo.InvariantCulture) + ".png";
		return Path.Combine(buffer);
	}

	private static string StyleRoot(string terrainRoot, string style)
	{
		return Path.Combine(terrainRoot, "styles", style);
	}

	private void WriteDerivedStyleLodPyramids(string terrainRoot, WebMapTerrainManifest manifest, WebMapTerrainExportStatus status, WebMapConfig config)
	{
		if (config.ExportSepiaTerrain)
		{
			WriteLodPyramid(StyleRoot(terrainRoot, "sepia"), manifest, status, config);
		}
		if (config.ExportTopographicTerrain)
		{
			WriteLodPyramid(StyleRoot(terrainRoot, "topo"), manifest, status, config);
		}
	}

	private static void FillBackground(byte[] rgba, int usedLength)
	{
		Array.Clear(rgba, 0, usedLength);
	}

	private static void ApplyHillshade(byte[] rgba, int size)
	{
		int num = size * size;
		if (size < 3)
		{
			for (int i = 3; i < num * 4; i += 4)
			{
				rgba[i] = byte.MaxValue;
			}
			return;
		}
		byte[] array = ArrayPool<byte>.Shared.Rent(num);
		try
		{
			int num2 = 0;
			int num3 = 3;
			while (num2 < num)
			{
				array[num2] = rgba[num3];
				num2++;
				num3 += 4;
			}
			for (int j = 0; j < size; j++)
			{
				int num4 = Math.Max(0, j - 1);
				int num5 = Math.Min(size - 1, j + 1);
				int num6 = Math.Max(0, j - 2);
				int num7 = Math.Min(size - 1, j + 2);
				for (int k = 0; k < size; k++)
				{
					int num8 = Math.Max(0, k - 1);
					int num9 = Math.Min(size - 1, k + 1);
					int num10 = Math.Max(0, k - 2);
					int num11 = Math.Min(size - 1, k + 2);
					int num12 = (j * size + k) * 4;
					if (rgba[num12 + 3] != 0)
					{
						int num13 = array[j * size + num9] - array[j * size + num8];
						int num14 = array[num5 * size + k] - array[num4 * size + k];
						int num15 = array[j * size + num11] - array[j * size + num10];
						int num16 = array[num7 * size + k] - array[num6 * size + k];
						int num17 = Math.Abs(num13) + Math.Abs(num14);
						int num18 = num13 * 2 + num14 * 2 + (num15 + num16) / 2;
						int num19 = Math.Clamp(6 + num18 - Math.Min(8, num17 / 5), -42, 42);
						rgba[num12] = (byte)Math.Clamp(rgba[num12] + num19, 0, 255);
						rgba[num12 + 1] = (byte)Math.Clamp(rgba[num12 + 1] + num19, 0, 255);
						rgba[num12 + 2] = (byte)Math.Clamp(rgba[num12 + 2] + num19, 0, 255);
						rgba[num12 + 3] = byte.MaxValue;
					}
				}
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(array);
		}
	}

	private WebMapTerrainManifest CreateManifest(WebMapConfig config, TerrainCoverage coverage)
	{
		return new WebMapTerrainManifest
		{
			Version = 12,
			WorldName = (api.WorldManager.SaveGame.WorldName ?? ""),
			SavegameIdentifier = (api.WorldManager.SaveGame.SavegameIdentifier ?? ""),
			TileSize = config.TerrainTileSize,
			Style = "server-palette-save-db-fastpatch-true-pyramid-hillshade-raised-v11",
			Tiles = new Dictionary<string, WebMapTerrainTileMetadata>(StringComparer.Ordinal)
		};
	}

	private void FinalizeManifest(WebMapTerrainManifest manifest, TerrainCoverage coverage, int tileCount, long chunkColumns)
	{
		long generatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		manifest.Version = 12;
		manifest.GeneratedUnixMilliseconds = generatedUnixMilliseconds;
		manifest.TileCount = tileCount;
		manifest.ChunkColumnCount = chunkColumns;
		manifest.TileSize = Math.Max(1, manifest.TileSize);
		if (coverage.Tiles.Count > 0)
		{
			manifest.MinTileX = coverage.Tiles.Min((TileCoord t) => t.X);
			manifest.MaxTileX = coverage.Tiles.Max((TileCoord t) => t.X);
			manifest.MinTileZ = coverage.Tiles.Min((TileCoord t) => t.Z);
			manifest.MaxTileZ = coverage.Tiles.Max((TileCoord t) => t.Z);
			manifest.MinWorldX = manifest.MinTileX * manifest.TileSize;
			manifest.MaxWorldXExclusive = (manifest.MaxTileX + 1) * manifest.TileSize;
			manifest.MinWorldZ = manifest.MinTileZ * manifest.TileSize;
			manifest.MaxWorldZExclusive = (manifest.MaxTileZ + 1) * manifest.TileSize;
		}
		else
		{
			int num = (manifest.MaxTileZ = 0);
			int num3 = (manifest.MinTileZ = num);
			int minTileX = (manifest.MaxTileX = num3);
			manifest.MinTileX = minTileX;
			num = (manifest.MaxWorldZExclusive = 0);
			num3 = (manifest.MinWorldZ = num);
			minTileX = (manifest.MaxWorldXExclusive = num3);
			manifest.MinWorldX = minTileX;
		}
	}

	private void WriteOverview(string dir, WebMapTerrainManifest manifest, WebMapTerrainExportStatus status)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		List<WebMapTerrainTileMetadata> list = manifest.Tiles?.Values.ToList() ?? new List<WebMapTerrainTileMetadata>();
		if (list.Count == 0)
		{
			manifest.OverviewAvailable = false;
			int overviewWidth = (manifest.OverviewHeight = 1);
			manifest.OverviewWidth = overviewWidth;
			manifest.OverviewTileStride = 1;
			manifest.OverviewCellSizeBlocks = 32;
			byte[] rgba = new byte[4];
			PngWriter.WriteRgba(Path.Combine(dir, "overview.png"), 1, 1, rgba);
			stopwatch.Stop();
			status.OverviewMilliseconds += stopwatch.ElapsedMilliseconds;
			return;
		}
		int num2 = Math.Max(1, manifest.TileSize / 32);
		int num3 = list.Min((WebMapTerrainTileMetadata t) => t.X);
		int num4 = list.Max((WebMapTerrainTileMetadata t) => t.X);
		int num5 = list.Min((WebMapTerrainTileMetadata t) => t.Z);
		int num6 = list.Max((WebMapTerrainTileMetadata t) => t.Z);
		int num7;
		int num8;
		checked
		{
			num7 = (num4 - num3 + 1) * num2;
			num8 = (num6 - num5 + 1) * num2;
		}
		int num9 = Math.Max(1, (int)Math.Ceiling((double)Math.Max(num7, num8) / 1024.0));
		int num10 = Math.Max(1, (num7 + num9 - 1) / num9);
		int num11 = Math.Max(1, (num8 + num9 - 1) / num9);
		int num12;
		int[] array;
		int[] array2;
		int[] array3;
		int[] array4;
		byte[] array5;
		checked
		{
			num12 = num10 * num11;
			array = new int[num12];
			array2 = new int[num12];
			array3 = new int[num12];
			array4 = new int[num12];
			array5 = new byte[num12 * 4];
		}
		foreach (WebMapTerrainTileMetadata item in list)
		{
			byte[] array6 = item.ChunkRgb ?? Array.Empty<byte>();
			bool flag = array6.Length >= num2 * num2 * 3;
			for (int num13 = 0; num13 < num2; num13++)
			{
				for (int num14 = 0; num14 < num2; num14++)
				{
					int num15 = (item.X - num3) * num2 + num14;
					int num16 = (item.Z - num5) * num2 + num13;
					int num17 = num15 / num9;
					int num18 = num16 / num9;
					int num19 = num18 * num10 + num17;
					int num20 = (num13 * num2 + num14) * 3;
					byte b = (flag ? array6[num20] : item.R);
					byte b2 = (flag ? array6[num20 + 1] : item.G);
					byte b3 = (flag ? array6[num20 + 2] : item.B);
					array[num19] += b;
					array2[num19] += b2;
					array3[num19] += b3;
					array4[num19]++;
				}
			}
		}
		int num21 = num12 * 4;
		for (int num22 = 0; num22 < num12; num22++)
		{
			int num23 = num22 * 4;
			if (array4[num22] == 0)
			{
				array5[num23] = 0;
				array5[num23 + 1] = 0;
				array5[num23 + 2] = 0;
				array5[num23 + 3] = 0;
			}
			else
			{
				array5[num23] = (byte)(array[num22] / array4[num22]);
				array5[num23 + 1] = (byte)(array2[num22] / array4[num22]);
				array5[num23 + 2] = (byte)(array3[num22] / array4[num22]);
				array5[num23 + 3] = byte.MaxValue;
			}
		}
		PngWriter.WriteRgba(Path.Combine(dir, "overview.png"), num10, num11, array5);
		manifest.OverviewAvailable = true;
		manifest.OverviewWidth = num10;
		manifest.OverviewHeight = num11;
		manifest.OverviewTileStride = num9;
		manifest.OverviewCellSizeBlocks = 32 * num9;
		status.OverviewCellSizeBlocks = manifest.OverviewCellSizeBlocks;
		stopwatch.Stop();
		status.OverviewMilliseconds += stopwatch.ElapsedMilliseconds;
	}

	private HashSet<LodTileCoord> StageIncrementalLodTiles(string stageDir, string currentDir, WebMapTerrainManifest manifest, HashSet<TileCoord> dirtyTiles, List<string> staleKeys, WebMapTerrainExportStatus status, WebMapConfig config, out bool replaceWholeLod)
	{
		int[] array = (manifest.LodFactors = BuildPyramidFactors(manifest));
		manifest.LodTilePixelSize = manifest.TileSize;
		string currentLod = Path.Combine(currentDir, "lod");
		if (!Directory.Exists(currentLod) || array.Any((int f) => !Directory.Exists(Path.Combine(currentLod, f.ToString(CultureInfo.InvariantCulture)))))
		{
			replaceWholeLod = true;
			WriteLodPyramid(stageDir, manifest, status, config);
			return new HashSet<LodTileCoord>();
		}
		replaceWholeLod = false;
		HashSet<TileCoord> hashSet = new HashSet<TileCoord>(dirtyTiles);
		HashSet<TileCoord> hashSet2 = new HashSet<TileCoord>();
		foreach (string staleKey in staleKeys)
		{
			if (TryParseTileKey(staleKey, out var x, out var z))
			{
				TileCoord item = new TileCoord(x, z);
				hashSet.Add(item);
				hashSet2.Add(item);
			}
		}
		if (hashSet.Count == 0 || array.Length == 0)
		{
			return new HashSet<LodTileCoord>();
		}
		Stopwatch stopwatch = Stopwatch.StartNew();
		HashSet<LodTileCoord> hashSet3 = new HashSet<LodTileCoord>();
		int childFactor = 1;
		HashSet<TileCoord> hashSet4 = hashSet;
		int num = 0;
		int[] array3 = array;
		foreach (int num3 in array3)
		{
			HashSet<TileCoord> hashSet5 = new HashSet<TileCoord>();
			foreach (TileCoord item2 in hashSet4)
			{
				hashSet5.Add(new TileCoord(FloorDiv(item2.X, 2), FloorDiv(item2.Z, 2)));
			}
			HashSet<TileCoord> hashSet6 = new HashSet<TileCoord>();
			foreach (TileCoord item3 in hashSet5)
			{
				hashSet3.Add(new LodTileCoord(num3, item3.X, item3.Z));
				if (!WritePyramidParent(stageDir, currentDir, manifest.TileSize, childFactor, num3, item3.X, item3.Z, hashSet2))
				{
					hashSet6.Add(item3);
				}
			}
			hashSet4 = hashSet5;
			hashSet2 = hashSet6;
			childFactor = num3;
			num++;
			status.ProgressPercent = 96.0 + 2.5 * (double)num / (double)Math.Max(1, array.Length);
			status.Message = $"Updating zoom levels: {num:n0}/{array.Length:n0}";
			publishStatus(status.Clone());
		}
		stopwatch.Stop();
		status.LodWriteMilliseconds += stopwatch.ElapsedMilliseconds;
		return hashSet3;
	}

	private void WriteLodPyramid(string dir, WebMapTerrainManifest manifest, WebMapTerrainExportStatus status, WebMapConfig config)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		string path = Path.Combine(dir, "lod");
		if (Directory.Exists(path))
		{
			Directory.Delete(path, recursive: true);
		}
		Directory.CreateDirectory(path);
		int[] array = BuildPyramidFactors(manifest);
		manifest.LodFactors = array;
		manifest.LodTilePixelSize = manifest.TileSize;
		if (array.Length == 0 || manifest.Tiles == null || manifest.Tiles.Count == 0)
		{
			stopwatch.Stop();
			status.LodWriteMilliseconds += stopwatch.ElapsedMilliseconds;
			return;
		}
		HashSet<TileCoord> hashSet = new HashSet<TileCoord>();
		foreach (WebMapTerrainTileMetadata value in manifest.Tiles.Values)
		{
			hashSet.Add(new TileCoord(value.X, value.Z));
		}
		int num = 1;
		int maxDegreeOfParallelism = Math.Clamp(config.TerrainPyramidWorkers, 1, 4);
		int num2 = 0;
		int[] array2 = array;
		foreach (int num3 in array2)
		{
			HashSet<TileCoord> hashSet2 = new HashSet<TileCoord>();
			foreach (TileCoord item in hashSet)
			{
				hashSet2.Add(new TileCoord(FloorDiv(item.X, 2), FloorDiv(item.Z, 2)));
			}
			int levelChildFactor = num;
			int levelFactor = num3;
			ParallelOptions parallelOptions = new ParallelOptions
			{
				MaxDegreeOfParallelism = maxDegreeOfParallelism
			};
			Parallel.ForEach(hashSet2, parallelOptions, (TileCoord parent) =>
			{
				WritePyramidParent(dir, null, manifest.TileSize, levelChildFactor, levelFactor, parent.X, parent.Z, null);
			});
			hashSet = hashSet2;
			num = num3;
			num2++;
			status.ProgressPercent = 96.0 + 2.5 * (double)num2 / (double)Math.Max(1, array.Length);
			status.Message = $"Building zoom levels: {num2:n0}/{array.Length:n0}";
			publishStatus(status.Clone());
		}
		stopwatch.Stop();
		status.LodWriteMilliseconds += stopwatch.ElapsedMilliseconds;
	}

	private static int[] BuildPyramidFactors(WebMapTerrainManifest manifest)
	{
		if (manifest.Tiles == null || manifest.Tiles.Count <= 1)
		{
			return Array.Empty<int>();
		}
		int num = int.MaxValue;
		int num2 = int.MinValue;
		int num3 = int.MaxValue;
		int num4 = int.MinValue;
		foreach (WebMapTerrainTileMetadata value in manifest.Tiles.Values)
		{
			num = Math.Min(num, value.X);
			num2 = Math.Max(num2, value.X);
			num3 = Math.Min(num3, value.Z);
			num4 = Math.Max(num4, value.Z);
		}
		int num5 = Math.Max(num2 - num + 1, num4 - num3 + 1);
		if (num5 <= 1)
		{
			return Array.Empty<int>();
		}
		List<int> list = new List<int>();
		for (long num6 = 2L; num6 <= int.MaxValue; num6 <<= 1)
		{
			list.Add((int)num6);
			if ((num5 + num6 - 1) / num6 <= 1)
			{
				break;
			}
		}
		return list.ToArray();
	}

	private static string LodTilePath(string terrainDir, int factor, int x, int z)
	{
		InlineArray5<string> buffer = default;
		buffer[0] = terrainDir;
		buffer[1] = "lod";
		buffer[2] = factor.ToString(CultureInfo.InvariantCulture);
		buffer[3] = x.ToString(CultureInfo.InvariantCulture);
		buffer[4] = z.ToString(CultureInfo.InvariantCulture) + ".png";
		return Path.Combine(buffer);
	}

	private static bool WritePyramidParent(string stageRoot, string? currentRoot, int tileSize, int childFactor, int parentFactor, int parentX, int parentZ, HashSet<TileCoord>? removedChildren)
	{
		int num = tileSize / 2;
		if (num <= 0)
		{
			return false;
		}
		int num2 = checked(tileSize * tileSize * 4);
		byte[] array = ArrayPool<byte>.Shared.Rent(num2);
		try
		{
			Array.Clear(array, 0, num2);
			bool flag = false;
			for (int i = 0; i < 2; i++)
			{
				for (int j = 0; j < 2; j++)
				{
					int x = parentX * 2 + j;
					int z = parentZ * 2 + i;
					TileCoord item = new TileCoord(x, z);
					if (removedChildren == null || !removedChildren.Contains(item))
					{
						string text = ((childFactor == 1) ? TilePath(stageRoot, x, z) : LodTilePath(stageRoot, childFactor, x, z));
						string text2;
						if (currentRoot == null)
						{
							text2 = null;
						}
						else
						{
							text2 = ((childFactor == 1) ? TilePath(currentRoot, x, z) : LodTilePath(currentRoot, childFactor, x, z));
						}
						string text3;
						if (File.Exists(text))
						{
							text3 = text;
						}
						else
						{
							text3 = ((!string.IsNullOrEmpty(text2) && File.Exists(text2)) ? text2 : null);
						}
						if (text3 != null && PngReader.TryReadRgba(text3, out int width, out int height, out byte[] rgba) && width == tileSize && height == tileSize)
						{
							flag = true;
							DownsampleChildIntoQuadrant(rgba, tileSize, array, j * num, i * num, num);
						}
					}
				}
			}
			if (!flag)
			{
				return false;
			}
			string path = LodTilePath(stageRoot, parentFactor, parentX, parentZ);
			string directoryName = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			PngWriter.WriteRgba(path, tileSize, tileSize, array);
			return true;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(array);
		}
	}

	private static void DownsampleChildIntoQuadrant(byte[] child, int childSize, byte[] parent, int dstX, int dstY, int half)
	{
		for (int i = 0; i < half; i++)
		{
			int num = Math.Min(childSize - 2, i * 2);
			for (int j = 0; j < half; j++)
			{
				int num2 = Math.Min(childSize - 2, j * 2);
				int num3 = (num * childSize + num2) * 4;
				int num4 = num3 + 4;
				int num5 = num3 + childSize * 4;
				int num6 = num5 + 4;
				int num7 = 0;
				int num8 = 0;
				int num9 = 0;
				int num10 = 0;
				if (child[num3 + 3] != 0)
				{
					num8 += child[num3];
					num9 += child[num3 + 1];
					num10 += child[num3 + 2];
					num7++;
				}
				if (child[num4 + 3] != 0)
				{
					num8 += child[num4];
					num9 += child[num4 + 1];
					num10 += child[num4 + 2];
					num7++;
				}
				if (child[num5 + 3] != 0)
				{
					num8 += child[num5];
					num9 += child[num5 + 1];
					num10 += child[num5 + 2];
					num7++;
				}
				if (child[num6 + 3] != 0)
				{
					num8 += child[num6];
					num9 += child[num6 + 1];
					num10 += child[num6 + 2];
					num7++;
				}
				int num11 = ((dstY + i) * childSize + (dstX + j)) * 4;
				if (num7 == 0)
				{
					parent[num11] = (parent[num11 + 1] = (parent[num11 + 2] = (parent[num11 + 3] = 0)));
					continue;
				}
				int num12 = num8 / num7;
				int num13 = num9 / num7;
				int num14 = num10 / num7;
				int num15 = (num12 * 3 + num13 * 6 + num14) / 10;
				parent[num11] = (byte)Math.Clamp(num15 + (num12 - num15) * 11 / 10, 0, 255);
				parent[num11 + 1] = (byte)Math.Clamp(num15 + (num13 - num15) * 11 / 10, 0, 255);
				parent[num11 + 2] = (byte)Math.Clamp(num15 + (num14 - num15) * 11 / 10, 0, 255);
				parent[num11 + 3] = byte.MaxValue;
			}
		}
	}

	private static WebMapTerrainManifest? LoadManifest(string dir)
	{
		try
		{
			string path = Path.Combine(dir, "manifest.json");
			if (!File.Exists(path))
			{
				return null;
			}
			return JsonSerializer.Deserialize<WebMapTerrainManifest>(File.ReadAllText(path));
		}
		catch
		{
			return null;
		}
	}

	private static void WriteManifest(string dir, WebMapTerrainManifest manifest)
	{
		File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest));
	}

	private static void WriteManifestAtomic(string dir, WebMapTerrainManifest manifest)
	{
		string text = Path.Combine(dir, "manifest.json");
		string text2 = text + ".tmp";
		File.WriteAllText(text2, JsonSerializer.Serialize(manifest));
		File.Move(text2, text, overwrite: true);
	}

	private static string RawTilePath(string root, int x, int z)
	{
		return Path.Combine(root, "raw", x.ToString(CultureInfo.InvariantCulture), z.ToString(CultureInfo.InvariantCulture) + ".bin");
	}

	private static void WriteRawTile(string root, int x, int z, byte[] rgba, int usedLength)
	{
		string path = RawTilePath(root, x, z);
		string directoryName = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		using FileStream fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan);
		fileStream.Write(rgba, 0, usedLength);
	}

	private static bool TryReadRawTile(string root, int x, int z, byte[] destination, int usedLength)
	{
		string text = RawTilePath(root, x, z);
		try
		{
			FileInfo fileInfo = new FileInfo(text);
			if (!fileInfo.Exists || fileInfo.Length != usedLength)
			{
				return false;
			}
			using FileStream fileStream = new FileStream(text, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
			int num;
			for (int i = 0; i < usedLength; i += num)
			{
				num = fileStream.Read(destination, i, usedLength - i);
				if (num <= 0)
				{
					return false;
				}
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static void ClearChunkPatch(byte[] rgba, int tileSize, int pixelOffsetX, int pixelOffsetZ)
	{
		int num = Math.Clamp(pixelOffsetX, 0, tileSize);
		int num2 = Math.Clamp(pixelOffsetZ, 0, tileSize);
		int num3 = Math.Clamp(pixelOffsetX + 32, 0, tileSize);
		int num4 = Math.Clamp(pixelOffsetZ + 32, 0, tileSize);
		int length = Math.Max(0, (num3 - num) * 4);
		for (int i = num2; i < num4; i++)
		{
			Array.Clear(rgba, (i * tileSize + num) * 4, length);
		}
	}

	private static string TilePath(string root, int x, int z)
	{
		return Path.Combine(root, x.ToString(CultureInfo.InvariantCulture), z.ToString(CultureInfo.InvariantCulture) + ".png");
	}

	private static string TileKey(int x, int z)
	{
		return x.ToString(CultureInfo.InvariantCulture) + "," + z.ToString(CultureInfo.InvariantCulture);
	}

	private static bool TryParseTileKey(string key, out int x, out int z)
	{
		x = (z = 0);
		string[] array = key.Split(',', 2);
		return array.Length == 2 && int.TryParse(array[0], out x) && int.TryParse(array[1], out z);
	}

	private static void DecodeMapChunkPosition(long packed, out int x, out int z, out int dimension)
	{
		x = (int)(packed & 0x1FFFFF);
		int num = (int)((packed >> 22) & 0x1F);
		z = (int)((packed >> 27) & 0x1FFFFF);
		int num2 = (int)((packed >> 49) & 0x1F);
		dimension = num | (num2 << 5);
	}

	private static int FloorDiv(int value, int divisor)
	{
		int num = value / divisor;
		int num2 = value % divisor;
		if (num2 != 0 && num2 < 0 != divisor < 0)
		{
			num--;
		}
		return num;
	}

	private static void AtomicSwap(string stageDir, string currentDir, string backupDir)
	{
		if (Directory.Exists(backupDir))
		{
			Directory.Delete(backupDir, recursive: true);
		}
		if (Directory.Exists(currentDir))
		{
			Directory.Move(currentDir, backupDir);
		}
		try
		{
			Directory.Move(stageDir, currentDir);
			if (Directory.Exists(backupDir))
			{
				Directory.Delete(backupDir, recursive: true);
			}
		}
		catch
		{
			if (!Directory.Exists(currentDir) && Directory.Exists(backupDir))
			{
				Directory.Move(backupDir, currentDir);
			}
			throw;
		}
	}

	private void ResetSurfaceDiagnostics()
	{
		surfaceSampleCount = 0L;
		resolvedSurfaceSampleCount = 0L;
		unresolvedSurfaceSampleCount = 0L;
		chunkExistenceChecks = 0L;
		existingChunkColumns = 0L;
		loadedChunkColumns = 0L;
		mapChunksResolved = 0L;
		rainHeightMapsResolved = 0L;
		terrainHeightMapsResolved = 0L;
		accessorHeightFallbackColumns = 0L;
		directScanFallbackColumns = 0L;
		sampledBlockCounts.Clear();
		blockColorCache.Clear();
	}

	private void ValidateAndLogSurfaceDiagnostics(TerrainCoverage coverage)
	{
		if (coverage.ChunkColumns > 0)
		{
			double num = ((surfaceSampleCount <= 0) ? 1.0 : ((double)unresolvedSurfaceSampleCount / (double)surfaceSampleCount));
			string text = string.Join(", ", sampledBlockCounts.OrderByDescending((KeyValuePair<int, long> kv) => kv.Value).Take(8).Select((KeyValuePair<int, long> kv) =>
			{
				string text2 = api.World.GetBlock(kv.Key)?.Code?.ToString() ?? ("id:" + kv.Key);
				return text2 + "=" + kv.Value.ToString("n0", CultureInfo.InvariantCulture);
			}));
			api.Logger.Notification("[ServerEssentials/WebMap] terrain surface diagnostics: resolved={0:n0}, unresolved={1:n0} ({2:P1}), uniqueSampledBlocks={3:n0}, paletteEntries={4:n0}, top=[{5}].", resolvedSurfaceSampleCount, unresolvedSurfaceSampleCount, num, sampledBlockCounts.Count, blockColorCache.Count, text);
			api.Logger.Notification("[ServerEssentials/WebMap] terrain loader diagnostics: redundantExistenceChecks={0:n0}, generatedColumnsVisited={1:n0}, loadedColumns={2:n0}, mapChunks={3:n0}, rainHeightMaps={4:n0}, terrainHeightMaps={5:n0}, accessorHeightFallbackColumns={6:n0}, directScanFallbackColumns={7:n0}.", chunkExistenceChecks, existingChunkColumns, loadedChunkColumns, mapChunksResolved, rainHeightMapsResolved, terrainHeightMapsResolved, accessorHeightFallbackColumns, directScanFallbackColumns);
			if (surfaceSampleCount == 0L || resolvedSurfaceSampleCount == 0L || num >= 0.9)
			{
				throw new InvalidDataException($"Terrain renderer resolved only {resolvedSurfaceSampleCount:n0} of {surfaceSampleCount:n0} sampled surface pixels ({num:P1} unresolved). Loader diagnostics: existenceChecks={chunkExistenceChecks:n0}, existingColumns={existingChunkColumns:n0}, loadedColumns={loadedChunkColumns:n0}, mapChunks={mapChunksResolved:n0}, rainHeightMaps={rainHeightMapsResolved:n0}, terrainHeightMaps={terrainHeightMapsResolved:n0}, accessorHeightFallbackColumns={accessorHeightFallbackColumns:n0}, directScanFallbackColumns={directScanFallbackColumns:n0}. Refusing to publish gray/empty terrain tiles.");
			}
		}
	}

	private void SetProgressPhase(WebMapTerrainExportStatus status, string phase, double percent, string? message = null, bool publish = true)
	{
		status.Phase = (string.IsNullOrWhiteSpace(phase) ? "working" : phase);
		status.ProgressPercent = Math.Clamp(percent, 0.0, 100.0);
		if (!string.IsNullOrWhiteSpace(message))
		{
			status.Message = message;
		}
		if (publish)
		{
			publishStatus(status.Clone());
		}
	}

	private static void UpdateRenderProgress(WebMapTerrainExportStatus status, int completedTiles, int totalTiles)
	{
		if (string.Equals(status.Phase, "rendering", StringComparison.OrdinalIgnoreCase))
		{
			if (totalTiles <= 0)
			{
				status.ProgressPercent = 90.0;
				return;
			}
			double num = Math.Clamp((double)completedTiles / (double)totalTiles, 0.0, 1.0);
			status.ProgressPercent = 5.0 + num * 85.0;
		}
	}

	private void ResetEtaSamples()
	{
		recentTileMilliseconds.Clear();
		etaLastSampleUnixMilliseconds = progressPhaseStartedUnixMilliseconds;
		etaLastCompletedTiles = 0;
	}

	private void UpdateEta(WebMapTerrainExportStatus status, int completedTiles, int totalTiles)
	{
		UpdateRenderProgress(status, completedTiles, totalTiles);
		if (completedTiles <= 0 || totalTiles <= 0 || progressPhaseStartedUnixMilliseconds <= 0)
		{
			status.EstimatedRemainingMilliseconds = ((totalTiles > 0) ? (-1) : 0);
			status.AverageMillisecondsPerTile = 0.0;
			return;
		}
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (completedTiles > etaLastCompletedTiles)
		{
			int num2 = completedTiles - etaLastCompletedTiles;
			long num3 = Math.Max(1L, num - Math.Max(progressPhaseStartedUnixMilliseconds, etaLastSampleUnixMilliseconds));
			double item = (double)num3 / (double)num2;
			if (completedTiles > 2)
			{
				recentTileMilliseconds.Enqueue(item);
				while (recentTileMilliseconds.Count > 16)
				{
					recentTileMilliseconds.Dequeue();
				}
			}
			etaLastCompletedTiles = completedTiles;
			etaLastSampleUnixMilliseconds = num;
		}
		double num4;
		if (recentTileMilliseconds.Count > 0)
		{
			num4 = recentTileMilliseconds.Average();
		}
		else
		{
			long num5 = Math.Max(1L, num - progressPhaseStartedUnixMilliseconds);
			num4 = (double)num5 / (double)completedTiles;
		}
		int num6 = Math.Max(0, totalTiles - completedTiles);
		status.AverageMillisecondsPerTile = num4;
		status.EstimatedRemainingMilliseconds = ((num6 == 0) ? 0 : ((long)Math.Ceiling(num4 * (double)num6)));
	}

	private void LogConsoleProgress(string phase, int completedTiles, int totalTiles, long processedColumns, WebMapTerrainExportStatus status, bool force = false)
	{
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		int num2 = (int)((totalTiles <= 0) ? 100 : Math.Clamp((long)completedTiles * 100L / totalTiles, 0L, 100L));
		bool flag = totalTiles > 0 && completedTiles >= totalTiles;
		if ((force | flag) || num - lastStatusProgressPublishUnixMilliseconds >= 1000)
		{
			UpdateEta(status, completedTiles, totalTiles);
			publishStatus(status.Clone());
			lastStatusProgressPublishUnixMilliseconds = num;
		}
		bool flag2 = num2 >= lastConsoleProgressPercent + 10;
		bool flag3 = num - lastConsoleProgressUnixMilliseconds >= 30000;
		if (force || flag || flag2 || flag3)
		{
			UpdateEta(status, completedTiles, totalTiles);
			lastConsoleProgressUnixMilliseconds = num;
			lastConsoleProgressPercent = Math.Max(lastConsoleProgressPercent, num2);
			api.Logger.Notification("[ServerEssentials/WebMap] terrain export progress ({0}): {1}% ({2:n0}/{3:n0} tiles), {4:n0} chunk columns processed, elapsed {5}, ETA {6}.", phase, num2, completedTiles, totalTiles, processedColumns, FormatElapsed(num - status.StartedUnixMilliseconds), FormatEta(status.EstimatedRemainingMilliseconds));
		}
	}

	private void LogConsoleTileHeartbeat(TileCoord tile, long processedColumns, WebMapTerrainExportStatus status)
	{
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (num - lastConsoleProgressUnixMilliseconds >= 30000)
		{
			lastConsoleProgressUnixMilliseconds = num;
			api.Logger.Notification("[ServerEssentials/WebMap] terrain export still working: rendering tile ({0}, {1}), {2:n0} chunk columns processed, elapsed {3}, ETA {4}.", tile.X, tile.Z, processedColumns, FormatElapsed(num - status.StartedUnixMilliseconds), FormatEta(status.EstimatedRemainingMilliseconds));
		}
	}

	private static string FormatEta(long estimatedRemainingMilliseconds)
	{
		return (estimatedRemainingMilliseconds < 0) ? "calculating" : FormatElapsed(estimatedRemainingMilliseconds);
	}

	private static string FormatElapsed(long elapsedMilliseconds)
	{
		TimeSpan timeSpan = TimeSpan.FromMilliseconds(Math.Max(0L, elapsedMilliseconds));
		return (timeSpan.TotalHours >= 1.0) ? $"{(int)timeSpan.TotalHours}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}" : $"{timeSpan.Minutes}:{timeSpan.Seconds:00}";
	}
}
