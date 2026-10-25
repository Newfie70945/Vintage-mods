using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

/// <summary>
/// Persistent, event-driven dirty map-chunk journal.
///
/// Common-world coordinates are packed into one Int64 inside the resident HashSet, reducing
/// per-entry RAM and hashing cost versus a three-int struct. Extremely large coordinates or
/// dimension ids fall back to the lossless struct set, so correctness is never traded for packing.
/// The game thread performs only a deduplicated set insertion plus queue signal. Disk persistence
/// remains event-driven; there is no polling timer.
/// </summary>
public sealed class WebMapDirtyChunkJournal : IDisposable
{
    private const int ChunkSize = 32;
    private const string FileName = "dirty-mapchunks-v1.log";

    // 16 bits dimension + 24 bits chunk X + 24 bits chunk Z = 64 bits.
    // Signed 24-bit chunk coordinates cover +/- 8,388,608 map chunks (> +/-268m blocks).
    private const int PackedCoordBias = 1 << 23;
    private const int PackedCoordMask = (1 << 24) - 1;

    private readonly ICoreServerAPI api;
    private readonly string rootPath;
    private readonly string path;
    private readonly string worldId;
    private readonly object gate = new();
    private readonly object fileGate = new();
    private readonly HashSet<long> dirtyPacked = new();
    private readonly HashSet<WebMapDirtyChunkCoord> dirtyOverflow = new();
    // Chunks marked again after the outstanding export snapshot was taken. Tiny compared with the dirty set, and it lets
    // an export acknowledge everything it rendered except those chunks, instead of nothing at all.
    private readonly HashSet<long> touchedPacked = new();
    private readonly HashSet<WebMapDirtyChunkCoord> touchedOverflow = new();
    private bool trackTouches;
    private long trackedSnapshotVersion = -1;
    private readonly ConcurrentQueue<long> pendingPackedWrites = new();
    private readonly ConcurrentQueue<WebMapDirtyChunkCoord> pendingOverflowWrites = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly object workerGate = new();
    private readonly object loadGate = new();
    private Task? worker;
    private bool loaded;
    private readonly StringBuilder writeBuffer = new(4096);
    private long mutationVersion;
    private bool disposed;

    public WebMapDirtyChunkJournal(ICoreServerAPI api)
    {
        this.api = api;
        rootPath = api.GetOrCreateDataPath("NewfiesServerEssentialsWebMap");
        path = Path.Combine(rootPath, FileName);
        worldId = api.World.SavegameIdentifier ?? api.WorldManager.SaveGame.SavegameIdentifier ?? "unknownworld";
    }

    public int Count
    {
        get
        {
            EnsureLoaded();
            lock (gate) return dirtyPacked.Count + dirtyOverflow.Count;
        }
    }

    /// <summary>
    /// Monotonic in-memory change counter. It advances even when a block change hits a
    /// coordinate that was already dirty, allowing a live forced export to detect that
    /// gameplay changed terrain while it was rendering and conservatively keep the journal.
    /// </summary>
    public long MutationVersion => Interlocked.Read(ref mutationVersion);

    public void MarkBlockDirty(int dimension, int blockX, int blockZ)
    {
        if (disposed) return;
        EnsureLoaded();
        int chunkX = FloorDiv(blockX, ChunkSize);
        int chunkZ = FloorDiv(blockZ, ChunkSize);
        lock (gate)
        {
            if (disposed) return;
            // Generation and insertion are atomic relative to export acknowledgement.
            Interlocked.Increment(ref mutationVersion);
            if (TryPack(dimension, chunkX, chunkZ, out long packed))
            {
                if (trackTouches) touchedPacked.Add(packed);
                if (!dirtyPacked.Add(packed)) return;
                pendingPackedWrites.Enqueue(packed);
            }
            else
            {
                var coord = new WebMapDirtyChunkCoord(dimension, chunkX, chunkZ);
                if (trackTouches) touchedOverflow.Add(coord);
                if (!dirtyOverflow.Add(coord)) return;
                pendingOverflowWrites.Enqueue(coord);
            }
        }
        EnsureWorkerStarted();
        SignalWriter();
    }

