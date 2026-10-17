using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlockLogger.Services;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

public sealed class WebMapDirtyChunkJournal : IDisposable
{
	private const int ChunkSize = 32;

	private const string FileName = "dirty-mapchunks-v1.log";

	private const int PackedCoordBias = 8388608;

	private const int PackedCoordMask = 16777215;

	private readonly ICoreServerAPI api;

	private readonly string rootPath;

	private readonly string path;

	private readonly string worldId;

	private readonly object gate = new object();

	private readonly object fileGate = new object();

	private readonly HashSet<long> dirtyPacked = new HashSet<long>();

	private readonly HashSet<WebMapDirtyChunkCoord> dirtyOverflow = new HashSet<WebMapDirtyChunkCoord>();

	private readonly ConcurrentQueue<long> pendingPackedWrites = new ConcurrentQueue<long>();

	private readonly ConcurrentQueue<WebMapDirtyChunkCoord> pendingOverflowWrites = new ConcurrentQueue<WebMapDirtyChunkCoord>();

	private readonly SemaphoreSlim wake = new SemaphoreSlim(0, 1);

	private readonly CancellationTokenSource stop = new CancellationTokenSource();

	private readonly object workerGate = new object();

	private readonly object loadGate = new object();

	private Task? worker;

	private bool loaded;

	private readonly StringBuilder writeBuffer = new StringBuilder(4096);

	private long mutationVersion;

	private bool disposed;

	public int Count
	{
		get
		{
			EnsureLoaded();
			lock (gate)
			{
				return dirtyPacked.Count + dirtyOverflow.Count;
			}
		}
	}

	public long MutationVersion => Interlocked.Read(in mutationVersion);

	public WebMapDirtyChunkJournal(ICoreServerAPI api)
	{
		this.api = api;
		rootPath = api.GetOrCreateDataPath("NewfiesServerEssentialsWebMap");
		path = Path.Combine(rootPath, "dirty-mapchunks-v1.log");
		worldId = api.World.SavegameIdentifier ?? api.WorldManager.SaveGame.SavegameIdentifier ?? "unknownworld";
	}

	public void MarkBlockDirty(int dimension, int blockX, int blockZ)
	{
		if (disposed)
		{
			return;
		}
		EnsureLoaded();
		int chunkX = FloorDiv(blockX, 32);
		int chunkZ = FloorDiv(blockZ, 32);
		lock (gate)
		{
			if (disposed)
			{
				return;
			}
			Interlocked.Increment(ref mutationVersion);
			if (TryPack(dimension, chunkX, chunkZ, out var packed))
			{
				if (!dirtyPacked.Add(packed))
				{
					return;
				}
				pendingPackedWrites.Enqueue(packed);
			}
			else
			{
				WebMapDirtyChunkCoord item = new WebMapDirtyChunkCoord(dimension, chunkX, chunkZ);
				if (!dirtyOverflow.Add(item))
				{
					return;
				}
				pendingOverflowWrites.Enqueue(item);
			}
		}
		EnsureWorkerStarted();
		SignalWriter();
	}