    private void SignalWriter()
    {
        // One pending wake covers the whole burst; thousands of edits must not schedule
        // thousands of subsequent 200 ms delays after the work is already persisted.
        try { if (wake.CurrentCount == 0) wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private void EnsureWorkerStarted()
    {
        lock (workerGate)
        {
            if (disposed || worker != null) return;
            worker = Task.Run(WriterLoopAsync);
        }
    }

    private long liveCandidateOffset;
    // Worker-side, bounded look-ahead; rotate so unloaded old edits do not hide loaded edits forever.
    public WebMapDirtyChunkCoord[] NextLiveCandidates(HashSet<WebMapDirtyChunkCoord> snapshot,int maximum)
    {
        var coords=snapshot.Where(d=>d.Dimension==0).ToArray();
        if(coords.Length==0)return Array.Empty<WebMapDirtyChunkCoord>();
        int count=Math.Min(coords.Length,Math.Clamp(maximum,1,2048));
        long end=Interlocked.Add(ref liveCandidateOffset,count);
        int start=(int)((end-count)%coords.Length);
        var result=new WebMapDirtyChunkCoord[count];
        for(int i=0;i<count;i++)result[i]=coords[(start+i)%coords.Length];
        return result;
    }

    public HashSet<WebMapDirtyChunkCoord> Snapshot() => Snapshot(out _);

    public HashSet<WebMapDirtyChunkCoord> Snapshot(out long snapshotMutationVersion)
    {
        EnsureLoaded();
        lock (gate)
        {
            snapshotMutationVersion = MutationVersion;
            trackTouches = true;
            trackedSnapshotVersion = snapshotMutationVersion;
            touchedPacked.Clear();
            touchedOverflow.Clear();
            var result = new HashSet<WebMapDirtyChunkCoord>(dirtyPacked.Count + dirtyOverflow.Count);
            foreach (long packed in dirtyPacked) result.Add(Unpack(packed));
            foreach (var coord in dirtyOverflow) result.Add(coord);
            return result;
        }
    }

    /// <summary>
    /// Remove only coordinates that were part of a successful export snapshot. Changes marked
    /// after that snapshot remain dirty for the next startup.
    /// </summary>
    public void Acknowledge(IEnumerable<WebMapDirtyChunkCoord> exported)
        => TryAcknowledgeSnapshot(exported, MutationVersion);

    public bool TryAcknowledgeSnapshot(IEnumerable<WebMapDirtyChunkCoord> exported, long expectedMutationVersion)
    {
        if (disposed) return false;
        EnsureLoaded();
        // Hold the file gate across the snapshot and rewrite. A newer append must not
        // complete between them and then get overwritten by an older compaction snapshot.
        lock (fileGate)
        {
            FlushPendingWrites();
            var removed = new List<WebMapDirtyChunkCoord>();
            var remaining = new List<WebMapDirtyChunkCoord>();
            lock (gate)
            {
                if (disposed) return false;
                bool mutatedSinceSnapshot = MutationVersion != expectedMutationVersion;
                // Edits during the export only keep the chunks they touched dirty. If this is not the snapshot that is
                // being tracked (a newer one was taken), nothing can be proven, so stay fully conservative.
                if (mutatedSinceSnapshot && !(trackTouches && trackedSnapshotVersion == expectedMutationVersion)) return false;
                foreach (var coord in exported)
                {
                    if (mutatedSinceSnapshot)
                    {
                        bool touched = TryPack(coord.Dimension, coord.ChunkX, coord.ChunkZ, out long touchedKey)
                            ? touchedPacked.Contains(touchedKey) : touchedOverflow.Contains(coord);
                        if (touched) continue;
                    }
                    bool changed = TryPack(coord.Dimension, coord.ChunkX, coord.ChunkZ, out long packed)
                        ? dirtyPacked.Remove(packed) : dirtyOverflow.Remove(coord);
                    if (changed) removed.Add(coord);
                }
                if (trackedSnapshotVersion == expectedMutationVersion)
                {
                    trackTouches = false;
                    trackedSnapshotVersion = -1;
                    touchedPacked.Clear();
                    touchedOverflow.Clear();
                }
                if (removed.Count == 0) return true;
                foreach (long packed in dirtyPacked) remaining.Add(Unpack(packed));
                foreach (var coord in dirtyOverflow) remaining.Add(coord);
            }
            remaining.Sort(static (a, b) =>
            {
                int c = a.Dimension.CompareTo(b.Dimension);
                if (c != 0) return c;
                c = a.ChunkZ.CompareTo(b.ChunkZ);
                return c != 0 ? c : a.ChunkX.CompareTo(b.ChunkX);
            });
            try { Rewrite(remaining); }
            catch
            {
                // The old file is intact. Keep the in-memory snapshot conservative too.
                lock (gate)
                    foreach (var coord in removed)
                        if (TryPack(coord.Dimension, coord.ChunkX, coord.ChunkZ, out long packed)) dirtyPacked.Add(packed);
                        else dirtyOverflow.Add(coord);
                throw;
            }
            return true;
        }
    }

    public void AcknowledgeAllSnapshot(HashSet<WebMapDirtyChunkCoord> exported) => Acknowledge(exported);

    private void EnsureLoaded()
    {
        lock (loadGate)
        {
            if (loaded || disposed) return;
            Load();
            loaded = true;
        }
    }

    private void Load()
    {
        try
        {
            Directory.CreateDirectory(rootPath);
            if (!File.Exists(path)) return;
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split('|');
                if (parts.Length != 5 || parts[0] != "v1") continue;
                if (!string.Equals(parts[1], worldId, StringComparison.Ordinal)) continue;
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dimension)) continue;
                if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int chunkX)) continue;
                if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int chunkZ)) continue;
                if (TryPack(dimension, chunkX, chunkZ, out long packed)) dirtyPacked.Add(packed);
                else dirtyOverflow.Add(new WebMapDirtyChunkCoord(dimension, chunkX, chunkZ));
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
                await wake.WaitAsync(stop.Token).ConfigureAwait(false);
                // Coalesce short build/mining bursts into one append instead of turning many
                // adjacent chunk changes into a sequence of tiny filesystem writes.
                await Task.Delay(200, stop.Token).ConfigureAwait(false);
                FlushPendingWrites();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                api.Logger.Warning("[ServerEssentials/WebMap] dirty terrain journal writer failed: {0}: {1}", ex.GetType().Name, ex.Message);
                // Retry retained rows without requiring another edit, at a bounded cadence.
                try { await Task.Delay(5000, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                SignalWriter();
            }
        }
        try { FlushPendingWrites(); } catch { }
    }

    private void FlushPendingWrites()
    {
        if (pendingPackedWrites.IsEmpty && pendingOverflowWrites.IsEmpty) return;
        lock (fileGate)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Directory.CreateDirectory(rootPath);
                // Snapshot only a small prefix. Dequeue after append succeeds; a partial
                // failed append may replay rows, which the loader safely deduplicates.
                var packedBatch = new List<long>(1024);
                var overflowBatch = new List<WebMapDirtyChunkCoord>(1024);
                while (!pendingPackedWrites.IsEmpty || !pendingOverflowWrites.IsEmpty)
                {
                    packedBatch.Clear(); overflowBatch.Clear(); writeBuffer.Clear();
                    // A failed append can leave an unterminated row. Separate the retry
                    // from that tail so its first full row is not swallowed by corruption.
                    writeBuffer.Append('\n');
                    foreach (long packed in pendingPackedWrites)
                    {
                        packedBatch.Add(packed); AppendRow(writeBuffer, Unpack(packed));
                        if (packedBatch.Count == 1024) break;
                    }
                    foreach (var coord in pendingOverflowWrites)
                    {
                        overflowBatch.Add(coord); AppendRow(writeBuffer, coord);
                        if (overflowBatch.Count == 1024) break;
                    }
                    if (packedBatch.Count == 0 && overflowBatch.Count == 0) break;
                    File.AppendAllText(path, writeBuffer.ToString(), Encoding.UTF8);
                    for (int i = 0; i < packedBatch.Count; i++) pendingPackedWrites.TryDequeue(out _);
                    for (int i = 0; i < overflowBatch.Count; i++) pendingOverflowWrites.TryDequeue(out _);
                }
            }
            finally
            {
                // A large one-off dirty burst must not permanently pin an equally large
                // char[] through this reusable builder after the disk append completes/fails.
                writeBuffer.Clear();
                if (writeBuffer.Capacity > 64 * 1024) writeBuffer.Capacity = 4096;
            }
            sw.Stop();
            BlockLogger.Services.SuiteTimingRegistry.Record("Web Map dirty journal flush", sw.Elapsed.TotalMilliseconds);
        }
    }

    private void AppendRow(StringBuilder sb, WebMapDirtyChunkCoord coord)
    {
        sb.Append("v1|").Append(worldId).Append('|')
          .Append(coord.Dimension.ToString(CultureInfo.InvariantCulture)).Append('|')
          .Append(coord.ChunkX.ToString(CultureInfo.InvariantCulture)).Append('|')
          .Append(coord.ChunkZ.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private void Rewrite(List<WebMapDirtyChunkCoord> rows)
    {
        string tmp = path + ".tmp";
        lock (fileGate)
        {
            Directory.CreateDirectory(rootPath);
            using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                foreach (var coord in rows)
                {
                    writer.Write("v1|"); writer.Write(worldId); writer.Write('|');
                    writer.Write(coord.Dimension.ToString(CultureInfo.InvariantCulture)); writer.Write('|');
                    writer.Write(coord.ChunkX.ToString(CultureInfo.InvariantCulture)); writer.Write('|');
                    writer.WriteLine(coord.ChunkZ.ToString(CultureInfo.InvariantCulture));
                }
            }
            File.Move(tmp, path, true);
        }
    }

    private static bool TryPack(int dimension, int chunkX, int chunkZ, out long packed)
    {
        if (dimension < short.MinValue || dimension > short.MaxValue ||
            chunkX < -PackedCoordBias || chunkX >= PackedCoordBias ||
            chunkZ < -PackedCoordBias || chunkZ >= PackedCoordBias)
        {
            packed = 0;
            return false;
        }

        ulong d = (ushort)(short)dimension;
        ulong x = (uint)(chunkX + PackedCoordBias) & PackedCoordMask;
        ulong z = (uint)(chunkZ + PackedCoordBias) & PackedCoordMask;
        packed = unchecked((long)((d << 48) | (x << 24) | z));
        return true;
    }

    private static WebMapDirtyChunkCoord Unpack(long packed)
    {
        ulong value = unchecked((ulong)packed);
        int dimension = (short)(value >> 48);
        int chunkX = (int)((value >> 24) & PackedCoordMask) - PackedCoordBias;
        int chunkZ = (int)(value & PackedCoordMask) - PackedCoordBias;
        return new WebMapDirtyChunkCoord(dimension, chunkX, chunkZ);
    }

    private static int FloorDiv(int value, int divisor)
    {
        int q = value / divisor;
        int r = value % divisor;
        if (r != 0 && ((r < 0) != (divisor < 0))) q--;
        return q;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { FlushPendingWrites(); } catch { }
        try { stop.Cancel(); } catch { }
        try { wake.Release(); } catch { }
        Task? activeWorker;
        lock (workerGate) activeWorker = worker;
        try { activeWorker?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { stop.Dispose(); } catch { }
        try { wake.Dispose(); } catch { }
    }
}

public readonly record struct WebMapDirtyChunkCoord(int Dimension, int ChunkX, int ChunkZ);