	private void SignalWriter()
	{
		try
		{
			if (wake.CurrentCount == 0)
			{
				wake.Release();
			}
		}
		catch (SemaphoreFullException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private void EnsureWorkerStarted()
	{
		lock (workerGate)
		{
			if (!disposed && worker == null)
			{
				worker = Task.Run((Func<Task?>)WriterLoopAsync);
			}
		}
	}

	public HashSet<WebMapDirtyChunkCoord> Snapshot()
	{
		long snapshotMutationVersion;
		return Snapshot(out snapshotMutationVersion);
	}

	public HashSet<WebMapDirtyChunkCoord> Snapshot(out long snapshotMutationVersion)
	{
		EnsureLoaded();
		lock (gate)
		{
			snapshotMutationVersion = MutationVersion;
			HashSet<WebMapDirtyChunkCoord> hashSet = new HashSet<WebMapDirtyChunkCoord>(dirtyPacked.Count + dirtyOverflow.Count);
			foreach (long item in dirtyPacked)
			{
				hashSet.Add(Unpack(item));
			}
			foreach (WebMapDirtyChunkCoord item2 in dirtyOverflow)
			{
				hashSet.Add(item2);
			}
			return hashSet;
		}
	}

	public void Acknowledge(IEnumerable<WebMapDirtyChunkCoord> exported)
	{
		TryAcknowledgeSnapshot(exported, MutationVersion);
	}

	public bool TryAcknowledgeSnapshot(IEnumerable<WebMapDirtyChunkCoord> exported, long expectedMutationVersion)
	{
		if (disposed)
		{
			return false;
		}
		EnsureLoaded();
		lock (fileGate)
		{
			FlushPendingWrites();
			List<WebMapDirtyChunkCoord> list = new List<WebMapDirtyChunkCoord>();
			List<WebMapDirtyChunkCoord> list2 = new List<WebMapDirtyChunkCoord>();
			lock (gate)
			{
				if (disposed || MutationVersion != expectedMutationVersion)
				{
					return false;
				}
				foreach (WebMapDirtyChunkCoord item in exported)
				{
					if (TryPack(item.Dimension, item.ChunkX, item.ChunkZ, out var packed) ? dirtyPacked.Remove(packed) : dirtyOverflow.Remove(item))
					{
						list.Add(item);
					}
				}
				if (list.Count == 0)
				{
					return true;
				}
				foreach (long item2 in dirtyPacked)
				{
					list2.Add(Unpack(item2));
				}
				foreach (WebMapDirtyChunkCoord item3 in dirtyOverflow)
				{
					list2.Add(item3);
				}
			}
			list2.Sort((WebMapDirtyChunkCoord a, WebMapDirtyChunkCoord b) =>
			{
				int num = a.Dimension.CompareTo(b.Dimension);
				if (num != 0)
				{
					return num;
				}
				num = a.ChunkZ.CompareTo(b.ChunkZ);
				return (num != 0) ? num : a.ChunkX.CompareTo(b.ChunkX);
			});
			try
			{
				Rewrite(list2);
			}
			catch
			{
				lock (gate)
				{
					foreach (WebMapDirtyChunkCoord item4 in list)
					{
						if (TryPack(item4.Dimension, item4.ChunkX, item4.ChunkZ, out var packed2))
						{
							dirtyPacked.Add(packed2);
						}
						else
						{
							dirtyOverflow.Add(item4);
						}
					}
				}
				throw;
			}
			return true;
		}
	}

	public void AcknowledgeAllSnapshot(HashSet<WebMapDirtyChunkCoord> exported)
	{
		Acknowledge(exported);
	}

	private void EnsureLoaded()
	{
		lock (loadGate)
		{
			if (!loaded && !disposed)
			{
				Load();
				loaded = true;
			}
		}
	}

	private void Load()
	{
		try
		{
			Directory.CreateDirectory(rootPath);
			if (!File.Exists(path))
			{
				return;
			}
			foreach (string item in File.ReadLines(path))
			{
				string text = item.Trim();
				if (text.Length == 0)
				{
					continue;
				}
				string[] array = text.Split('|');
				if (array.Length == 5 && !(array[0] != "v1") && string.Equals(array[1], worldId, StringComparison.Ordinal) && int.TryParse(array[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && int.TryParse(array[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result2) && int.TryParse(array[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result3))
				{
					if (TryPack(result, result2, result3, out var packed))
					{
						dirtyPacked.Add(packed);
					}
					else
					{
						dirtyOverflow.Add(new WebMapDirtyChunkCoord(result, result2, result3));
					}
				}
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/WebMap] dirty terrain journal load failed: {0}: {1}. Incremental export will conservatively fall back when needed.", ex.GetType().Name, ex.Message);
		}
	}

	private async Task WriterLoopAsync()
	{
		while (!stop.IsCancellationRequested)
		{
			try
			{
				await wake.WaitAsync(stop.Token).ConfigureAwait(continueOnCapturedContext: false);
				await Task.Delay(200, stop.Token).ConfigureAwait(continueOnCapturedContext: false);
				FlushPendingWrites();
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex2)
			{
				api.Logger.Warning("[ServerEssentials/WebMap] dirty terrain journal writer failed: {0}: {1}", ex2.GetType().Name, ex2.Message);
				try
				{
					await Task.Delay(5000, stop.Token).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (OperationCanceledException)
				{
					break;
				}
				SignalWriter();
			}
		}
		try
		{
			FlushPendingWrites();
		}
		catch
		{
		}
	}

	private void FlushPendingWrites()
	{
		if (pendingPackedWrites.IsEmpty && pendingOverflowWrites.IsEmpty)
		{
			return;
		}
		lock (fileGate)
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			try
			{
				Directory.CreateDirectory(rootPath);
				List<long> list = new List<long>(1024);
				List<WebMapDirtyChunkCoord> list2 = new List<WebMapDirtyChunkCoord>(1024);
				while (!pendingPackedWrites.IsEmpty || !pendingOverflowWrites.IsEmpty)
				{
					list.Clear();
					list2.Clear();
					writeBuffer.Clear();
					writeBuffer.Append('\n');
					foreach (long pendingPackedWrite in pendingPackedWrites)
					{
						list.Add(pendingPackedWrite);
						AppendRow(writeBuffer, Unpack(pendingPackedWrite));
						if (list.Count == 1024)
						{
							break;
						}
					}
					foreach (WebMapDirtyChunkCoord pendingOverflowWrite in pendingOverflowWrites)
					{
						list2.Add(pendingOverflowWrite);
						AppendRow(writeBuffer, pendingOverflowWrite);
						if (list2.Count == 1024)
						{
							break;
						}
					}
					if (list.Count == 0 && list2.Count == 0)
					{
						break;
					}
					File.AppendAllText(path, writeBuffer.ToString(), Encoding.UTF8);
					for (int i = 0; i < list.Count; i++)
					{
						pendingPackedWrites.TryDequeue(out var _);
					}
					for (int j = 0; j < list2.Count; j++)
					{
						pendingOverflowWrites.TryDequeue(out var _);
					}
				}
			}
			finally
			{
				writeBuffer.Clear();
				if (writeBuffer.Capacity > 65536)
				{
					writeBuffer.Capacity = 4096;
				}
			}
			stopwatch.Stop();
			SuiteTimingRegistry.Record("Web Map dirty journal flush", stopwatch.Elapsed.TotalMilliseconds);
		}
	}

	private void AppendRow(StringBuilder sb, WebMapDirtyChunkCoord coord)
	{
		sb.Append("v1|").Append(worldId).Append('|')
			.Append(coord.Dimension.ToString(CultureInfo.InvariantCulture))
			.Append('|')
			.Append(coord.ChunkX.ToString(CultureInfo.InvariantCulture))
			.Append('|')
			.Append(coord.ChunkZ.ToString(CultureInfo.InvariantCulture))
			.Append('\n');
	}

	private void Rewrite(List<WebMapDirtyChunkCoord> rows)
	{
		string sourceFileName = path + ".tmp";
		lock (fileGate)
		{
			Directory.CreateDirectory(rootPath);
			using (StreamWriter streamWriter = new StreamWriter(sourceFileName, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
			{
				foreach (WebMapDirtyChunkCoord row in rows)
				{
					streamWriter.Write("v1|");
					streamWriter.Write(worldId);
					streamWriter.Write('|');
					streamWriter.Write(row.Dimension.ToString(CultureInfo.InvariantCulture));
					streamWriter.Write('|');
					streamWriter.Write(row.ChunkX.ToString(CultureInfo.InvariantCulture));
					streamWriter.Write('|');
					streamWriter.WriteLine(row.ChunkZ.ToString(CultureInfo.InvariantCulture));
				}
			}
			File.Move(sourceFileName, path, overwrite: true);
		}
	}

	private static bool TryPack(int dimension, int chunkX, int chunkZ, out long packed)
	{
		if (dimension < -32768 || dimension > 32767 || chunkX < -8388608 || chunkX >= 8388608 || chunkZ < -8388608 || chunkZ >= 8388608)
		{
			packed = 0L;
			return false;
		}
		ulong num = (ushort)(short)dimension;
		ulong num2 = (uint)((chunkX + 8388608) & 0xFFFFFF);
		ulong num3 = (uint)((chunkZ + 8388608) & 0xFFFFFF);
		packed = (long)((num << 48) | (num2 << 24) | num3);
		return true;
	}

	private static WebMapDirtyChunkCoord Unpack(long packed)
	{
		int dimension = (short)(packed >>> 48);
		int chunkX = (int)((packed >>> 24) & 0xFFFFFF) - 8388608;
		int chunkZ = (int)(packed & 0xFFFFFF) - 8388608;
		return new WebMapDirtyChunkCoord(dimension, chunkX, chunkZ);
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

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}
		disposed = true;
		try
		{
			FlushPendingWrites();
		}
		catch
		{
		}
		try
		{
			stop.Cancel();
		}
		catch
		{
		}
		try
		{
			wake.Release();
		}
		catch
		{
		}
		Task task;
		lock (workerGate)
		{
			task = worker;
		}
		try
		{
			task?.Wait(TimeSpan.FromSeconds(2L));
		}
		catch
		{
		}
		try
		{
			stop.Dispose();
		}
		catch
		{
		}
		try
		{
			wake.Dispose();
		}
		catch
		{
		}
	}
}
