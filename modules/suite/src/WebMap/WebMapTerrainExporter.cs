using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlockLogger.Services;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

/// <summary>
/// Startup-only terrain exporter for Newfie's Native Web Map.
///
/// 1.5.26 optimized terrain model:
/// - Enumerates only already-generated map chunks; never generates unexplored terrain.
/// - Uses a persistent BlockLogger-driven dirty map-chunk journal.
/// - Can reuse unchanged PNG tiles and regenerate only dirty/new/missing tiles when incremental mode is enabled.
/// - Uses pooled pixel/height buffers and one-tile-at-a-time bounded processing.
/// - Trusts the already-enumerated VCDBS coverage set instead of repeating blocking existence checks per chunk.
/// - Lazily unpacks only vertical chunk sections actually needed by surface sampling.
/// - Resolves missing height metadata with one cache-friendly top-down column sweep rather than 1,024 independent scans.
/// - Caches block-id to deterministic server-side map colors during the startup export.
/// - Stores one 32x32-block overview sample per generated map chunk while the detailed tile is already in memory.
/// - Stages incremental tile changes and validates them before committing them to the public cache.
/// - Performs no continuous terrain scanning; automatic runtime terrain exports are disabled.
/// </summary>
internal sealed class WebMapTerrainExporter
{
    private const int ChunkSize = 32;
    private const int DimensionMask = 31;
    private const long CoordMask21 = (1L << 21) - 1;
    private const int ManifestVersion = 12;
    private static readonly string[] DerivedTerrainStyles = { "sepia", "topo" };

    private readonly ICoreServerAPI api;
    private readonly string rootPath;
    private readonly Action<WebMapTerrainExportStatus> publishStatus;
    private readonly WebMapDirtyChunkJournal? dirtyJournal;
    private readonly Dictionary<int, Rgb> blockColorCache = new();
    private Rgb[] blockColorById = Array.Empty<Rgb>();
    private int blockPalettePrimed;
    private long lastConsoleProgressUnixMilliseconds;
    private long lastStatusProgressPublishUnixMilliseconds;
    private int lastConsoleProgressPercent = -1;
    private long progressPhaseStartedUnixMilliseconds;
    private readonly Queue<double> recentTileMilliseconds = new();
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
    private readonly Dictionary<int, long> sampledBlockCounts = new();
    private readonly object sampledBlockCountsLock = new();

    private static void RecordTimingSummary(WebMapTerrainExportStatus status)
    {
        if (status.TotalElapsedMilliseconds > 0) SuiteTimingRegistry.Record("Web Map terrain export", status.TotalElapsedMilliseconds);
        if (status.CoverageScanMilliseconds > 0) SuiteTimingRegistry.Record("Web Map terrain coverage scan", status.CoverageScanMilliseconds);
        if (status.WorldReadMilliseconds > 0) SuiteTimingRegistry.Record("Web Map terrain reads", status.WorldReadMilliseconds);
        if (status.RenderMilliseconds > 0) SuiteTimingRegistry.Record("Web Map terrain rendering", status.RenderMilliseconds);
        if (status.PngWriteMilliseconds > 0) SuiteTimingRegistry.Record("Web Map PNG encoding", status.PngWriteMilliseconds);
        if (status.LodWriteMilliseconds > 0) SuiteTimingRegistry.Record("Web Map pyramid generation", status.LodWriteMilliseconds);
    }

    public WebMapTerrainExporter(
        ICoreServerAPI api,
        string rootPath,
        Action<WebMapTerrainExportStatus> publishStatus,
        WebMapDirtyChunkJournal? dirtyJournal)
    {
        this.api = api;
        this.rootPath = rootPath;
        this.publishStatus = publishStatus;
        this.dirtyJournal = dirtyJournal;
    }

    public bool Export(WebMapConfig config, string trigger = "startup", bool requireIncremental = false)
    {
        using var exportEntityLogScope = WebMapExportLogSuppressor.Begin(api);
        var totalSw = Stopwatch.StartNew();
        var status = new WebMapTerrainExportStatus
        {
            State = "exporting",
            Phase = "coverage",
            ProgressPercent = 2,
            StartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Message = "Enumerating generated map chunks…",
            TileSize = config.TerrainTileSize,
            PeakWorkingBufferBytes = (long)config.TerrainTileSize * config.TerrainTileSize * 5,
            Trigger = string.IsNullOrWhiteSpace(trigger) ? "startup" : trigger.Trim().ToLowerInvariant(),
            EstimatedRemainingMilliseconds = -1,
            ManagedHeapBytesAtStart = GC.GetTotalMemory(false),
            ProcessWorkingSetBytesAtStart = ReadProcessWorkingSet()
        };
        publishStatus(status.Clone());
        lastConsoleProgressUnixMilliseconds = status.StartedUnixMilliseconds;
        lastStatusProgressPublishUnixMilliseconds = status.StartedUnixMilliseconds;
        lastConsoleProgressPercent = -1;
        api.Logger.Notification("[ServerEssentials/WebMap] terrain {0} export started. Enumerating already-generated map chunks; the server may remain busy while this runs.", status.Trigger);
        ResetSurfaceDiagnostics();

        string currentDir = Path.Combine(rootPath, "terrain");
        string stageDir = Path.Combine(rootPath, "terrain-next");
        string backupDir = Path.Combine(rootPath, "terrain-previous");
        long dirtyMutationVersion = 0;
        var dirtySnapshot = dirtyJournal?.Snapshot(out dirtyMutationVersion) ?? new HashSet<WebMapDirtyChunkCoord>();
        status.DirtyChunkCount = dirtySnapshot.Count;

        try
        {
            PrimeBlockColorCache();
            string savePath = ResolveCurrentWorldFilepath();
            var coverageSw = Stopwatch.StartNew();
            var coverage = EnumerateGeneratedTiles(savePath, config.TerrainTileSize);
            coverageSw.Stop();
            status.CoverageScanMilliseconds = coverageSw.ElapsedMilliseconds;
            status.TotalChunkColumns = coverage.ChunkColumns;
            status.TotalTiles = coverage.Tiles.Count;
            api.Logger.Notification(
                "[ServerEssentials/WebMap] terrain coverage scan complete: {0:n0} generated chunk columns across {1:n0} terrain tiles ({2:n0}ms).",
                status.TotalChunkColumns, status.TotalTiles, status.CoverageScanMilliseconds);

            using WebMapSaveDbTerrainReader? directReader = OpenDirectSaveReader(config, savePath, status);
            if (config.UseDirectSaveDatabaseTerrain && directReader == null)
                throw new InvalidOperationException("Safe save-database terrain decoding is unavailable. See the preceding reader diagnostic; the published map has been kept.");
            if (!config.UseDirectSaveDatabaseTerrain)
                api.Logger.Warning("[ServerEssentials/WebMap] legacy world-loader terrain export explicitly selected. It reconstructs entity/mod state and can increase server memory. Enable UseDirectSaveDatabaseTerrain for terrain-only decoding.");
            ValidateStartupCoverageCoordinates(coverage);

            WebMapTerrainManifest? previous = LoadManifest(currentDir);
            string currentSaveId = api.WorldManager.SaveGame.SavegameIdentifier ?? "";
            bool canIncrement = config.UseIncrementalTerrainExport &&
                                previous != null &&
                                previous.Version == ManifestVersion &&
                                previous.TileSize == config.TerrainTileSize &&
                                string.Equals(previous.SavegameIdentifier ?? "", currentSaveId, StringComparison.Ordinal) &&
                                previous.Tiles != null &&
                                Directory.Exists(currentDir);

            bool manualOneShotNeedsRealRender =
                canIncrement &&
                string.Equals(status.Trigger, "startup-one-shot", StringComparison.OrdinalIgnoreCase) &&
                dirtySnapshot.Count == 0;

            if (!canIncrement || manualOneShotNeedsRealRender)
            {
                status.ExportMode = "full";
                status.FullRebuildReason = manualOneShotNeedsRealRender
                    ? "Manual next-start export was explicitly requested and there are no dirty chunks; escalating the no-op incremental check to a full rebuild."
                    : !config.UseIncrementalTerrainExport ? "Incremental terrain exports are disabled by administrator." :
                    previous == null ? "No existing terrain manifest." :
                    previous.Version != ManifestVersion ? "Terrain renderer/cache version changed." :
                    previous.TileSize != config.TerrainTileSize ? "Terrain tile size changed." :
                    !string.Equals(previous.SavegameIdentifier ?? "", currentSaveId, StringComparison.Ordinal) ? "World/save identity changed." :
                    "Existing terrain cache is incomplete.";

                api.Logger.Notification("[ServerEssentials/WebMap] terrain export selected a full rebuild: {0}", status.FullRebuildReason);

                if (requireIncremental)
                {
                    totalSw.Stop();
                    status.State = "ready";
                    status.Phase = "complete";
                    status.ProgressPercent = 100;
                    status.ExportMode = "scheduled-skipped";
                    status.TotalElapsedMilliseconds = totalSw.ElapsedMilliseconds;
                    status.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    status.EstimatedRemainingMilliseconds = 0;
                    status.Message = $"Scheduled dirty export skipped: {status.FullRebuildReason} A full rebuild is only allowed during startup.";
                    publishStatus(status.Clone());
                    api.Logger.Warning("[ServerEssentials/WebMap] incremental-only terrain export skipped: {0} Dirty journal was preserved for the next startup rebuild.", status.FullRebuildReason);
                    RecordTimingSummary(status);
                    return false;
                }

                FullExport(config, coverage, stageDir, currentDir, backupDir, status, directReader, savePath);
                dirtyJournal?.TryAcknowledgeSnapshot(dirtySnapshot, dirtyMutationVersion);
            }
            else
            {
                status.ExportMode = "incremental";
                IncrementalExport(config, coverage, stageDir, currentDir, previous!, dirtySnapshot, status, directReader);
                dirtyJournal?.TryAcknowledgeSnapshot(dirtySnapshot, dirtyMutationVersion);
            }

            totalSw.Stop();
            status.TotalElapsedMilliseconds = totalSw.ElapsedMilliseconds;
            status.State = "ready";
            status.Phase = "complete";
            status.ProgressPercent = 100;
            status.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            status.LastSuccessfulUnixMilliseconds = status.CompletedUnixMilliseconds;
            status.LastError = "";
            status.EstimatedRemainingMilliseconds = 0;
            if (status.ExportMode == "incremental")
            {
                status.Message = status.WrittenTiles == 0
                    ? $"Incremental terrain check complete: all {status.ReusedTiles:n0} cached tiles reused; no terrain tiles required rendering."
                    : $"Incremental terrain export complete: {status.WrittenTiles:n0} changed/new tiles rendered, {status.ReusedTiles:n0} unchanged tiles reused.";
            }
            else
            {
                status.Message = status.WrittenTiles == 0
                    ? "Full terrain export completed; no generated overworld tiles were present."
                    : $"Full terrain export complete: {status.WrittenTiles:n0} tiles from {status.ProcessedChunkColumns:n0} chunk columns.";
            }
            publishStatus(status.Clone());
            api.Logger.Notification(
                "[ServerEssentials/WebMap] terrain {0} export complete. mode={1}, readMode={2}, rendered={3}, reused={4}, dirtyChunks={5}, elapsed={6}ms, worldRead={7}ms, render={8}ms, png={9}ms, lod={10}ms, resolvedSurface={11:n0}, unresolvedSurface={12:n0}.",
                status.Trigger, status.ExportMode, status.TerrainReadMode, status.WrittenTiles, status.ReusedTiles, status.DirtyChunkCount, status.TotalElapsedMilliseconds,
                status.WorldReadMilliseconds, status.RenderMilliseconds, status.PngWriteMilliseconds, status.LodWriteMilliseconds, resolvedSurfaceSampleCount, unresolvedSurfaceSampleCount);
            RecordTimingSummary(status);
            return true;
        }
        catch (OperationCanceledException)
        {
            totalSw.Stop();
            try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
            status.State = "cancelled";
            status.Phase = "cancelled";
            status.ProgressPercent = 100;
            status.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            status.TotalElapsedMilliseconds = totalSw.ElapsedMilliseconds;
            status.EstimatedRemainingMilliseconds = -1;
            status.Message = "Forced terrain export cancelled because the Native Web Map was turned OFF. The previously published terrain cache was kept.";
            status.LastError = "";
            publishStatus(status.Clone());
            api.Logger.Notification("[ServerEssentials/WebMap] forced live terrain export cancelled cleanly; existing terrain cache was preserved.");
            RecordTimingSummary(status);
            return false;
        }
        catch (Exception ex)
        {
            totalSw.Stop();
            try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
            status.State = "failed";
            status.Phase = "failed";
            status.ProgressPercent = 100;
            status.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            status.TotalElapsedMilliseconds = totalSw.ElapsedMilliseconds;
            status.Message = "Terrain export failed; existing successfully-written terrain cache was kept and dirty entries remain queued.";
            status.LastError = ex.GetType().Name + ": " + ex.Message;
            publishStatus(status.Clone());
            status.EstimatedRemainingMilliseconds = -1;
            api.Logger.Error("[ServerEssentials/WebMap] terrain {0} export failed: {1}: {2}. Dirty terrain journal was preserved for retry.", status.Trigger, ex.GetType().Name, ex.Message);
            RecordTimingSummary(status);
            return false;
        }
        finally
        {
            // The using reader and parallel worker readers are already disposed here.
            // Drop export-only references even if a caller keeps this exporter for diagnostics.
            blockColorCache.Clear();
            blockColorById = Array.Empty<Rgb>();
            blockPalettePrimed = 0;
            lock (sampledBlockCountsLock) sampledBlockCounts.Clear();
            recentTileMilliseconds.Clear();
            status.ExportResourcesReleased = true;
            status.ManagedHeapBytesAtEnd = GC.GetTotalMemory(false);
            status.ProcessWorkingSetBytesAtEnd = ReadProcessWorkingSet();
            publishStatus(status.Clone());
            api.Logger.Notification("[ServerEssentials/WebMap] export resources released. managedHeap={0:n0}->{1:n0} bytes, processWorkingSet={2:n0}->{3:n0} bytes, legacyWorldColumns={4:n0}. Managed heap and OS/container memory can remain high until normal collection/cache reclaim.",
                status.ManagedHeapBytesAtStart, status.ManagedHeapBytesAtEnd, status.ProcessWorkingSetBytesAtStart, status.ProcessWorkingSetBytesAtEnd, status.WorldLoaderChunkColumns);
        }
    }

    private static long ReadProcessWorkingSet()
    {
        try { using var process = Process.GetCurrentProcess(); return process.WorkingSet64; }
        catch { return -1; }
    }

    /// <summary>
    /// Explicit live-server export used by authenticated changed-terrain and full-rebuild actions.
    /// The preferred v10 path reads surface terrain from the save database without attaching
    /// chunks to the live world. If that runtime-internal decoder is unavailable, the fallback
    /// remains the supported asynchronous LoadChunkColumnPriority world-loader path.
    /// </summary>
    public async Task<bool> ExportRuntimeForcedAsync(WebMapConfig config, CancellationToken cancellationToken, bool fullRebuild)
    {
        using var exportEntityLogScope = WebMapExportLogSuppressor.Begin(api);
        var totalSw = Stopwatch.StartNew();
        var status = new WebMapTerrainExportStatus
        {
            State = "exporting",
            Phase = "coverage",
            ProgressPercent = 2,
            StartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Message = fullRebuild
                ? "Full live rebuild: enumerating generated map chunks…"
                : "Changed-terrain update: enumerating generated map chunks…",
            TileSize = config.TerrainTileSize,
            PeakWorkingBufferBytes = (long)config.TerrainTileSize * config.TerrainTileSize * 5,
            Trigger = fullRebuild ? "forced-full" : "runtime-update",
            ExportMode = fullRebuild ? "forced-full" : "runtime-incremental",
            EstimatedRemainingMilliseconds = -1
        };
        publishStatus(status.Clone());
        lastConsoleProgressUnixMilliseconds = status.StartedUnixMilliseconds;
        lastStatusProgressPublishUnixMilliseconds = status.StartedUnixMilliseconds;
        lastConsoleProgressPercent = -1;
        ResetSurfaceDiagnostics();

        string currentDir = Path.Combine(rootPath, "terrain");
        string stageDir = Path.Combine(rootPath, "terrain-next");
        string backupDir = Path.Combine(rootPath, "terrain-previous");
        long dirtyMutationVersion = 0;
        var dirtySnapshot = dirtyJournal?.Snapshot(out dirtyMutationVersion) ?? new HashSet<WebMapDirtyChunkCoord>();
        status.DirtyChunkCount = dirtySnapshot.Count;

        try
        {
            api.Logger.Warning(
                fullRebuild
                    ? "[ServerEssentials/WebMap] administrator started a FULL live terrain rebuild. The read-only save-DB terrain path will be preferred, with cooperative world loading as fallback."
                    : "[ServerEssentials/WebMap] administrator started a changed-terrain live update. Only dirty/new/missing terrain tiles will be rendered; the read-only save-DB terrain path is preferred.");

            await PrimeBlockColorCacheOnMainThreadAsync(cancellationToken).ConfigureAwait(false);
            string savePath = ResolveCurrentWorldFilepath();
            var coverageSw = Stopwatch.StartNew();
            TerrainCoverage coverage = EnumerateGeneratedTiles(savePath, config.TerrainTileSize);
            coverageSw.Stop();
            status.CoverageScanMilliseconds = coverageSw.ElapsedMilliseconds;
            status.TotalChunkColumns = coverage.ChunkColumns;
            using WebMapSaveDbTerrainReader? directReader = OpenDirectSaveReader(config, savePath, status);
            var liveDirtyChunks = new HashSet<ChunkCoord>(dirtySnapshot
                .Where(d => d.Dimension == 0)
                .Select(d => new ChunkCoord(d.ChunkX, d.ChunkZ)));
            if (directReader != null && liveDirtyChunks.Count > 0)
                status.TerrainReadMode = "save-db+live-dirty";

            WebMapTerrainManifest? previous = LoadManifest(currentDir);
            string currentSaveId = api.WorldManager.SaveGame.SavegameIdentifier ?? "";
            bool compatibleIncrementalCache =
                previous != null &&
                previous.Version == ManifestVersion &&
                previous.TileSize == config.TerrainTileSize &&
                string.Equals(previous.SavegameIdentifier ?? "", currentSaveId, StringComparison.Ordinal) &&
                previous.Tiles != null &&
                Directory.Exists(currentDir);

            if (!fullRebuild && !compatibleIncrementalCache)
            {
                totalSw.Stop();
                status.State = "ready";
                status.ExportMode = "runtime-update-needs-rebuild";
                status.TotalElapsedMilliseconds = totalSw.ElapsedMilliseconds;
                status.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                status.EstimatedRemainingMilliseconds = 0;
                status.FullRebuildReason = previous == null ? "No existing terrain manifest." :
                    previous.Version != ManifestVersion ? "Terrain renderer/cache version changed." :
                    previous.TileSize != config.TerrainTileSize ? "Terrain tile size changed." :
                    !string.Equals(previous.SavegameIdentifier ?? "", currentSaveId, StringComparison.Ordinal) ? "World/save identity changed." :
                    "Existing terrain cache is incomplete.";
                status.Message = $"Changed-terrain update not started: {status.FullRebuildReason} Use Full Rebuild Map once, then future updates can be incremental.";
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
                success = await FullExportRuntimeAsync(config, coverage, stageDir, currentDir, backupDir, status, directReader, liveDirtyChunks, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                success = await IncrementalExportRuntimeAsync(config, coverage, stageDir, currentDir, previous!, dirtySnapshot, status, directReader, liveDirtyChunks, cancellationToken).ConfigureAwait(false);
            }

            if (!success) return false;

            if (dirtyJournal != null)
            {
                if (!dirtyJournal.TryAcknowledgeSnapshot(dirtySnapshot, dirtyMutationVersion))
                {
                    api.Logger.Notification("[ServerEssentials/WebMap] terrain changed while the live export was running; dirty journal was conservatively preserved for the next update.");
                }
            }

            totalSw.Stop();
            status.TotalElapsedMilliseconds = totalSw.ElapsedMilliseconds;
            status.State = "ready";
            status.Phase = "complete";
            status.ProgressPercent = 100;
            status.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            status.LastSuccessfulUnixMilliseconds = status.CompletedUnixMilliseconds;
            status.LastError = "";
            status.EstimatedRemainingMilliseconds = 0;
            status.Message = fullRebuild
                ? (status.WrittenTiles == 0
                    ? "Full live terrain rebuild completed; no generated overworld tiles were present."
                    : $"Full live terrain rebuild complete: {status.WrittenTiles:n0} tiles from {status.ProcessedChunkColumns:n0} chunk columns.")
                : (status.WrittenTiles == 0
                    ? $"Changed-terrain update complete: {status.ReusedTiles:n0} cached tiles reused; nothing needed rendering."
                    : $"Changed-terrain update complete: {status.WrittenTiles:n0} changed/new tiles rendered, {status.ReusedTiles:n0} cached tiles reused.");
            publishStatus(status.Clone());
            api.Logger.Notification(
                "[ServerEssentials/WebMap] live terrain export complete. mode={0}, readMode={1}, rendered={2}, reused={3}, chunkColumns={4}, elapsed={5}ms, worldRead={6}ms, render={7}ms, png={8}ms, lod={9}ms.",
                status.ExportMode, status.TerrainReadMode, status.WrittenTiles, status.ReusedTiles, status.ProcessedChunkColumns, status.TotalElapsedMilliseconds, status.WorldReadMilliseconds, status.RenderMilliseconds, status.PngWriteMilliseconds, status.LodWriteMilliseconds);
            RecordTimingSummary(status);
            return true;
        }
        catch (OperationCanceledException)
        {
            totalSw.Stop();
            try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
            status.State = "ready";
            status.Phase = "cancelled";
            status.ProgressPercent = 100;
            status.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            status.TotalElapsedMilliseconds = totalSw.ElapsedMilliseconds;
            status.EstimatedRemainingMilliseconds = 0;
            status.Message = "Live terrain export cancelled. The previously published terrain cache was kept.";
            status.LastError = "";
            publishStatus(status.Clone());
            api.Logger.Notification("[ServerEssentials/WebMap] live terrain export cancelled; existing terrain cache and dirty journal were preserved.");
            RecordTimingSummary(status);
            return false;
        }
        catch (Exception ex)
        {
            totalSw.Stop();
            try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
            status.State = "failed";
            status.Phase = "failed";
            status.ProgressPercent = 100;
            status.CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            status.TotalElapsedMilliseconds = totalSw.ElapsedMilliseconds;
            status.EstimatedRemainingMilliseconds = -1;
            status.Message = "Live terrain export failed; the previously published terrain cache was kept and dirty entries remain queued.";
            status.LastError = ex.GetType().Name + ": " + ex.Message;
            publishStatus(status.Clone());
            api.Logger.Error("[ServerEssentials/WebMap] live terrain export failed: {0}: {1}. Existing map cache and dirty journal were preserved.", ex.GetType().Name, ex.Message);
            RecordTimingSummary(status);
            return false;
        }
    }

    private async Task<bool> FullExportRuntimeAsync(
        WebMapConfig config,
        TerrainCoverage coverage,
        string stageDir,
        string currentDir,
        string backupDir,
        WebMapTerrainExportStatus status,
        WebMapSaveDbTerrainReader? directReader,
        HashSet<ChunkCoord> forceLiveChunks,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true);
        Directory.CreateDirectory(stageDir);
        progressPhaseStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ResetEtaSamples();
        status.Phase = "rendering";
        status.ProgressPercent = 5;
        status.EstimatedRemainingMilliseconds = -1;
        status.AverageMillisecondsPerTile = 0;
        status.ReusedTiles = 0;
        publishStatus(status.Clone());
        LogConsoleProgress("forced-full", 0, coverage.Tiles.Count, 0, status, force: true);

        WebMapTerrainManifest manifest = CreateManifest(config, coverage);
        int written = 0;
        long processedColumns = 0;
        foreach (TileCoord tile in coverage.Tiles.OrderBy(t => t.Z).ThenBy(t => t.X))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuntimeRenderedTile rendered = await RenderTileRuntimeAsync(config, tile, stageDir, coverage.Chunks, status, directReader, forceLiveChunks, cancellationToken).ConfigureAwait(false);
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
            written++;
            status.WrittenTiles = written;
            status.ProcessedChunkColumns = processedColumns;
            UpdateEta(status, written, coverage.Tiles.Count);
            if (written == coverage.Tiles.Count || (written & 15) == 0)
            {
                status.Message = $"Full live rebuild: {written:n0}/{coverage.Tiles.Count:n0} tiles";
                publishStatus(status.Clone());
            }
            LogConsoleProgress("forced-full", written, coverage.Tiles.Count, processedColumns, status);
            await Task.Yield();
        }

        if (written > 0) ValidateAndLogSurfaceDiagnostics(coverage);
        SetProgressPhase(status, "overview", 92, "Building map overview…");
        if (config.ExportNormalTerrain) WriteOverview(stageDir, manifest, status);
        SetProgressPhase(status, "pyramid", 96, "Building zoom levels…");
        if (config.ExportNormalTerrain) WriteLodPyramid(stageDir, manifest, status, config);
        WriteDerivedStyleLodPyramids(stageDir, manifest, status, config);
        SetProgressPhase(status, "finalizing", 99, "Publishing terrain cache…");
        FinalizeManifest(manifest, coverage, written, processedColumns);
        WriteManifest(stageDir, manifest);
        AtomicSwap(stageDir, currentDir, backupDir);
        return true;
    }

    private async Task<bool> IncrementalExportRuntimeAsync(
        WebMapConfig config,
        TerrainCoverage coverage,
        string stageDir,
        string currentDir,
        WebMapTerrainManifest manifest,
        HashSet<WebMapDirtyChunkCoord> dirtySnapshot,
        WebMapTerrainExportStatus status,
        WebMapSaveDbTerrainReader? directReader,
        HashSet<ChunkCoord> forceLiveChunks,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true);
        Directory.CreateDirectory(stageDir);
        Directory.CreateDirectory(currentDir);
        manifest.Tiles ??= new Dictionary<string, WebMapTerrainTileMetadata>(StringComparer.Ordinal);

        var dirtyTiles = CollectDirtyTiles(config, coverage, currentDir, manifest, dirtySnapshot);
        var dirtyChunksByTile = CollectDirtyChunksByTile(config, dirtySnapshot);
        var staleKeys = CollectStaleTileKeys(coverage, manifest);
        bool metadataRepairNeeded =
            (config.ExportNormalTerrain && !File.Exists(Path.Combine(currentDir, "overview.png"))) ||
            !File.Exists(Path.Combine(currentDir, "manifest.json")) ||
            (config.ExportNormalTerrain && !Directory.Exists(Path.Combine(currentDir, "lod")));

        status.TotalTiles = dirtyTiles.Count;
        status.ReusedTiles = Math.Max(0, coverage.Tiles.Count - dirtyTiles.Count);
        status.Message = dirtyTiles.Count == 0
            ? $"Changed-terrain update: no changed/new tiles; reusing {status.ReusedTiles:n0} cached tiles."
            : $"Changed-terrain update: rendering {dirtyTiles.Count:n0} changed/new tiles and reusing {status.ReusedTiles:n0}.";
        publishStatus(status.Clone());
        api.Logger.Notification(
            "[ServerEssentials/WebMap] changed-terrain live update: rendering {0:n0} changed/new tiles, reusing {1:n0}; {2:n0} stale tiles pending removal.",
            dirtyTiles.Count, status.ReusedTiles, staleKeys.Count);

        progressPhaseStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ResetEtaSamples();
        status.Phase = dirtyTiles.Count == 0 ? "finalizing" : "rendering";
        status.ProgressPercent = dirtyTiles.Count == 0 ? 90 : 5;
        status.EstimatedRemainingMilliseconds = dirtyTiles.Count == 0 ? 0 : -1;
        status.AverageMillisecondsPerTile = 0;
        publishStatus(status.Clone());
        LogConsoleProgress("runtime-update", 0, dirtyTiles.Count, 0, status, force: true);

        if (dirtyTiles.Count == 0 && staleKeys.Count == 0 && !metadataRepairNeeded)
        {
            status.WrittenTiles = 0;
            status.ProcessedChunkColumns = 0;
            try { Directory.Delete(stageDir, true); } catch { }
            return true;
        }

        int written = 0;
        long processedColumns = 0;
        foreach (TileCoord tile in dirtyTiles.OrderBy(t => t.Z).ThenBy(t => t.X))
        {
            cancellationToken.ThrowIfCancellationRequested();
            dirtyChunksByTile.TryGetValue(tile, out HashSet<ChunkCoord>? patchChunks);
            RuntimeRenderedTile rendered = await RenderTileRuntimeAsync(
                config, tile, stageDir, coverage.Chunks, status, directReader, forceLiveChunks, cancellationToken,
                rawSourceDir: currentDir, patchChunks: patchChunks).ConfigureAwait(false);
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
            written++;
            status.WrittenTiles = written;
            status.ProcessedChunkColumns = processedColumns;
            UpdateEta(status, written, dirtyTiles.Count);
            if (written == dirtyTiles.Count || (written & 15) == 0)
            {
                status.Message = $"Changed-terrain update: {written:n0}/{dirtyTiles.Count:n0} changed/new tiles";
                publishStatus(status.Clone());
            }
            LogConsoleProgress("runtime-update", written, dirtyTiles.Count, processedColumns, status);
            await Task.Yield();
        }

        if (written > 0) ValidateAndLogSurfaceDiagnostics(coverage);
        SetProgressPhase(status, "overview", 92, "Updating map overview…");
        if (config.ExportNormalTerrain) WriteOverview(stageDir, manifest, status);
        SetProgressPhase(status, "pyramid", 96, "Updating zoom levels…");
        bool replaceWholeLod = false;
        HashSet<LodTileCoord> lodTiles = config.ExportNormalTerrain ? StageIncrementalLodTiles(stageDir, currentDir, manifest, dirtyTiles, staleKeys, status, config, out replaceWholeLod) : new HashSet<LodTileCoord>();
        bool replaceWholeSepiaLod = false;
        HashSet<LodTileCoord> sepiaLodTiles = config.ExportSepiaTerrain ? StageIncrementalLodTiles(StyleRoot(stageDir, "sepia"), StyleRoot(currentDir, "sepia"), manifest, dirtyTiles, staleKeys, status, config, out replaceWholeSepiaLod) : new HashSet<LodTileCoord>();
        bool replaceWholeTopoLod = false;
        HashSet<LodTileCoord> topoLodTiles = config.ExportTopographicTerrain ? StageIncrementalLodTiles(StyleRoot(stageDir, "topo"), StyleRoot(currentDir, "topo"), manifest, dirtyTiles, staleKeys, status, config, out replaceWholeTopoLod) : new HashSet<LodTileCoord>();
        SetProgressPhase(status, "finalizing", 99, "Publishing changed terrain…");
        FinalizeManifest(manifest, coverage, manifest.Tiles.Count, coverage.ChunkColumns);
        WriteManifest(stageDir, manifest);
        CommitIncrementalStage(stageDir, currentDir, dirtyTiles, staleKeys, lodTiles, replaceWholeLod, sepiaLodTiles, replaceWholeSepiaLod, topoLodTiles, replaceWholeTopoLod, config.ExportNormalTerrain, config.ExportSepiaTerrain, config.ExportTopographicTerrain);
        return true;
    }

    private async Task<RuntimeRenderedTile> RenderTileRuntimeAsync(
        WebMapConfig config,
        TileCoord tile,
        string outputDir,
        HashSet<ChunkCoord> generatedChunks,
        WebMapTerrainExportStatus status,
        WebMapSaveDbTerrainReader? directReader,
        HashSet<ChunkCoord> forceLiveChunks,
        CancellationToken cancellationToken,
        string? rawSourceDir = null,
        HashSet<ChunkCoord>? patchChunks = null)
    {
        int pixelBytes = checked(config.TerrainTileSize * config.TerrainTileSize * 4);
        byte[] rgba = ArrayPool<byte>.Shared.Rent(pixelBytes);
        try
        {
            bool patchMode = rawSourceDir != null && patchChunks != null && patchChunks.Count > 0 &&
                             TryReadRawTile(rawSourceDir, tile.X, tile.Z, rgba, pixelBytes);
            if (!patchMode) FillBackground(rgba, pixelBytes);
            int tileBlockX = tile.X * config.TerrainTileSize;
            int tileBlockZ = tile.Z * config.TerrainTileSize;
            int firstChunkX = FloorDiv(tileBlockX, ChunkSize);
            int firstChunkZ = FloorDiv(tileBlockZ, ChunkSize);
            int chunksWide = Math.Max(1, config.TerrainTileSize / ChunkSize);
            long processed = 0;

            for (int dz = 0; dz < chunksWide; dz++)
            {
                for (int dx = 0; dx < chunksWide; dx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int chunkX = firstChunkX + dx;
                    int chunkZ = firstChunkZ + dz;
                    var chunkCoord = new ChunkCoord(chunkX, chunkZ);
                    if (patchMode && !patchChunks!.Contains(chunkCoord)) continue;
                    if (patchMode) ClearChunkPatch(rgba, config.TerrainTileSize, dx * ChunkSize, dz * ChunkSize);
                    Interlocked.Increment(ref chunkExistenceChecks);
                    if (!generatedChunks.Contains(chunkCoord)) continue;
                    Interlocked.Increment(ref existingChunkColumns);
                    if (!forceLiveChunks.Contains(chunkCoord) && directReader != null && directReader.IsActive && TryRenderDatabaseColumn(
                            directReader, chunkX, chunkZ, rgba, config.TerrainTileSize,
                            dx * ChunkSize, dz * ChunkSize, status))
                    {
                        processed++;
                        LogConsoleTileHeartbeat(tile, processed, status);
                        continue;
                    }

                    var loadSw = Stopwatch.StartNew();
                    await LoadAndRenderRuntimeChunkAsync(
                        chunkX,
                        chunkZ,
                        rgba,
                        config.TerrainTileSize,
                        dx * ChunkSize,
                        dz * ChunkSize,
                        cancellationToken).ConfigureAwait(false);
                    loadSw.Stop();
                    status.WorldReadMilliseconds += loadSw.ElapsedMilliseconds;
                    processed++;
                    LogConsoleTileHeartbeat(tile, processed, status);
                }
            }

            WriteRawTile(outputDir, tile.X, tile.Z, rgba, pixelBytes);
            WriteDerivedTerrainStyles(outputDir, tile.X, tile.Z, rgba, config.TerrainTileSize, config.ExportSepiaTerrain, config.ExportTopographicTerrain);

            var shadeSw = Stopwatch.StartNew();
            ApplyHillshade(rgba, config.TerrainTileSize);
            shadeSw.Stop();
            status.RenderMilliseconds += shadeSw.ElapsedMilliseconds;
            RenderedTile summary = SummarizeTile(rgba, config.TerrainTileSize);

            if (config.ExportNormalTerrain)
            {
                string xDir = Path.Combine(outputDir, tile.X.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(xDir);
                string finalPath = Path.Combine(xDir, tile.Z.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png");
                var pngSw = Stopwatch.StartNew();
                PngWriter.WriteRgba(finalPath, config.TerrainTileSize, config.TerrainTileSize, rgba);
                pngSw.Stop();
                status.PngWriteMilliseconds += pngSw.ElapsedMilliseconds;
            }
            status.BlockColorCacheEntries = blockColorCache.Count;
            return new RuntimeRenderedTile(summary, processed);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rgba, clearArray: false);
        }
    }

    private bool TryRenderDatabaseColumn(
        WebMapSaveDbTerrainReader directReader,
        int chunkX,
        int chunkZ,
        byte[] rgba,
        int tileSize,
        int pixelOffsetX,
        int pixelOffsetZ,
        WebMapTerrainExportStatus status)
    {
        bool ok = TryRenderDatabaseColumnCore(
            directReader, chunkX, chunkZ, rgba, tileSize, pixelOffsetX, pixelOffsetZ,
            out long worldReadMilliseconds, out long renderMilliseconds);
        status.WorldReadMilliseconds += worldReadMilliseconds;
        status.RenderMilliseconds += renderMilliseconds;
        if (!ok && !directReader.IsActive) status.TerrainReadMode = "save-db-unavailable";
        return ok;
    }

    private bool TryRenderDatabaseColumnCore(
        WebMapSaveDbTerrainReader directReader,
        int chunkX,
        int chunkZ,
        byte[] rgba,
        int tileSize,
        int pixelOffsetX,
        int pixelOffsetZ,
        out long worldReadMilliseconds,
        out long renderMilliseconds)
    {
        worldReadMilliseconds = 0;
        renderMilliseconds = 0;
        int verticalChunks = Math.Max(1, (api.WorldManager.MapSizeY + ChunkSize - 1) / ChunkSize);
        IServerChunk[] chunks = ArrayPool<IServerChunk>.Shared.Rent(verticalChunks);
        Array.Clear(chunks, 0, chunks.Length);
        var loadSw = Stopwatch.StartNew();
        try
        {
            if (!directReader.TryLoadColumn(chunkX, chunkZ, api.WorldManager.MapSizeY, chunks, out ushort[]? rainHeights))
            {
                loadSw.Stop();
                worldReadMilliseconds += loadSw.ElapsedMilliseconds;
                return false;
            }

            loadSw.Stop();
            worldReadMilliseconds += loadSw.ElapsedMilliseconds;
            Interlocked.Increment(ref loadedChunkColumns);
            var renderSw = Stopwatch.StartNew();
            RenderChunkColumn(chunkX, chunkZ, chunks, null, rgba, tileSize, pixelOffsetX, pixelOffsetZ, rainHeights);
            renderSw.Stop();
            renderMilliseconds += renderSw.ElapsedMilliseconds;
            return true;
        }
        finally
        {
            if (loadSw.IsRunning)
            {
                loadSw.Stop();
                worldReadMilliseconds += loadSw.ElapsedMilliseconds;
            }
            for (int i = 0; i < verticalChunks && i < chunks.Length; i++)
            {
                try { chunks[i]?.Dispose(); } catch { }
                chunks[i] = null!;
            }
            ArrayPool<IServerChunk>.Shared.Return(chunks, clearArray: true);
        }
    }

    private Task LoadAndRenderRuntimeChunkAsync(
        int chunkX,
        int chunkZ,
        byte[] rgba,
        int tileSize,
        int pixelOffsetX,
        int pixelOffsetZ,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        api.Event.EnqueueMainThreadTask(() =>
        {
            try
            {
                void RenderLoadedColumn()
                {
                    IServerChunk[]? chunks = null;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int verticalChunks = Math.Max(1, (api.WorldManager.MapSizeY + ChunkSize - 1) / ChunkSize);
                        chunks = ArrayPool<IServerChunk>.Shared.Rent(verticalChunks);
                        Array.Clear(chunks, 0, chunks.Length);
                        bool anyChunk = false;
                        for (int cy = 0; cy < verticalChunks; cy++)
                        {
                            chunks[cy] = api.WorldManager.GetChunk(chunkX, cy, chunkZ);
                            if (chunks[cy] != null) anyChunk = true;
                        }
                        if (anyChunk) loadedChunkColumns++;

                        IMapChunk? mapChunk = null;
                        try { mapChunk = api.WorldManager.GetMapChunk(chunkX, chunkZ); } catch { }
                        if (mapChunk == null)
                        {
                            try { mapChunk = api.World.BlockAccessor.GetMapChunk(chunkX, chunkZ); } catch { }
                        }
                        if (mapChunk != null) mapChunksResolved++;

                        RenderChunkColumn(chunkX, chunkZ, chunks, mapChunk, rgba, tileSize, pixelOffsetX, pixelOffsetZ);
                        tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                    finally
                    {
                        if (chunks != null) ArrayPool<IServerChunk>.Shared.Return(chunks, clearArray: true);
                    }
                }

                // If the column is already resident, do not ask the loader to touch it again.
                if (api.WorldManager.GetChunk(chunkX, 0, chunkZ) != null)
                {
                    RenderLoadedColumn();
                    return;
                }

                api.WorldManager.LoadChunkColumnPriority(
                    chunkX,
                    chunkZ,
                    new ChunkLoadOptions
                    {
                        KeepLoaded = false,
                        OnLoaded = () => api.Event.EnqueueMainThreadTask(RenderLoadedColumn, "nses-webmap-force-render-column")
                    });
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, "nses-webmap-force-load-column");

        return tcs.Task;
    }

    private void FullExport(
        WebMapConfig config,
        TerrainCoverage coverage,
        string stageDir,
        string currentDir,
        string backupDir,
        WebMapTerrainExportStatus status,
        WebMapSaveDbTerrainReader? directReader,
        string savePath)
    {
        if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true);
        Directory.CreateDirectory(stageDir);
        status.Message = coverage.Tiles.Count == 0
            ? "No generated overworld map chunks were found."
            : $"Full terrain seed: rendering {coverage.Tiles.Count:n0} cached tiles from {coverage.ChunkColumns:n0} generated chunk columns…";
        publishStatus(status.Clone());
        api.Logger.Notification(
            "[ServerEssentials/WebMap] full terrain export: rendering {0:n0} tiles from {1:n0} generated chunk columns.",
            coverage.Tiles.Count, coverage.ChunkColumns);
        progressPhaseStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ResetEtaSamples();
        status.Phase = "rendering";
        status.ProgressPercent = 5;
        status.EstimatedRemainingMilliseconds = -1;
        status.AverageMillisecondsPerTile = 0;
        publishStatus(status.Clone());
        LogConsoleProgress("full", 0, coverage.Tiles.Count, 0, status, force: true);

        var manifest = CreateManifest(config, coverage);
        int written = 0;
        long processedColumns = 0;
        int dbWorkers = Math.Clamp(config.TerrainBaseRenderWorkers, 1, 4);
        bool parallelDb = directReader != null && directReader.IsActive && dbWorkers > 1 && coverage.Tiles.Count > 1;

        if (parallelDb)
        {
            var workerReaders = new List<WebMapSaveDbTerrainReader>();
            try
            {
                for (int i = 0; i < dbWorkers; i++)
                {
                    WebMapSaveDbTerrainReader? workerReader = WebMapSaveDbTerrainReader.TryOpen(api, savePath, out string reason);
                    if (workerReader == null)
                    {
                        api.Logger.Warning("[ServerEssentials/WebMap] could not open save-DB terrain worker {0}: {1}", i + 1, reason);
                        break;
                    }
                    workerReaders.Add(workerReader);
                }

                if (workerReaders.Count >= 2)
                {
                    status.TerrainReadMode = $"save-db-parallel-{workerReaders.Count}";
                    api.Logger.Notification("[ServerEssentials/WebMap] full terrain export using {0} parallel read-only save-DB terrain workers.", workerReaders.Count);
                    var readerPool = new ConcurrentQueue<WebMapSaveDbTerrainReader>(workerReaders);
                    var failedTiles = new ConcurrentBag<TileCoord>();
                    object progressGate = new();
                    var options = new ParallelOptions { MaxDegreeOfParallelism = workerReaders.Count };

                    Parallel.ForEach(coverage.Tiles.OrderBy(t => t.Z).ThenBy(t => t.X), options, tile =>
                    {
                        if (!readerPool.TryDequeue(out WebMapSaveDbTerrainReader? reader))
                        {
                            failedTiles.Add(tile);
                            return;
                        }
                        try
                        {
                            if (!TryRenderTileDatabaseOnly(config, tile, stageDir, coverage.Chunks, reader, out DatabaseRenderedTile rendered))
                            {
                                failedTiles.Add(tile);
                                return;
                            }

                            lock (progressGate)
                            {
                                manifest.Tiles[TileKey(tile.X, tile.Z)] = new WebMapTerrainTileMetadata
                                {
                                    X = tile.X, Z = tile.Z, R = rendered.Tile.Average.R, G = rendered.Tile.Average.G, B = rendered.Tile.Average.B,
                                    ChunkRgb = rendered.Tile.ChunkRgb,
                                    GeneratedChunkCount = CountGeneratedChunksForTile(config, tile, coverage.Chunks),
                                    LastGeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                };
                                written++;
                                processedColumns += rendered.ProcessedColumns;
                                status.WorldReadMilliseconds += rendered.WorldReadMilliseconds;
                                status.RenderMilliseconds += rendered.RenderMilliseconds;
                                status.PngWriteMilliseconds += rendered.PngWriteMilliseconds;
                                status.WrittenTiles = written;
                                status.ProcessedChunkColumns = processedColumns;
                                UpdateEta(status, written, coverage.Tiles.Count);
                                if (written == coverage.Tiles.Count || (written & 15) == 0)
                                {
                                    status.Message = $"Full terrain seed: {written:n0}/{coverage.Tiles.Count:n0} tiles";
                                    publishStatus(status.Clone());
                                }
                                LogConsoleProgress("full-db-parallel", written, coverage.Tiles.Count, processedColumns, status);
                            }
                        }
                        finally
                        {
                            readerPool.Enqueue(reader);
                        }
                    });

                    TileCoord[] fallbackTiles = failedTiles.Distinct().OrderBy(t => t.Z).ThenBy(t => t.X).ToArray();
                    if (fallbackTiles.Length > 0)
                    {
                        status.TerrainReadMode = "save-db-parallel+serial-retry";
                        api.Logger.Warning("[ServerEssentials/WebMap] {0:n0} terrain tiles missed the parallel DB fast path and will be retried through the serial save-DB path.", fallbackTiles.Length);
                        foreach (TileCoord tile in fallbackTiles)
                        {
                            RenderedTile result = RenderTile(config, tile, stageDir, coverage.Chunks, ref processedColumns, status, directReader);
                            manifest.Tiles[TileKey(tile.X, tile.Z)] = new WebMapTerrainTileMetadata
                            {
                                X = tile.X, Z = tile.Z, R = result.Average.R, G = result.Average.G, B = result.Average.B,
                                ChunkRgb = result.ChunkRgb,
                                GeneratedChunkCount = CountGeneratedChunksForTile(config, tile, coverage.Chunks),
                                LastGeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            };
                            written++;
                            status.WrittenTiles = written;
                            status.ProcessedChunkColumns = processedColumns;
                            UpdateEta(status, written, coverage.Tiles.Count);
                            LogConsoleProgress("full-fallback", written, coverage.Tiles.Count, processedColumns, status);
                        }
                    }
                }
                else
                {
                    parallelDb = false;
                }
            }
            finally
            {
                foreach (WebMapSaveDbTerrainReader reader in workerReaders) reader.Dispose();
            }
        }

        if (!parallelDb)
        {
            foreach (var tile in coverage.Tiles.OrderBy(t => t.Z).ThenBy(t => t.X))
            {
                RenderedTile result = RenderTile(config, tile, stageDir, coverage.Chunks, ref processedColumns, status, directReader);
                manifest.Tiles[TileKey(tile.X, tile.Z)] = new WebMapTerrainTileMetadata
                {
                    X = tile.X, Z = tile.Z, R = result.Average.R, G = result.Average.G, B = result.Average.B,
                    ChunkRgb = result.ChunkRgb,
                    GeneratedChunkCount = CountGeneratedChunksForTile(config, tile, coverage.Chunks),
                    LastGeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                written++;
                status.WrittenTiles = written;
                status.ProcessedChunkColumns = processedColumns;
                UpdateEta(status, written, coverage.Tiles.Count);
                if (written == coverage.Tiles.Count || (written & 15) == 0)
                {
                    status.Message = $"Full terrain seed: {written:n0}/{coverage.Tiles.Count:n0} tiles";
                    publishStatus(status.Clone());
                }
                LogConsoleProgress("full", written, coverage.Tiles.Count, processedColumns, status);
            }
        }

        ValidateAndLogSurfaceDiagnostics(coverage);
        SetProgressPhase(status, "overview", 92, "Building map overview…");
        if (config.ExportNormalTerrain) WriteOverview(stageDir, manifest, status);
        SetProgressPhase(status, "pyramid", 96, "Building zoom levels…");
        if (config.ExportNormalTerrain) WriteLodPyramid(stageDir, manifest, status, config);
        WriteDerivedStyleLodPyramids(stageDir, manifest, status, config);
        SetProgressPhase(status, "finalizing", 99, "Publishing terrain cache…");
        FinalizeManifest(manifest, coverage, written, processedColumns);
        WriteManifest(stageDir, manifest);
        AtomicSwap(stageDir, currentDir, backupDir);
        status.ReusedTiles = 0;
    }

    private void IncrementalExport(
        WebMapConfig config,
        TerrainCoverage coverage,
        string stageDir,
        string currentDir,
        WebMapTerrainManifest manifest,
        HashSet<WebMapDirtyChunkCoord> dirtySnapshot,
        WebMapTerrainExportStatus status,
        WebMapSaveDbTerrainReader? directReader)
    {
        // Incremental work is staged first. The public cache is not touched until the newly
        // rendered tiles pass surface validation and the replacement manifest/overview exist.
        if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true);
        Directory.CreateDirectory(stageDir);
        Directory.CreateDirectory(currentDir);
        manifest.Tiles ??= new Dictionary<string, WebMapTerrainTileMetadata>(StringComparer.Ordinal);

        var dirtyTiles = CollectDirtyTiles(config, coverage, currentDir, manifest, dirtySnapshot);
        var dirtyChunksByTile = CollectDirtyChunksByTile(config, dirtySnapshot);
        var staleKeys = CollectStaleTileKeys(coverage, manifest);
        bool metadataRepairNeeded = !File.Exists(Path.Combine(currentDir, "overview.png")) ||
                                    !File.Exists(Path.Combine(currentDir, "manifest.json")) ||
                                    !Directory.Exists(Path.Combine(currentDir, "lod"));
        status.TotalTiles = dirtyTiles.Count;
        status.ReusedTiles = Math.Max(0, coverage.Tiles.Count - dirtyTiles.Count);
        status.Message = dirtyTiles.Count == 0
            ? $"Incremental terrain: no changed/new tiles; reusing {status.ReusedTiles:n0} cached tiles."
            : $"Incremental terrain: staging {dirtyTiles.Count:n0} changed/new tiles and reusing {status.ReusedTiles:n0}.";
        publishStatus(status.Clone());
        api.Logger.Notification(
            "[ServerEssentials/WebMap] incremental terrain export: staging {0:n0} changed/new tiles, reusing {1:n0}; {2:n0} stale tiles pending removal after validation.",
            dirtyTiles.Count, status.ReusedTiles, staleKeys.Count);
        progressPhaseStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ResetEtaSamples();
        status.Phase = dirtyTiles.Count == 0 ? "finalizing" : "rendering";
        status.ProgressPercent = dirtyTiles.Count == 0 ? 90 : 5;
        status.EstimatedRemainingMilliseconds = dirtyTiles.Count == 0 ? 0 : -1;
        status.AverageMillisecondsPerTile = 0;
        publishStatus(status.Clone());
        LogConsoleProgress("incremental", 0, dirtyTiles.Count, 0, status, force: true);

        if (dirtyTiles.Count == 0 && staleKeys.Count == 0 && !metadataRepairNeeded)
        {
            status.WrittenTiles = 0;
            status.ProcessedChunkColumns = 0;
            try { Directory.Delete(stageDir, true); } catch { }
            return;
        }

        int written = 0;
        long processedColumns = 0;
        foreach (var tile in dirtyTiles.OrderBy(t => t.Z).ThenBy(t => t.X))
        {
            dirtyChunksByTile.TryGetValue(tile, out HashSet<ChunkCoord>? patchChunks);
            RenderedTile result = RenderTile(
                config, tile, stageDir, coverage.Chunks, ref processedColumns, status, directReader,
                rawSourceDir: currentDir, patchChunks: patchChunks);
            manifest.Tiles[TileKey(tile.X, tile.Z)] = new WebMapTerrainTileMetadata
            {
                X = tile.X, Z = tile.Z, R = result.Average.R, G = result.Average.G, B = result.Average.B,
                ChunkRgb = result.ChunkRgb,
                GeneratedChunkCount = CountGeneratedChunksForTile(config, tile, coverage.Chunks),
                LastGeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            written++;
            status.WrittenTiles = written;
            status.ProcessedChunkColumns = processedColumns;
            UpdateEta(status, written, dirtyTiles.Count);
            if (written == dirtyTiles.Count || (written & 15) == 0)
            {
                status.Message = $"Incremental terrain: {written:n0}/{dirtyTiles.Count:n0} changed/new tiles staged";
                publishStatus(status.Clone());
            }
            LogConsoleProgress("incremental", written, dirtyTiles.Count, processedColumns, status);
        }

        if (written > 0) ValidateAndLogSurfaceDiagnostics(coverage);
        SetProgressPhase(status, "overview", 92, "Updating map overview…");
        if (config.ExportNormalTerrain) WriteOverview(stageDir, manifest, status);
        SetProgressPhase(status, "pyramid", 96, "Updating zoom levels…");
        bool replaceWholeLod = false;
        HashSet<LodTileCoord> lodTiles = config.ExportNormalTerrain ? StageIncrementalLodTiles(stageDir, currentDir, manifest, dirtyTiles, staleKeys, status, config, out replaceWholeLod) : new HashSet<LodTileCoord>();
        bool replaceWholeSepiaLod = false;
        HashSet<LodTileCoord> sepiaLodTiles = config.ExportSepiaTerrain ? StageIncrementalLodTiles(StyleRoot(stageDir, "sepia"), StyleRoot(currentDir, "sepia"), manifest, dirtyTiles, staleKeys, status, config, out replaceWholeSepiaLod) : new HashSet<LodTileCoord>();
        bool replaceWholeTopoLod = false;
        HashSet<LodTileCoord> topoLodTiles = config.ExportTopographicTerrain ? StageIncrementalLodTiles(StyleRoot(stageDir, "topo"), StyleRoot(currentDir, "topo"), manifest, dirtyTiles, staleKeys, status, config, out replaceWholeTopoLod) : new HashSet<LodTileCoord>();
        SetProgressPhase(status, "finalizing", 99, "Publishing changed terrain…");
        FinalizeManifest(manifest, coverage, manifest.Tiles.Count, coverage.ChunkColumns);
        WriteManifest(stageDir, manifest);
        CommitIncrementalStage(stageDir, currentDir, dirtyTiles, staleKeys, lodTiles, replaceWholeLod, sepiaLodTiles, replaceWholeSepiaLod, topoLodTiles, replaceWholeTopoLod, config.ExportNormalTerrain, config.ExportSepiaTerrain, config.ExportTopographicTerrain);
    }

    private static Dictionary<TileCoord, HashSet<ChunkCoord>> CollectDirtyChunksByTile(
        WebMapConfig config,
        HashSet<WebMapDirtyChunkCoord> dirtySnapshot)
    {
        var result = new Dictionary<TileCoord, HashSet<ChunkCoord>>();
        foreach (WebMapDirtyChunkCoord dirty in dirtySnapshot)
        {
            if (dirty.Dimension != 0) continue;
            int worldX = dirty.ChunkX * ChunkSize;
            int worldZ = dirty.ChunkZ * ChunkSize;
            var tile = new TileCoord(FloorDiv(worldX, config.TerrainTileSize), FloorDiv(worldZ, config.TerrainTileSize));
            if (!result.TryGetValue(tile, out HashSet<ChunkCoord>? chunks))
            {
                chunks = new HashSet<ChunkCoord>();
                result[tile] = chunks;
            }
            chunks.Add(new ChunkCoord(dirty.ChunkX, dirty.ChunkZ));
        }
        return result;
    }

    private static HashSet<TileCoord> CollectDirtyTiles(
        WebMapConfig config,
        TerrainCoverage coverage,
        string currentDir,
        WebMapTerrainManifest manifest,
        HashSet<WebMapDirtyChunkCoord> dirtySnapshot)
    {
        var dirtyTiles = new HashSet<TileCoord>();
        foreach (var dirty in dirtySnapshot)
        {
            if (dirty.Dimension != 0) continue;
            int worldX = dirty.ChunkX * ChunkSize;
            int worldZ = dirty.ChunkZ * ChunkSize;
            dirtyTiles.Add(new TileCoord(FloorDiv(worldX, config.TerrainTileSize), FloorDiv(worldZ, config.TerrainTileSize)));
        }

        foreach (var tile in coverage.Tiles)
        {
            // Manifest metadata alone is not enough: a manually deleted/corruptly published PNG/raw
            // cache must be regenerated instead of being counted as reusable. Also detect newly
            // generated chunk columns inside an already-existing 256px tile.
            string key = TileKey(tile.X, tile.Z);
            int generatedCount = CountGeneratedChunksForTile(config, tile, coverage.Chunks);
            if (!manifest.Tiles.TryGetValue(key, out WebMapTerrainTileMetadata? meta) ||
                meta.GeneratedChunkCount != generatedCount ||
                (config.ExportNormalTerrain && !File.Exists(TilePath(currentDir, tile.X, tile.Z))) ||
                !File.Exists(RawTilePath(currentDir, tile.X, tile.Z)) ||
                (config.ExportSepiaTerrain && !File.Exists(StyleTilePath(currentDir, "sepia", tile.X, tile.Z))) ||
                (config.ExportTopographicTerrain && !File.Exists(StyleTilePath(currentDir, "topo", tile.X, tile.Z))))
                dirtyTiles.Add(tile);
        }

        dirtyTiles.IntersectWith(coverage.Tiles);
        return dirtyTiles;
    }

    private static int CountGeneratedChunksForTile(WebMapConfig config, TileCoord tile, HashSet<ChunkCoord> generatedChunks)
    {
        int tileBlockX = tile.X * config.TerrainTileSize;
        int tileBlockZ = tile.Z * config.TerrainTileSize;
        int firstChunkX = FloorDiv(tileBlockX, ChunkSize);
        int firstChunkZ = FloorDiv(tileBlockZ, ChunkSize);
        int chunksWide = Math.Max(1, config.TerrainTileSize / ChunkSize);
        int count = 0;
        for (int dz = 0; dz < chunksWide; dz++)
            for (int dx = 0; dx < chunksWide; dx++)
                if (generatedChunks.Contains(new ChunkCoord(firstChunkX + dx, firstChunkZ + dz))) count++;
        return count;
    }

    private static List<string> CollectStaleTileKeys(TerrainCoverage coverage, WebMapTerrainManifest manifest)
    {
        var coverageKeys = new HashSet<string>(coverage.Tiles.Count, StringComparer.Ordinal);
        foreach (var tile in coverage.Tiles) coverageKeys.Add(TileKey(tile.X, tile.Z));
        var staleKeys = new List<string>();
        foreach (string key in manifest.Tiles.Keys)
            if (!coverageKeys.Contains(key)) staleKeys.Add(key);
        foreach (string stale in staleKeys) manifest.Tiles.Remove(stale);
        return staleKeys;
    }

    private static void CommitIncrementalStage(
        string stageDir,
        string currentDir,
        HashSet<TileCoord> dirtyTiles,
        List<string> staleKeys,
        HashSet<LodTileCoord> lodTiles,
        bool replaceWholeLod,
        HashSet<LodTileCoord> sepiaLodTiles,
        bool replaceWholeSepiaLod,
        HashSet<LodTileCoord> topoLodTiles,
        bool replaceWholeTopoLod,
        bool exportNormal,
        bool exportSepia,
        bool exportTopo)
    {
        string rollbackDir = stageDir + "-rollback";
        if (Directory.Exists(rollbackDir)) Directory.Delete(rollbackDir, true);
        Directory.CreateDirectory(rollbackDir);
        Directory.CreateDirectory(currentDir);

        var touched = new List<(string Target, string? Backup)>();
        void BackupTarget(string target)
        {
            if (touched.Any(x => string.Equals(x.Target, target, StringComparison.OrdinalIgnoreCase))) return;
            if (!File.Exists(target))
            {
                touched.Add((target, null));
                return;
            }
            string relative = Path.GetRelativePath(currentDir, target);
            string backup = Path.Combine(rollbackDir, relative);
            string? parent = Path.GetDirectoryName(backup);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(target, backup, true);
            touched.Add((target, backup));
        }

        string? lodBackup = null;
        var styleLodBackups = new List<(string Current, string Backup)>();

        void CommitDerivedStyleLod(string style, HashSet<LodTileCoord> affectedTiles, bool replaceWhole)
        {
            string stagedRoot = StyleRoot(stageDir, style);
            string currentRoot = StyleRoot(currentDir, style);
            string stagedLod = Path.Combine(stagedRoot, "lod");
            string currentLod = Path.Combine(currentRoot, "lod");
            if (replaceWhole)
            {
                if (!Directory.Exists(stagedLod)) throw new DirectoryNotFoundException($"Staged {style} LOD terrain directory is missing.");
                if (Directory.Exists(currentLod))
                {
                    string backup = Path.Combine(rollbackDir, "styles", style, "lod");
                    string? parent = Path.GetDirectoryName(backup);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    if (Directory.Exists(backup)) Directory.Delete(backup, true);
                    Directory.Move(currentLod, backup);
                    styleLodBackups.Add((currentLod, backup));
                }
                Directory.CreateDirectory(currentRoot);
                Directory.Move(stagedLod, currentLod);
                return;
            }

            foreach (LodTileCoord lod in affectedTiles)
            {
                string source = LodTilePath(stagedRoot, lod.Factor, lod.X, lod.Z);
                string target = LodTilePath(currentRoot, lod.Factor, lod.X, lod.Z);
                BackupTarget(target);
                if (File.Exists(source))
                {
                    string? parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    File.Move(source, target, true);
                }
                else if (File.Exists(target)) File.Delete(target);
            }
        }

        try
        {
            foreach (TileCoord tile in dirtyTiles)
            {
                if (exportNormal)
                {
                    string source = TilePath(stageDir, tile.X, tile.Z);
                    if (!File.Exists(source)) throw new FileNotFoundException("Staged incremental terrain tile is missing.", source);
                    string target = TilePath(currentDir, tile.X, tile.Z);
                    BackupTarget(target);
                    string? parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    File.Move(source, target, true);
                }

                string rawSource = RawTilePath(stageDir, tile.X, tile.Z);
                string rawTarget = RawTilePath(currentDir, tile.X, tile.Z);
                if (!File.Exists(rawSource)) throw new FileNotFoundException("Staged incremental raw terrain tile is missing.", rawSource);
                BackupTarget(rawTarget);
                string? rawParent = Path.GetDirectoryName(rawTarget);
                if (!string.IsNullOrEmpty(rawParent)) Directory.CreateDirectory(rawParent);
                File.Move(rawSource, rawTarget, true);

                foreach (string style in DerivedTerrainStyles)
                {
                    if ((style == "sepia" && !exportSepia) || (style == "topo" && !exportTopo)) continue;
                    string styleSource = StyleTilePath(stageDir, style, tile.X, tile.Z);
                    if (!File.Exists(styleSource)) throw new FileNotFoundException($"Staged incremental {style} terrain tile is missing.", styleSource);
                    string styleTarget = StyleTilePath(currentDir, style, tile.X, tile.Z);
                    BackupTarget(styleTarget);
                    string? styleParent = Path.GetDirectoryName(styleTarget);
                    if (!string.IsNullOrEmpty(styleParent)) Directory.CreateDirectory(styleParent);
                    File.Move(styleSource, styleTarget, true);
                }
            }

            foreach (string stale in staleKeys)
            {
                if (!TryParseTileKey(stale, out int sx, out int sz)) continue;
                string target = TilePath(currentDir, sx, sz);
                BackupTarget(target);
                if (File.Exists(target)) File.Delete(target);
                string rawTarget = RawTilePath(currentDir, sx, sz);
                BackupTarget(rawTarget);
                if (File.Exists(rawTarget)) File.Delete(rawTarget);
                foreach (string style in DerivedTerrainStyles)
                {
                    string styleTarget = StyleTilePath(currentDir, style, sx, sz);
                    BackupTarget(styleTarget);
                    if (File.Exists(styleTarget)) File.Delete(styleTarget);
                }
            }

            if (exportNormal)
            {
                string stagedOverview = Path.Combine(stageDir, "overview.png");
                if (!File.Exists(stagedOverview)) throw new FileNotFoundException("Staged incremental terrain overview is missing.", stagedOverview);
                string currentOverview = Path.Combine(currentDir, "overview.png");
                BackupTarget(currentOverview);
                File.Move(stagedOverview, currentOverview, true);
            }

            string stagedLod = Path.Combine(stageDir, "lod");
            string currentLod = Path.Combine(currentDir, "lod");
            if (exportNormal && replaceWholeLod)
            {
                if (!Directory.Exists(stagedLod)) throw new DirectoryNotFoundException("Staged LOD terrain directory is missing.");
                if (Directory.Exists(currentLod))
                {
                    lodBackup = Path.Combine(rollbackDir, "lod");
                    if (Directory.Exists(lodBackup)) Directory.Delete(lodBackup, true);
                    Directory.Move(currentLod, lodBackup);
                }
                Directory.Move(stagedLod, currentLod);
            }
            else if (exportNormal)
            {
                foreach (LodTileCoord lod in lodTiles)
                {
                    string source = LodTilePath(stageDir, lod.Factor, lod.X, lod.Z);
                    string target = LodTilePath(currentDir, lod.Factor, lod.X, lod.Z);
                    BackupTarget(target);
                    if (File.Exists(source))
                    {
                        string? parent = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        File.Move(source, target, true);
                    }
                    else if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
            }

            if (exportSepia) CommitDerivedStyleLod("sepia", sepiaLodTiles, replaceWholeSepiaLod);
            if (exportTopo) CommitDerivedStyleLod("topo", topoLodTiles, replaceWholeTopoLod);

            // Publish the manifest last so browsers never observe a new generation id before
            // its overview/LOD cache has been committed.
            string stagedManifest = Path.Combine(stageDir, "manifest.json");
            if (!File.Exists(stagedManifest)) throw new FileNotFoundException("Staged incremental terrain manifest is missing.", stagedManifest);
            string currentManifest = Path.Combine(currentDir, "manifest.json");
            BackupTarget(currentManifest);
            File.Move(stagedManifest, currentManifest, true);
        }
        catch
        {
            for (int i = touched.Count - 1; i >= 0; i--)
            {
                var entry = touched[i];
                try
                {
                    if (entry.Backup == null)
                    {
                        if (File.Exists(entry.Target)) File.Delete(entry.Target);
                    }
                    else
                    {
                        string? parent = Path.GetDirectoryName(entry.Target);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        File.Copy(entry.Backup, entry.Target, true);
                    }
                }
                catch { }
            }
            try
            {
                if (replaceWholeLod)
                {
                    string currentLod = Path.Combine(currentDir, "lod");
                    if (Directory.Exists(currentLod)) Directory.Delete(currentLod, true);
                    if (!string.IsNullOrEmpty(lodBackup) && Directory.Exists(lodBackup))
                        Directory.Move(lodBackup, currentLod);
                }
                for (int i = styleLodBackups.Count - 1; i >= 0; i--)
                {
                    var entry = styleLodBackups[i];
                    if (Directory.Exists(entry.Current)) Directory.Delete(entry.Current, true);
                    if (Directory.Exists(entry.Backup)) Directory.Move(entry.Backup, entry.Current);
                }
            }
            catch { }
            throw;
        }
        finally
        {
            try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
            try { if (Directory.Exists(rollbackDir)) Directory.Delete(rollbackDir, true); } catch { }
        }
    }

    private bool TryRenderTileDatabaseOnly(
        WebMapConfig config,
        TileCoord tile,
        string outputDir,
        HashSet<ChunkCoord> generatedChunks,
        WebMapSaveDbTerrainReader directReader,
        out DatabaseRenderedTile rendered)
    {
        rendered = default;
        int pixelBytes = checked(config.TerrainTileSize * config.TerrainTileSize * 4);
        byte[] rgba = ArrayPool<byte>.Shared.Rent(pixelBytes);
        try
        {
            FillBackground(rgba, pixelBytes);
            int tileBlockX = tile.X * config.TerrainTileSize;
            int tileBlockZ = tile.Z * config.TerrainTileSize;
            int firstChunkX = FloorDiv(tileBlockX, ChunkSize);
            int firstChunkZ = FloorDiv(tileBlockZ, ChunkSize);
            int chunksWide = Math.Max(1, config.TerrainTileSize / ChunkSize);
            long processed = 0;
            long readMs = 0;
            long renderMs = 0;

            for (int dz = 0; dz < chunksWide; dz++)
            {
                for (int dx = 0; dx < chunksWide; dx++)
                {
                    int chunkX = firstChunkX + dx;
                    int chunkZ = firstChunkZ + dz;
                    var coord = new ChunkCoord(chunkX, chunkZ);
                    if (!generatedChunks.Contains(coord)) continue;
                    Interlocked.Increment(ref existingChunkColumns);
                    if (!TryRenderDatabaseColumnCore(
                            directReader, chunkX, chunkZ, rgba, config.TerrainTileSize,
                            dx * ChunkSize, dz * ChunkSize,
                            out long columnReadMs, out long columnRenderMs))
                    {
                        return false;
                    }
                    readMs += columnReadMs;
                    renderMs += columnRenderMs;
                    processed++;
                }
            }

            WriteRawTile(outputDir, tile.X, tile.Z, rgba, pixelBytes);
            WriteDerivedTerrainStyles(outputDir, tile.X, tile.Z, rgba, config.TerrainTileSize, config.ExportSepiaTerrain, config.ExportTopographicTerrain);
            var shadeSw = Stopwatch.StartNew();
            ApplyHillshade(rgba, config.TerrainTileSize);
            shadeSw.Stop();
            renderMs += shadeSw.ElapsedMilliseconds;
            RenderedTile summary = SummarizeTile(rgba, config.TerrainTileSize);

            long pngWriteMs = 0;
            if (config.ExportNormalTerrain)
            {
                string xDir = Path.Combine(outputDir, tile.X.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(xDir);
                string finalPath = Path.Combine(xDir, tile.Z.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png");
                var pngSw = Stopwatch.StartNew();
                PngWriter.WriteRgba(finalPath, config.TerrainTileSize, config.TerrainTileSize, rgba);
                pngSw.Stop();
                pngWriteMs = pngSw.ElapsedMilliseconds;
            }
            rendered = new DatabaseRenderedTile(summary, processed, readMs, renderMs, pngWriteMs);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rgba, clearArray: false);
        }
    }

    private RenderedTile RenderTile(
        WebMapConfig config,
        TileCoord tile,
        string outputDir,
        HashSet<ChunkCoord> generatedChunks,
        ref long processedColumns,
        WebMapTerrainExportStatus status,
        WebMapSaveDbTerrainReader? directReader,
        bool atomicReplace = false,
        string? rawSourceDir = null,
        HashSet<ChunkCoord>? patchChunks = null)
    {
        int pixelBytes = checked(config.TerrainTileSize * config.TerrainTileSize * 4);
        byte[] rgba = ArrayPool<byte>.Shared.Rent(pixelBytes);
        try
        {
            bool patchMode = rawSourceDir != null && patchChunks != null && patchChunks.Count > 0 &&
                             TryReadRawTile(rawSourceDir, tile.X, tile.Z, rgba, pixelBytes);
            if (!patchMode) FillBackground(rgba, pixelBytes);
            int tileBlockX = tile.X * config.TerrainTileSize;
            int tileBlockZ = tile.Z * config.TerrainTileSize;
            int firstChunkX = FloorDiv(tileBlockX, ChunkSize);
            int firstChunkZ = FloorDiv(tileBlockZ, ChunkSize);
            int chunksWide = Math.Max(1, config.TerrainTileSize / ChunkSize);

            for (int dz = 0; dz < chunksWide; dz++)
            {
                for (int dx = 0; dx < chunksWide; dx++)
                {
                    int chunkX = firstChunkX + dx;
                    int chunkZ = firstChunkZ + dz;
                    var chunkCoord = new ChunkCoord(chunkX, chunkZ);
                    if (patchMode && !patchChunks!.Contains(chunkCoord)) continue;
                    if (patchMode) ClearChunkPatch(rgba, config.TerrainTileSize, dx * ChunkSize, dz * ChunkSize);
                    // The VCDBS coverage set was already enumerated from the save at the start
                    // of this export. Avoid a second blocking database existence check for every
                    // chunk column in every tile; a HashSet lookup is enough here.
                    if (!generatedChunks.Contains(chunkCoord)) continue;
                    Interlocked.Increment(ref existingChunkColumns);

                    if (directReader != null && directReader.IsActive && TryRenderDatabaseColumn(
                            directReader, chunkX, chunkZ, rgba, config.TerrainTileSize,
                            dx * ChunkSize, dz * ChunkSize, status))
                    {
                        processedColumns++;
                        LogConsoleTileHeartbeat(tile, processedColumns, status);
                        continue;
                    }

                    if (config.UseDirectSaveDatabaseTerrain)
                        throw new InvalidDataException($"Safe terrain read failed at {chunkX},{chunkZ}: {directReader?.LastFailure ?? "reader unavailable"}. The exporter did not load this column into the world.");

                    IServerChunk[]? chunks = null;
                    var loadSw = Stopwatch.StartNew();
                    try
                    {
                        status.WorldLoaderChunkColumns++;
                        chunks = api.WorldManager.BlockingLoadChunkColumn(chunkX, chunkZ);
                        loadSw.Stop();
                        status.WorldReadMilliseconds += loadSw.ElapsedMilliseconds;
                        if (chunks == null || chunks.Length == 0) continue;
                        loadedChunkColumns++;

                        // BlockingLoadChunkColumn reliably supplies the vertical chunk data, but on
                        // some server/runtime combinations the IWorldChunk.MapChunk back-reference is
                        // not populated on the returned array. Resolve the map chunk explicitly after
                        // the column is loaded so RainHeightMap sampling does not silently become 0/0.
                        IMapChunk? mapChunk = null;
                        for (int cy = chunks.Length - 1; cy >= 0; cy--)
                        {
                            if (chunks[cy]?.MapChunk == null) continue;
                            mapChunk = chunks[cy].MapChunk;
                            break;
                        }
                        if (mapChunk == null)
                        {
                            try { mapChunk = api.WorldManager.GetMapChunk(chunkX, chunkZ); } catch { }
                        }
                        if (mapChunk == null)
                        {
                            try { mapChunk = api.World.BlockAccessor.GetMapChunk(chunkX, chunkZ); } catch { }
                        }
                        if (mapChunk != null) mapChunksResolved++;

                        var renderSw = Stopwatch.StartNew();
                        RenderChunkColumn(chunkX, chunkZ, chunks, mapChunk, rgba, config.TerrainTileSize, dx * ChunkSize, dz * ChunkSize);
                        renderSw.Stop();
                        status.RenderMilliseconds += renderSw.ElapsedMilliseconds;
                        processedColumns++;
                        LogConsoleTileHeartbeat(tile, processedColumns, status);
                    }
                    finally
                    {
                        if (loadSw.IsRunning)
                        {
                            loadSw.Stop();
                            status.WorldReadMilliseconds += loadSw.ElapsedMilliseconds;
                        }
                        if (chunks != null)
                        {
                            foreach (var chunk in chunks)
                                try { chunk?.Dispose(); } catch { }
                        }
                    }
                }
            }

            WriteRawTile(outputDir, tile.X, tile.Z, rgba, pixelBytes);
            WriteDerivedTerrainStyles(outputDir, tile.X, tile.Z, rgba, config.TerrainTileSize, config.ExportSepiaTerrain, config.ExportTopographicTerrain);

            var shadeSw = Stopwatch.StartNew();
            ApplyHillshade(rgba, config.TerrainTileSize);
            shadeSw.Stop();
            status.RenderMilliseconds += shadeSw.ElapsedMilliseconds;
            RenderedTile summary = SummarizeTile(rgba, config.TerrainTileSize);

            if (config.ExportNormalTerrain)
            {
                string xDir = Path.Combine(outputDir, tile.X.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(xDir);
                string finalPath = Path.Combine(xDir, tile.Z.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png");
                string writePath = atomicReplace ? finalPath + ".tmp" : finalPath;
                var pngSw = Stopwatch.StartNew();
                PngWriter.WriteRgba(writePath, config.TerrainTileSize, config.TerrainTileSize, rgba);
                if (atomicReplace) File.Move(writePath, finalPath, true);
                pngSw.Stop();
                status.PngWriteMilliseconds += pngSw.ElapsedMilliseconds;
            }
            status.BlockColorCacheEntries = blockColorCache.Count;
            return summary;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rgba, clearArray: false);
        }
    }

    private string ResolveCurrentWorldFilepath()
    {
        string path = BlockLogger.Services.WorldSavePathResolver.Resolve(api.WorldManager, api.DataBasePath);
        api.Logger.Notification($"[ServerEssentials/WebMap] terrain export resolved active save database to '{path}'.");
        return path;
    }

    private TerrainCoverage EnumerateGeneratedTiles(string savePath, int tileSize)
    {
        savePath = Path.GetFullPath(savePath);
        if (!File.Exists(savePath))
        {
            throw new FileNotFoundException(
                $"The active Vintage Story save database was not found at '{savePath}'.",
                savePath);
        }

        // First try a true read-only/private connection to the live save. Do not use
        // SQLite shared-cache mode here: Vintage Story already owns this database and a
        // second shared-cache/WAL participant can require side-file access that dedicated
        // server service accounts do not have, producing SQLITE_CANTOPEN (14).
        try
        {
            return EnumerateGeneratedTilesFromDatabase(savePath, tileSize, readOnly: true);
        }
        catch (SqliteException ex)
        {
            api.Logger.Warning(
                "[ServerEssentials/WebMap] direct read-only terrain coverage open failed for '{0}' (SQLite {1}/{2}: {3}). Retrying from a private terrain-coverage snapshot.",
                savePath, ex.SqliteErrorCode, ex.SqliteExtendedErrorCode, ex.Message);
        }

        // Some Vintage Story deployments keep the live save in WAL mode or under a
        // directory where this mod can read the database but cannot participate in the
        // database's -shm file. Make a short-lived private copy under our writable map
        // data root, including WAL/SHM sidecars when present, and query that instead.
        // The snapshot is used only to enumerate already-generated map-chunk positions;
        // actual chunk contents are read from the live save by the terrain-only reader.
        // Failure to open that reader preserves the existing public cache.
        string snapshotPath = Path.Combine(
            rootPath,
            ".terrain-coverage-" + Guid.NewGuid().ToString("N") + ".vcdbs");
        string snapshotWal = snapshotPath + "-wal";
        string snapshotShm = snapshotPath + "-shm";
        string snapshotJournal = snapshotPath + "-journal";
        try
        {
            Directory.CreateDirectory(rootPath);
            CopyDatabaseSnapshotFile(savePath, snapshotPath);
            CopyDatabaseSnapshotSidecar(savePath + "-wal", snapshotWal);
            CopyDatabaseSnapshotSidecar(savePath + "-shm", snapshotShm);
            CopyDatabaseSnapshotSidecar(savePath + "-journal", snapshotJournal);

            TerrainCoverage coverage = EnumerateGeneratedTilesFromDatabase(snapshotPath, tileSize, readOnly: false);
            api.Logger.Notification(
                "[ServerEssentials/WebMap] terrain coverage read succeeded from a private terrain-coverage snapshot ({0:n0} generated overworld map chunks).",
                coverage.ChunkColumns);
            return coverage;
        }
        catch (Exception snapshotEx)
        {
            throw new InvalidOperationException(
                $"Unable to read generated terrain coverage from the active save '{savePath}' or from a private terrain-coverage snapshot. " +
                "Check the resolved save path and filesystem permissions. " +
                snapshotEx.GetType().Name + ": " + snapshotEx.Message,
                snapshotEx);
        }
        finally
        {
            TryDeleteSnapshotFile(snapshotJournal);
            TryDeleteSnapshotFile(snapshotShm);
            TryDeleteSnapshotFile(snapshotWal);
            TryDeleteSnapshotFile(snapshotPath);
        }
    }

    private TerrainCoverage EnumerateGeneratedTilesFromDatabase(string databasePath, int tileSize, bool readOnly)
    {
        var tiles = new HashSet<TileCoord>();
        var chunks = new HashSet<ChunkCoord>();
        long count = 0;
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 10
        };

        using var connection = new SqliteConnection(csb.ConnectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=10000;";
            pragma.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT position FROM mapchunk";
        command.CommandTimeout = 30;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long packed = reader.GetInt64(0);
            DecodeMapChunkPosition(packed, out int chunkX, out int chunkZ, out int dimension);
            if (dimension != 0) continue;
            int worldX = chunkX * ChunkSize;
            int worldZ = chunkZ * ChunkSize;
            chunks.Add(new ChunkCoord(chunkX, chunkZ));
            tiles.Add(new TileCoord(FloorDiv(worldX, tileSize), FloorDiv(worldZ, tileSize)));
            count++;
        }
        return new TerrainCoverage(tiles, chunks, count);
    }

    private static void CopyDatabaseSnapshotFile(string source, string destination)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                File.Copy(source, destination, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (attempt < 3) System.Threading.Thread.Sleep(150);
            }
        }
        throw new IOException($"Could not copy the live save database '{source}' for terrain coverage enumeration.", last);
    }

    private static void CopyDatabaseSnapshotSidecar(string source, string destination)
    {
        if (!File.Exists(source)) return;
        try { File.Copy(source, destination, true); }
        catch (IOException) { /* Sidecar can disappear during a checkpoint; the main DB copy may already be sufficient. */ }
    }

    private static void TryDeleteSnapshotFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void ValidateStartupCoverageCoordinates(TerrainCoverage coverage)
    {
        if (coverage.ChunkColumns <= 0 || coverage.Chunks.Count == 0) return;

        int tested = 0;
        int found = 0;
        foreach (ChunkCoord chunk in coverage.Chunks.Take(16))
        {
            tested++;
            if (api.WorldManager.BlockingTestMapChunkExists(chunk.X, chunk.Z)) found++;
        }

        api.Logger.Notification(
            "[ServerEssentials/WebMap] terrain coverage coordinate sanity check: {0}/{1} sampled map chunks resolved through Vintage Story.",
            found, tested);

        if (tested > 0 && found == 0)
        {
            throw new InvalidDataException(
                "Terrain coverage coordinates decoded from the VCDBS mapchunk table did not match any Vintage Story map chunks. " +
                "Refusing to render/publish terrain because the save-position decoder is likely incompatible with this world/runtime.");
        }
    }

    private void RenderChunkColumn(
        int chunkX,
        int chunkZ,
        IServerChunk[] chunks,
        IMapChunk? mapChunk,
        byte[] rgba,
        int tileSize,
        int pixelOffsetX,
        int pixelOffsetZ,
        ushort[]? preferredHeights = null)
    {
        const int ColumnCells = ChunkSize * ChunkSize;
        int[] surfaceIds = ArrayPool<int>.Shared.Rent(ColumnCells);
        ushort[] surfaceHeights = ArrayPool<ushort>.Shared.Rent(ColumnCells);
        bool[] unpacked = ArrayPool<bool>.Shared.Rent(Math.Max(1, chunks.Length));
        try
        {
            Array.Clear(surfaceIds, 0, ColumnCells);
            Array.Clear(surfaceHeights, 0, ColumnCells);
            Array.Clear(unpacked, 0, chunks.Length);

            ushort[]? heights = preferredHeights ?? mapChunk?.RainHeightMap;
            if (heights != null && heights.Length >= ColumnCells)
            {
                Interlocked.Increment(ref rainHeightMapsResolved);
            }
            else
            {
                heights = mapChunk?.WorldGenTerrainHeightMap;
                if (heights != null && heights.Length >= ColumnCells)
                {
                    Interlocked.Increment(ref terrainHeightMapsResolved);
                }
                else
                {
                    heights = null;
                    Interlocked.Increment(ref accessorHeightFallbackColumns);
                }
            }

            int unresolved = ColumnCells;
            if (heights != null)
            {
                // Fast path: use persisted 2D height metadata and unpack only the vertical chunk
                // sections actually touched by those heights. This avoids unpacking the entire
                // world-height column for every map chunk.
                unresolved = 0;
                int mapSizeY = api.WorldManager.MapSizeY;
                for (int localZ = 0; localZ < ChunkSize; localZ++)
                {
                    for (int localX = 0; localX < ChunkSize; localX++)
                    {
                        int idx2d = localZ * ChunkSize + localX;
                        int y = Math.Clamp((int)heights[idx2d], 0, mapSizeY - 1);
                        int id = ResolveSurfaceBlockId(chunks, unpacked, localX, y, localZ, out int resolvedY);
                        if (id == 0)
                        {
                            unresolved++;
                            continue;
                        }

                        surfaceIds[idx2d] = id;
                        surfaceHeights[idx2d] = (ushort)resolvedY;
                    }
                }
            }

            if (unresolved > 0)
            {
                // Fallback path: resolve every still-missing X/Z position in one top-down sweep.
                // The scan order matches the chunk's memory layout (Y plane -> Z -> X), which is
                // far cheaper than 1,024 independent top-to-bottom searches. Entire empty vertical
                // chunks are skipped before unpacking.
                ResolveMissingSurfacesByColumnScan(chunks, unpacked, surfaceIds, surfaceHeights, ref unresolved);
                Interlocked.Increment(ref directScanFallbackColumns);
            }

            Interlocked.Add(ref surfaceSampleCount, ColumnCells);
            Interlocked.Add(ref unresolvedSurfaceSampleCount, unresolved);
            Interlocked.Add(ref resolvedSurfaceSampleCount, ColumnCells - unresolved);

            int mapHeight = Math.Max(1, api.WorldManager.MapSizeY - 1);
            for (int localZ = 0; localZ < ChunkSize; localZ++)
            {
                for (int localX = 0; localX < ChunkSize; localX++)
                {
                    int idx2d = localZ * ChunkSize + localX;
                    int blockId = surfaceIds[idx2d];
                    if (blockId == 0) continue;

                    // Diagnostics are intentionally sampled at 1/64 of terrain pixels.
                    // Production exports do not need a dictionary lookup for every rendered block.
                    if ((localX & 7) == 0 && (localZ & 7) == 0)
                    {
                        lock (sampledBlockCountsLock)
                        {
                            if (sampledBlockCounts.Count < 256 || sampledBlockCounts.ContainsKey(blockId))
                                sampledBlockCounts[blockId] = sampledBlockCounts.TryGetValue(blockId, out long n) ? n + 1 : 1;
                        }
                    }

                    var color = GetBlockColor(blockId);
                    int px = pixelOffsetX + localX;
                    int pz = pixelOffsetZ + localZ;
                    if ((uint)px >= (uint)tileSize || (uint)pz >= (uint)tileSize) continue;
                    int offset = (pz * tileSize + px) * 4;
                    rgba[offset] = color.R;
                    rgba[offset + 1] = color.G;
                    rgba[offset + 2] = color.B;
                    rgba[offset + 3] = (byte)Math.Clamp((int)Math.Round(surfaceHeights[idx2d] * 255.0 / mapHeight), 1, 255);
                }
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(unpacked, clearArray: false);
            ArrayPool<ushort>.Shared.Return(surfaceHeights, clearArray: false);
            ArrayPool<int>.Shared.Return(surfaceIds, clearArray: false);
        }
    }

    private int ResolveSurfaceBlockId(
        IServerChunk[] chunks,
        bool[] unpacked,
        int localX,
        int y,
        int localZ,
        out int resolvedY)
    {
        resolvedY = y;
        int minY = Math.Max(0, y - 16);
        for (int yy = y; yy >= minY; yy--)
        {
            int chunkY = yy / ChunkSize;
            if ((uint)chunkY >= (uint)chunks.Length) continue;
            var chunk = chunks[chunkY];
            if (chunk == null || chunk.Empty) continue;
            EnsureChunkUnpacked(chunk, unpacked, chunkY);
            if (chunk.Data == null) continue;

            int localY = yy % ChunkSize;
            int index3d = (localY * ChunkSize + localZ) * ChunkSize + localX;
            // Default is the normal engine surface semantics: solid first, then fluid when
            // the solid layer is empty. It also avoids doing separate solid/fluid reads.
            int id = chunk.Data.GetBlockId(index3d, BlockLayersAccess.Default);
            if (id == 0) continue;
            resolvedY = yy;
            return id;
        }
        return 0;
    }

    private static void EnsureChunkUnpacked(IServerChunk chunk, bool[] unpacked, int chunkY)
    {
        if ((uint)chunkY < (uint)unpacked.Length && unpacked[chunkY]) return;
        chunk.Unpack_ReadOnly();
        if ((uint)chunkY < (uint)unpacked.Length) unpacked[chunkY] = true;
    }

    private void ResolveMissingSurfacesByColumnScan(
        IServerChunk[] chunks,
        bool[] unpacked,
        int[] surfaceIds,
        ushort[] surfaceHeights,
        ref int unresolved)
    {
        if (unresolved <= 0) return;
        int topChunkY = Math.Min(chunks.Length - 1, Math.Max(0, (api.WorldManager.MapSizeY - 1) / ChunkSize));

        for (int chunkY = topChunkY; chunkY >= 0 && unresolved > 0; chunkY--)
        {
            var chunk = chunks[chunkY];
            if (chunk == null || chunk.Empty) continue;
            EnsureChunkUnpacked(chunk, unpacked, chunkY);
            var data = chunk.Data;
            if (data == null) continue;

            int maxLocalY = chunkY == topChunkY
                ? Math.Min(ChunkSize - 1, (api.WorldManager.MapSizeY - 1) % ChunkSize)
                : ChunkSize - 1;

            for (int localY = maxLocalY; localY >= 0 && unresolved > 0; localY--)
            {
                int yBase = localY * ChunkSize * ChunkSize;
                int worldY = chunkY * ChunkSize + localY;
                for (int localZ = 0; localZ < ChunkSize; localZ++)
                {
                    int rowBase = yBase + localZ * ChunkSize;
                    int idx2d = localZ * ChunkSize;
                    for (int localX = 0; localX < ChunkSize; localX++, idx2d++)
                    {
                        if (surfaceIds[idx2d] != 0) continue;
                        int id = data.GetBlockId(rowBase + localX, BlockLayersAccess.Default);
                        if (id == 0) continue;
                        surfaceIds[idx2d] = id;
                        surfaceHeights[idx2d] = (ushort)worldY;
                        unresolved--;
                    }
                }
            }
        }
    }

    private WebMapSaveDbTerrainReader? OpenDirectSaveReader(WebMapConfig config, string savePath, WebMapTerrainExportStatus status)
    {
        if (!config.UseDirectSaveDatabaseTerrain)
        {
            status.TerrainReadMode = "world-api";
            return null;
        }

        WebMapSaveDbTerrainReader? reader = WebMapSaveDbTerrainReader.TryOpen(api, savePath, out string reason);
        if (reader == null)
        {
            status.TerrainReadMode = "save-db-unavailable";
            api.Logger.Warning("[ServerEssentials/WebMap] direct save-database terrain reader unavailable: {0}", reason);
            return null;
        }

        status.TerrainReadMode = "save-db-surface";
        api.Logger.Notification("[ServerEssentials/WebMap] direct read-only save-database terrain reader active; only RainHeightMap surface sections will be decoded for bulk map rendering.");
        return reader;
    }

    private void PrimeBlockColorCache()
    {
        if (Volatile.Read(ref blockPalettePrimed) != 0) return;
        int maxId = 0;
        foreach (Block block in api.World.Blocks)
        {
            if (block == null) continue;
            if (block.Id > maxId) maxId = block.Id;
        }

        var colors = new Rgb[Math.Max(1, maxId + 1)];
        Rgb air = new Rgb(16, 22, 29);
        Array.Fill(colors, air);
        foreach (Block block in api.World.Blocks)
        {
            if (block == null) continue;
            Rgb color = TerrainPalette.Get(block);
            blockColorCache[block.Id] = color;
            if ((uint)block.Id < (uint)colors.Length) colors[block.Id] = color;
        }
        blockColorById = colors;
        Volatile.Write(ref blockPalettePrimed, 1);
    }

    private Task PrimeBlockColorCacheOnMainThreadAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref blockPalettePrimed) != 0) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Event.EnqueueMainThreadTask(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                PrimeBlockColorCache();
                tcs.TrySetResult(true);
            }
            catch (OperationCanceledException) { tcs.TrySetCanceled(cancellationToken); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }, "nses-webmap-prime-terrain-palette");
        return tcs.Task;
    }

    private Rgb GetBlockColor(int blockId)
    {
        Rgb[] colors = blockColorById;
        if ((uint)blockId < (uint)colors.Length) return colors[blockId];
        if (blockColorCache.TryGetValue(blockId, out var cached)) return cached;
        // Direct save-DB exports render off the main thread. The palette is fully primed on
        // the main thread before that path begins, so an unknown id here is treated as air
        // instead of reaching back into the live world API from a worker thread.
        if (Volatile.Read(ref blockPalettePrimed) != 0)
        {
            if (colors.Length > 0) return colors[0];
            return new Rgb(16, 22, 29);
        }
        Block block = api.World.GetBlock(blockId);
        Rgb color = TerrainPalette.Get(block);
        blockColorCache[blockId] = color;
        return color;
    }

    private static RenderedTile SummarizeTile(byte[] rgba, int tileSize)
    {
        int cellsPerSide = Math.Max(1, tileSize / ChunkSize);
        byte[] summary = new byte[cellsPerSide * cellsPerSide * 3];
        long totalR = 0, totalG = 0, totalB = 0, totalCount = 0;
        for (int cz = 0; cz < cellsPerSide; cz++)
        {
            for (int cx = 0; cx < cellsPerSide; cx++)
            {
                long r = 0, g = 0, b = 0, count = 0;
                int x0 = cx * ChunkSize, z0 = cz * ChunkSize;
                int x1 = Math.Min(tileSize, x0 + ChunkSize), z1 = Math.Min(tileSize, z0 + ChunkSize);
                for (int z = z0; z < z1; z++)
                {
                    int row = z * tileSize * 4;
                    for (int x = x0; x < x1; x++)
                    {
                        int o = row + x * 4;
                        if (rgba[o + 3] == 0) continue;
                        r += rgba[o];
                        g += rgba[o + 1];
                        b += rgba[o + 2];
                        count++;
                    }
                }

                totalR += r;
                totalG += g;
                totalB += b;
                totalCount += count;

                int dst = (cz * cellsPerSide + cx) * 3;
                if (count == 0)
                {
                    summary[dst] = 16;
                    summary[dst + 1] = 22;
                    summary[dst + 2] = 29;
                }
                else
                {
                    summary[dst] = (byte)(r / count);
                    summary[dst + 1] = (byte)(g / count);
                    summary[dst + 2] = (byte)(b / count);
                }
            }
        }

        Rgb average = totalCount <= 0
            ? new Rgb(16, 22, 29)
            : new Rgb((byte)(totalR / totalCount), (byte)(totalG / totalCount), (byte)(totalB / totalCount));
        return new RenderedTile(average, summary);
    }

    private void WriteDerivedTerrainStyles(string terrainRoot, int tileX, int tileZ, byte[] sourceRgba, int size, bool exportSepia, bool exportTopo)
    {
        if (!exportSepia && !exportTopo) return;

        int bytes = checked(size * size * 4);
        byte[]? sepia = exportSepia ? ArrayPool<byte>.Shared.Rent(bytes) : null;
        byte[]? topo = exportTopo ? ArrayPool<byte>.Shared.Rent(bytes) : null;
        try
        {
            if (sepia != null) Array.Clear(sepia, 0, bytes);
            if (topo != null) Array.Clear(topo, 0, bytes);
            int mapHeight = Math.Max(1, api.WorldManager.MapSizeY - 1);

            for (int i = 0; i < bytes; i += 4)
            {
                byte h = sourceRgba[i + 3];
                if (h == 0) continue;
                int lum = (sourceRgba[i] * 30 + sourceRgba[i + 1] * 59 + sourceRgba[i + 2] * 11) / 100;
                int elevation = (h * mapHeight + 127) / 255;

                if (sepia != null)
                {
                    int tone = Math.Clamp(150 + (lum - 110) / 3 + elevation / Math.Max(8, mapHeight / 18), 112, 214);
                    sepia[i] = (byte)Math.Clamp(tone + 22, 0, 255);
                    sepia[i + 1] = (byte)Math.Clamp(tone + 5, 0, 255);
                    sepia[i + 2] = (byte)Math.Clamp(tone - 24, 0, 255);
                    sepia[i + 3] = h;
                }

                if (topo != null)
                {
                    int topoTone = Math.Clamp(226 - elevation * 34 / Math.Max(1, mapHeight), 184, 230);
                    topo[i] = (byte)(topoTone + 8);
                    topo[i + 1] = (byte)(topoTone + 3);
                    topo[i + 2] = (byte)Math.Max(0, topoTone - 8);
                    topo[i + 3] = h;
                }
            }

            if (sepia != null) ApplyHillshade(sepia, size);
            if (topo != null)
            {
                ApplyTopographicContours(topo, sourceRgba, size, mapHeight);
                ApplyHillshade(topo, size);
            }
            ApplyDerivedWaterEmphasis(sepia, topo, sourceRgba, size);

            if (sepia != null)
            {
                string sepiaPath = StyleTilePath(terrainRoot, "sepia", tileX, tileZ);
                Directory.CreateDirectory(Path.GetDirectoryName(sepiaPath)!);
                PngWriter.WriteRgba(sepiaPath, size, size, sepia);
            }
            if (topo != null)
            {
                string topoPath = StyleTilePath(terrainRoot, "topo", tileX, tileZ);
                Directory.CreateDirectory(Path.GetDirectoryName(topoPath)!);
                PngWriter.WriteRgba(topoPath, size, size, topo);
            }
        }
        finally
        {
            if (sepia != null) ArrayPool<byte>.Shared.Return(sepia, clearArray: false);
            if (topo != null) ArrayPool<byte>.Shared.Return(topo, clearArray: false);
        }
    }

    private static void ApplyDerivedWaterEmphasis(byte[]? sepia, byte[]? topo, byte[] sourceRgba, int size)
    {
        if (size <= 0) return;

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (z * size + x) * 4;
                if (!IsSourceWater(sourceRgba, i)) continue;

                bool shoreline =
                    x == 0 || z == 0 || x == size - 1 || z == size - 1 ||
                    !IsSourceWater(sourceRgba, (z * size + Math.Max(0, x - 1)) * 4) ||
                    !IsSourceWater(sourceRgba, (z * size + Math.Min(size - 1, x + 1)) * 4) ||
                    !IsSourceWater(sourceRgba, (Math.Max(0, z - 1) * size + x) * 4) ||
                    !IsSourceWater(sourceRgba, (Math.Min(size - 1, z + 1) * size + x) * 4);

                // Sepia remains parchment-led, but the cool slate-blue water provides enough
                // hue/value separation to identify waterways instantly without looking like
                // the normal-color terrain layer.
                if (sepia != null)
                {
                    sepia[i] = shoreline ? (byte)62 : (byte)84;
                    sepia[i + 1] = shoreline ? (byte)91 : (byte)116;
                    sepia[i + 2] = shoreline ? (byte)104 : (byte)129;
                }

                // Topographic water is slightly brighter than the sepia treatment so contour
                // lines and land relief remain the dominant information while hydrography is
                // still unmistakable.
                if (topo != null)
                {
                    topo[i] = shoreline ? (byte)66 : (byte)101;
                    topo[i + 1] = shoreline ? (byte)101 : (byte)139;
                    topo[i + 2] = shoreline ? (byte)116 : (byte)154;
                }
            }
        }
    }

    private static bool IsSourceWater(byte[] sourceRgba, int i)
    {
        if (i < 0 || i + 3 >= sourceRgba.Length || sourceRgba[i + 3] == 0) return false;

        // Water is deliberately emitted by the terrain palette as this stable RGB value.
        // Use a small tolerance so this remains robust if an upstream renderer applies a
        // tiny variation before derived-style generation in a future refactor.
        return Math.Abs(sourceRgba[i] - 54) <= 4 &&
               Math.Abs(sourceRgba[i + 1] - 104) <= 4 &&
               Math.Abs(sourceRgba[i + 2] - 145) <= 4;
    }

    private static void ApplyTopographicContours(byte[] topo, byte[] sourceRgba, int size, int mapHeight)
    {
        if (size < 2) return;
        const int MinorContourBlocks = 10;
        const int MajorContourBlocks = 50;
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (z * size + x) * 4;
                byte h = sourceRgba[i + 3];
                if (h == 0) continue;
                int y = (h * mapHeight + 127) / 255;
                int rightY = y;
                int downY = y;
                if (x + 1 < size)
                {
                    byte rh = sourceRgba[i + 7];
                    if (rh != 0) rightY = (rh * mapHeight + 127) / 255;
                }
                if (z + 1 < size)
                {
                    byte dh = sourceRgba[i + size * 4 + 3];
                    if (dh != 0) downY = (dh * mapHeight + 127) / 255;
                }

                bool minor = y / MinorContourBlocks != rightY / MinorContourBlocks || y / MinorContourBlocks != downY / MinorContourBlocks;
                bool major = y / MajorContourBlocks != rightY / MajorContourBlocks || y / MajorContourBlocks != downY / MajorContourBlocks;
                if (!minor) continue;

                int darken = major ? 78 : 38;
                topo[i] = (byte)Math.Max(0, topo[i] - darken);
                topo[i + 1] = (byte)Math.Max(0, topo[i + 1] - darken);
                topo[i + 2] = (byte)Math.Max(0, topo[i + 2] - darken);
            }
        }
    }

    private static string StyleTilePath(string terrainRoot, string style, int x, int z) => Path.Combine(
        terrainRoot,
        "styles",
        style,
        x.ToString(System.Globalization.CultureInfo.InvariantCulture),
        z.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png");

    private static string StyleRoot(string terrainRoot, string style) => Path.Combine(terrainRoot, "styles", style);

    private void WriteDerivedStyleLodPyramids(string terrainRoot, WebMapTerrainManifest manifest, WebMapTerrainExportStatus status, WebMapConfig config)
    {
        if (config.ExportSepiaTerrain) WriteLodPyramid(StyleRoot(terrainRoot, "sepia"), manifest, status, config);
        if (config.ExportTopographicTerrain) WriteLodPyramid(StyleRoot(terrainRoot, "topo"), manifest, status, config);
    }

    private static void FillBackground(byte[] rgba, int usedLength)
    {
        Array.Clear(rgba, 0, usedLength);
    }

    private static void ApplyHillshade(byte[] rgba, int size)
    {
        int cells = size * size;
        if (size < 3) { for (int i = 3; i < cells * 4; i += 4) rgba[i] = 255; return; }
        byte[] heights = ArrayPool<byte>.Shared.Rent(cells);
        try
        {
            for (int i = 0, p = 3; i < cells; i++, p += 4) heights[i] = rgba[p];
            for (int z = 0; z < size; z++)
            {
                int zm = Math.Max(0, z - 1), zp = Math.Min(size - 1, z + 1);
                int zm2 = Math.Max(0, z - 2), zp2 = Math.Min(size - 1, z + 2);
                for (int x = 0; x < size; x++)
                {
                    int xm = Math.Max(0, x - 1), xp = Math.Min(size - 1, x + 1);
                    int xm2 = Math.Max(0, x - 2), xp2 = Math.Min(size - 1, x + 2);
                    int offset = (z * size + x) * 4;
                    if (rgba[offset + 3] == 0) continue;

                    int hx = heights[z * size + xp] - heights[z * size + xm];
                    int hz = heights[zp * size + x] - heights[zm * size + x];
                    int hxWide = heights[z * size + xp2] - heights[z * size + xm2];
                    int hzWide = heights[zp2 * size + x] - heights[zm2 * size + x];
                    int slope = Math.Abs(hx) + Math.Abs(hz);

                    // Multi-scale raised-relief lighting. Screen/world Z increases downward on
                    // the web map, so a northwest light source must brighten positive X/Z
                    // height gradients. The old sign did the opposite and made tree canopies
                    // and ridges read visually as depressions. Keep edge darkening modest so
                    // abrupt leaf-height changes do not turn into dark crater rims.
                    int directional = (hx * 2) + (hz * 2) + ((hxWide + hzWide) / 2);
                    int shade = Math.Clamp(6 + directional - Math.Min(8, slope / 5), -42, 42);
                    rgba[offset] = (byte)Math.Clamp(rgba[offset] + shade, 0, 255);
                    rgba[offset + 1] = (byte)Math.Clamp(rgba[offset + 1] + shade, 0, 255);
                    rgba[offset + 2] = (byte)Math.Clamp(rgba[offset + 2] + shade, 0, 255);
                    rgba[offset + 3] = 255;
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(heights, clearArray: false); }
    }

    private WebMapTerrainManifest CreateManifest(WebMapConfig config, TerrainCoverage coverage)
    {
        return new WebMapTerrainManifest
        {
            Version = ManifestVersion,
            WorldName = api.WorldManager.SaveGame.WorldName ?? "",
            SavegameIdentifier = api.WorldManager.SaveGame.SavegameIdentifier ?? "",
            TileSize = config.TerrainTileSize,
            Style = "server-palette-save-db-fastpatch-true-pyramid-hillshade-raised-v11",
            Tiles = new Dictionary<string, WebMapTerrainTileMetadata>(StringComparer.Ordinal)
        };
    }

    private void FinalizeManifest(WebMapTerrainManifest manifest, TerrainCoverage coverage, int tileCount, long chunkColumns)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        manifest.Version = ManifestVersion;
        manifest.GeneratedUnixMilliseconds = now;
        manifest.TileCount = tileCount;
        manifest.ChunkColumnCount = chunkColumns;
        manifest.TileSize = Math.Max(1, manifest.TileSize);
        if (coverage.Tiles.Count > 0)
        {
            manifest.MinTileX = coverage.Tiles.Min(t => t.X);
            manifest.MaxTileX = coverage.Tiles.Max(t => t.X);
            manifest.MinTileZ = coverage.Tiles.Min(t => t.Z);
            manifest.MaxTileZ = coverage.Tiles.Max(t => t.Z);
            manifest.MinWorldX = manifest.MinTileX * manifest.TileSize;
            manifest.MaxWorldXExclusive = (manifest.MaxTileX + 1) * manifest.TileSize;
            manifest.MinWorldZ = manifest.MinTileZ * manifest.TileSize;
            manifest.MaxWorldZExclusive = (manifest.MaxTileZ + 1) * manifest.TileSize;
        }
        else
        {
            manifest.MinTileX = manifest.MaxTileX = manifest.MinTileZ = manifest.MaxTileZ = 0;
            manifest.MinWorldX = manifest.MaxWorldXExclusive = manifest.MinWorldZ = manifest.MaxWorldZExclusive = 0;
        }
    }

    private void WriteOverview(string dir, WebMapTerrainManifest manifest, WebMapTerrainExportStatus status)
    {
        var sw = Stopwatch.StartNew();
        var tiles = manifest.Tiles?.Values.ToList() ?? new List<WebMapTerrainTileMetadata>();
        if (tiles.Count == 0)
        {
            manifest.OverviewAvailable = false;
            manifest.OverviewWidth = manifest.OverviewHeight = 1;
            manifest.OverviewTileStride = 1;
            manifest.OverviewCellSizeBlocks = ChunkSize;
            byte[] empty = { 0, 0, 0, 0 };
            PngWriter.WriteRgba(Path.Combine(dir, "overview.png"), 1, 1, empty);
            sw.Stop(); status.OverviewMilliseconds += sw.ElapsedMilliseconds;
            return;
        }

        int cellsPerTile = Math.Max(1, manifest.TileSize / ChunkSize);
        int minTileX = tiles.Min(t => t.X), maxTileX = tiles.Max(t => t.X), minTileZ = tiles.Min(t => t.Z), maxTileZ = tiles.Max(t => t.Z);
        int rawWidth = checked((maxTileX - minTileX + 1) * cellsPerTile);
        int rawHeight = checked((maxTileZ - minTileZ + 1) * cellsPerTile);
        const int MaxOverviewSide = 1024;
        int stride = Math.Max(1, (int)Math.Ceiling(Math.Max(rawWidth, rawHeight) / (double)MaxOverviewSide));
        int width = Math.Max(1, (rawWidth + stride - 1) / stride);
        int height = Math.Max(1, (rawHeight + stride - 1) / stride);
        int cells = checked(width * height);
        // One overview per export: these large buffers must not be retained in shared pools.
        int[] rr = new int[cells], gg = new int[cells], bb = new int[cells], cc = new int[cells];
        byte[] overview = new byte[checked(cells * 4)];
        foreach (var tile in tiles)
        {
            byte[] summary = tile.ChunkRgb ?? Array.Empty<byte>();
            bool hasChunkSummary = summary.Length >= cellsPerTile * cellsPerTile * 3;
            for (int cz = 0; cz < cellsPerTile; cz++)
            {
                for (int cx = 0; cx < cellsPerTile; cx++)
                {
                    int rawX = (tile.X - minTileX) * cellsPerTile + cx;
                    int rawZ = (tile.Z - minTileZ) * cellsPerTile + cz;
                    int ox = rawX / stride, oz = rawZ / stride;
                    int index = oz * width + ox;
                    int src = (cz * cellsPerTile + cx) * 3;
                    byte r = hasChunkSummary ? summary[src] : tile.R;
                    byte g = hasChunkSummary ? summary[src + 1] : tile.G;
                    byte b = hasChunkSummary ? summary[src + 2] : tile.B;
                    rr[index] += r; gg[index] += g; bb[index] += b; cc[index]++;
                }
            }
        }

        int used = cells * 4;
        for (int i = 0; i < cells; i++)
        {
            int o = i * 4;
            if (cc[i] == 0) { overview[o] = 0; overview[o + 1] = 0; overview[o + 2] = 0; overview[o + 3] = 0; }
            else { overview[o] = (byte)(rr[i] / cc[i]); overview[o + 1] = (byte)(gg[i] / cc[i]); overview[o + 2] = (byte)(bb[i] / cc[i]); overview[o + 3] = 255; }
        }
        PngWriter.WriteRgba(Path.Combine(dir, "overview.png"), width, height, overview);

        manifest.OverviewAvailable = true;
        manifest.OverviewWidth = width;
        manifest.OverviewHeight = height;
        manifest.OverviewTileStride = stride;
        manifest.OverviewCellSizeBlocks = ChunkSize * stride;
        status.OverviewCellSizeBlocks = manifest.OverviewCellSizeBlocks;
        sw.Stop(); status.OverviewMilliseconds += sw.ElapsedMilliseconds;
    }

    private HashSet<LodTileCoord> StageIncrementalLodTiles(
        string stageDir,
        string currentDir,
        WebMapTerrainManifest manifest,
        HashSet<TileCoord> dirtyTiles,
        List<string> staleKeys,
        WebMapTerrainExportStatus status,
        WebMapConfig config,
        out bool replaceWholeLod)
    {
        int[] factors = BuildPyramidFactors(manifest);
        manifest.LodFactors = factors;
        manifest.LodTilePixelSize = manifest.TileSize;

        string currentLod = Path.Combine(currentDir, "lod");
        if (!Directory.Exists(currentLod) || factors.Any(f => !Directory.Exists(Path.Combine(currentLod, f.ToString(System.Globalization.CultureInfo.InvariantCulture)))))
        {
            replaceWholeLod = true;
            WriteLodPyramid(stageDir, manifest, status, config);
            return new HashSet<LodTileCoord>();
        }

        replaceWholeLod = false;
        var changed = new HashSet<TileCoord>(dirtyTiles);
        var removedChildren = new HashSet<TileCoord>();
        foreach (string stale in staleKeys)
        {
            if (!TryParseTileKey(stale, out int sx, out int sz)) continue;
            var coord = new TileCoord(sx, sz);
            changed.Add(coord);
            removedChildren.Add(coord);
        }
        if (changed.Count == 0 || factors.Length == 0) return new HashSet<LodTileCoord>();

        var sw = Stopwatch.StartNew();
        var affected = new HashSet<LodTileCoord>();
        int childFactor = 1;
        HashSet<TileCoord> changedChildren = changed;
        int lodLevelIndex = 0;

        foreach (int factor in factors)
        {
            var parents = new HashSet<TileCoord>();
            foreach (TileCoord child in changedChildren)
                parents.Add(new TileCoord(FloorDiv(child.X, 2), FloorDiv(child.Z, 2)));

            var removedParents = new HashSet<TileCoord>();
            foreach (TileCoord parent in parents)
            {
                affected.Add(new LodTileCoord(factor, parent.X, parent.Z));
                bool wrote = WritePyramidParent(
                    stageDir,
                    currentDir,
                    manifest.TileSize,
                    childFactor,
                    factor,
                    parent.X,
                    parent.Z,
                    removedChildren);
                if (!wrote) removedParents.Add(parent);
            }

            changedChildren = parents;
            removedChildren = removedParents;
            childFactor = factor;
            lodLevelIndex++;
            status.ProgressPercent = 96d + 2.5d * lodLevelIndex / Math.Max(1, factors.Length);
            status.Message = $"Updating zoom levels: {lodLevelIndex:n0}/{factors.Length:n0}";
            publishStatus(status.Clone());
        }

        sw.Stop();
        status.LodWriteMilliseconds += sw.ElapsedMilliseconds;
        return affected;
    }

    private void WriteLodPyramid(string dir, WebMapTerrainManifest manifest, WebMapTerrainExportStatus status, WebMapConfig config)
    {
        var sw = Stopwatch.StartNew();
        string lodRoot = Path.Combine(dir, "lod");
        if (Directory.Exists(lodRoot)) Directory.Delete(lodRoot, true);
        Directory.CreateDirectory(lodRoot);

        int[] factors = BuildPyramidFactors(manifest);
        manifest.LodFactors = factors;
        manifest.LodTilePixelSize = manifest.TileSize;
        if (factors.Length == 0 || manifest.Tiles == null || manifest.Tiles.Count == 0)
        {
            sw.Stop();
            status.LodWriteMilliseconds += sw.ElapsedMilliseconds;
            return;
        }

        var childCoords = new HashSet<TileCoord>();
        foreach (WebMapTerrainTileMetadata tile in manifest.Tiles.Values)
            childCoords.Add(new TileCoord(tile.X, tile.Z));

        int childFactor = 1;
        int workers = Math.Clamp(config.TerrainPyramidWorkers, 1, 4);
        int lodLevelIndex = 0;
        foreach (int factor in factors)
        {
            var parentCoords = new HashSet<TileCoord>();
            foreach (TileCoord child in childCoords)
                parentCoords.Add(new TileCoord(FloorDiv(child.X, 2), FloorDiv(child.Z, 2)));

            int levelChildFactor = childFactor;
            int levelFactor = factor;
            var options = new ParallelOptions { MaxDegreeOfParallelism = workers };
            Parallel.ForEach(parentCoords, options, parent =>
            {
                WritePyramidParent(dir, null, manifest.TileSize, levelChildFactor, levelFactor, parent.X, parent.Z, null);
            });

            childCoords = parentCoords;
            childFactor = factor;
            lodLevelIndex++;
            status.ProgressPercent = 96d + 2.5d * lodLevelIndex / Math.Max(1, factors.Length);
            status.Message = $"Building zoom levels: {lodLevelIndex:n0}/{factors.Length:n0}";
            publishStatus(status.Clone());
        }

        sw.Stop();
        status.LodWriteMilliseconds += sw.ElapsedMilliseconds;
    }

    private static int[] BuildPyramidFactors(WebMapTerrainManifest manifest)
    {
        if (manifest.Tiles == null || manifest.Tiles.Count <= 1) return Array.Empty<int>();
        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        foreach (WebMapTerrainTileMetadata tile in manifest.Tiles.Values)
        {
            minX = Math.Min(minX, tile.X); maxX = Math.Max(maxX, tile.X);
            minZ = Math.Min(minZ, tile.Z); maxZ = Math.Max(maxZ, tile.Z);
        }
        int span = Math.Max(maxX - minX + 1, maxZ - minZ + 1);
        if (span <= 1) return Array.Empty<int>();

        var factors = new List<int>();
        long factor = 2;
        while (factor <= int.MaxValue)
        {
            factors.Add((int)factor);
            if ((span + factor - 1) / factor <= 1) break;
            factor <<= 1;
        }
        return factors.ToArray();
    }

    private static string LodTilePath(string terrainDir, int factor, int x, int z)
    {
        return Path.Combine(
            terrainDir,
            "lod",
            factor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            x.ToString(System.Globalization.CultureInfo.InvariantCulture),
            z.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png");
    }

    /// <summary>
    /// Builds one true pyramid parent from up to four child PNGs. Each child is scaled to
    /// one quadrant of the parent. This retains rendered block-level terrain detail instead
    /// of replacing zoomed-out terrain with chunk-average summary colors.
    /// </summary>
    private static bool WritePyramidParent(
        string stageRoot,
        string? currentRoot,
        int tileSize,
        int childFactor,
        int parentFactor,
        int parentX,
        int parentZ,
        HashSet<TileCoord>? removedChildren)
    {
        int half = tileSize / 2;
        if (half <= 0) return false;
        int pixelBytes = checked(tileSize * tileSize * 4);
        byte[] output = ArrayPool<byte>.Shared.Rent(pixelBytes);
        try
        {
            Array.Clear(output, 0, pixelBytes);
            bool any = false;
            for (int qz = 0; qz < 2; qz++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    int childX = parentX * 2 + qx;
                    int childZ = parentZ * 2 + qz;
                    var childCoord = new TileCoord(childX, childZ);
                    if (removedChildren != null && removedChildren.Contains(childCoord)) continue;

                    string stagedChild = childFactor == 1
                        ? TilePath(stageRoot, childX, childZ)
                        : LodTilePath(stageRoot, childFactor, childX, childZ);
                    string? currentChild = currentRoot == null ? null : childFactor == 1
                        ? TilePath(currentRoot, childX, childZ)
                        : LodTilePath(currentRoot, childFactor, childX, childZ);
                    string? source = File.Exists(stagedChild) ? stagedChild :
                        (!string.IsNullOrEmpty(currentChild) && File.Exists(currentChild) ? currentChild : null);
                    if (source == null || !PngReader.TryReadRgba(source, out int width, out int height, out byte[] childRgba)) continue;
                    if (width != tileSize || height != tileSize) continue;

                    any = true;
                    DownsampleChildIntoQuadrant(childRgba, tileSize, output, qx * half, qz * half, half);
                }
            }

            if (!any) return false;
            string parentPath = LodTilePath(stageRoot, parentFactor, parentX, parentZ);
            string? dir = Path.GetDirectoryName(parentPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            PngWriter.WriteRgba(parentPath, tileSize, tileSize, output);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(output, clearArray: false);
        }
    }

    private static void DownsampleChildIntoQuadrant(byte[] child, int childSize, byte[] parent, int dstX, int dstY, int half)
    {
        for (int y = 0; y < half; y++)
        {
            int sy = Math.Min(childSize - 2, y * 2);
            for (int x = 0; x < half; x++)
            {
                int sx = Math.Min(childSize - 2, x * 2);
                int p0 = (sy * childSize + sx) * 4;
                int p1 = p0 + 4;
                int p2 = p0 + childSize * 4;
                int p3 = p2 + 4;

                int count = 0, r = 0, g = 0, b = 0;
                if (child[p0 + 3] != 0) { r += child[p0]; g += child[p0 + 1]; b += child[p0 + 2]; count++; }
                if (child[p1 + 3] != 0) { r += child[p1]; g += child[p1 + 1]; b += child[p1 + 2]; count++; }
                if (child[p2 + 3] != 0) { r += child[p2]; g += child[p2 + 1]; b += child[p2 + 2]; count++; }
                if (child[p3 + 3] != 0) { r += child[p3]; g += child[p3 + 1]; b += child[p3 + 2]; count++; }

                int d = ((dstY + y) * childSize + (dstX + x)) * 4;
                if (count == 0)
                {
                    parent[d] = parent[d + 1] = parent[d + 2] = parent[d + 3] = 0;
                }
                else
                {
                    // Small contrast preservation keeps roads/ridges readable after repeated
                    // pyramid reductions without adding artificial blur.
                    int rr = r / count, gg = g / count, bb = b / count;
                    int lum = (rr * 3 + gg * 6 + bb) / 10;
                    parent[d] = (byte)Math.Clamp(lum + (rr - lum) * 11 / 10, 0, 255);
                    parent[d + 1] = (byte)Math.Clamp(lum + (gg - lum) * 11 / 10, 0, 255);
                    parent[d + 2] = (byte)Math.Clamp(lum + (bb - lum) * 11 / 10, 0, 255);
                    parent[d + 3] = 255;
                }
            }
        }
    }

    private static WebMapTerrainManifest? LoadManifest(string dir)
    {
        try
        {
            string path = Path.Combine(dir, "manifest.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<WebMapTerrainManifest>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    private static void WriteManifest(string dir, WebMapTerrainManifest manifest)
    {
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private static void WriteManifestAtomic(string dir, WebMapTerrainManifest manifest)
    {
        string final = Path.Combine(dir, "manifest.json");
        string tmp = final + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest));
        File.Move(tmp, final, true);
    }

    private static string RawTilePath(string root, int x, int z) => Path.Combine(
        root,
        "raw",
        x.ToString(System.Globalization.CultureInfo.InvariantCulture),
        z.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin");

    private static void WriteRawTile(string root, int x, int z, byte[] rgba, int usedLength)
    {
        string path = RawTilePath(root, x, z);
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        file.Write(rgba, 0, usedLength);
    }

    private static bool TryReadRawTile(string root, int x, int z, byte[] destination, int usedLength)
    {
        string path = RawTilePath(root, x, z);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != usedLength) return false;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            int offset = 0;
            while (offset < usedLength)
            {
                int read = file.Read(destination, offset, usedLength - offset);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }
        catch { return false; }
    }

    private static void ClearChunkPatch(byte[] rgba, int tileSize, int pixelOffsetX, int pixelOffsetZ)
    {
        int x0 = Math.Clamp(pixelOffsetX, 0, tileSize);
        int z0 = Math.Clamp(pixelOffsetZ, 0, tileSize);
        int x1 = Math.Clamp(pixelOffsetX + ChunkSize, 0, tileSize);
        int z1 = Math.Clamp(pixelOffsetZ + ChunkSize, 0, tileSize);
        int rowBytes = Math.Max(0, (x1 - x0) * 4);
        for (int z = z0; z < z1; z++)
            Array.Clear(rgba, (z * tileSize + x0) * 4, rowBytes);
    }

    private static string TilePath(string root, int x, int z) => Path.Combine(root, x.ToString(System.Globalization.CultureInfo.InvariantCulture), z.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png");
    private static string TileKey(int x, int z) => x.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + z.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static bool TryParseTileKey(string key, out int x, out int z)
    {
        x = z = 0;
        string[] parts = key.Split(',', 2);
        return parts.Length == 2 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out z);
    }


    private static void DecodeMapChunkPosition(long packed, out int x, out int z, out int dimension)
    {
        // VCDBS mapchunk.position uses the save-file ChunkPos packing, NOT MapChunkIndex2D.
        // LSB layout: X(21), guard(1), dimension-low(5), Z(21), guard(1), dimension-high(5), Y(9), reserved(1).
        x = (int)(packed & CoordMask21);
        int dimLow = (int)((packed >> 22) & DimensionMask);
        z = (int)((packed >> 27) & CoordMask21);
        int dimHigh = (int)((packed >> 49) & DimensionMask);
        dimension = dimLow | (dimHigh << 5);
    }


    private static int FloorDiv(int value, int divisor)
    {
        int q = value / divisor;
        int r = value % divisor;
        if (r != 0 && ((r < 0) != (divisor < 0))) q--;
        return q;
    }

    private static void AtomicSwap(string stageDir, string currentDir, string backupDir)
    {
        if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);
        if (Directory.Exists(currentDir)) Directory.Move(currentDir, backupDir);
        try
        {
            Directory.Move(stageDir, currentDir);
            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);
        }
        catch
        {
            if (!Directory.Exists(currentDir) && Directory.Exists(backupDir)) Directory.Move(backupDir, currentDir);
            throw;
        }
    }

    private readonly record struct TileCoord(int X, int Z);
    private readonly record struct LodTileCoord(int Factor, int X, int Z);
    private readonly record struct ChunkCoord(int X, int Z);
    private readonly record struct TerrainCoverage(HashSet<TileCoord> Tiles, HashSet<ChunkCoord> Chunks, long ChunkColumns);

    private static class TerrainPalette
    {
        public static Rgb Get(Block? block)
        {
            if (block == null || block.Id == 0) return new Rgb(16, 22, 29);
            string domain = block.Code?.Domain?.ToLowerInvariant() ?? "";
            string code = block.Code?.Path?.ToLowerInvariant() ?? "";

            // Vintage Story soil blocks keep their grass state in the grasscoverage variant;
            // the block code can still look like ordinary soil. Resolve that before the generic
            // soil fallback or fully-grown grass gets painted brown on dedicated-server exports.
            string grassCoverage = "";
            try
            {
                if (block.Variant != null)
                {
                    foreach (var pair in block.Variant)
                    {
                        if (!string.Equals(pair.Key, "grasscoverage", StringComparison.OrdinalIgnoreCase)) continue;
                        grassCoverage = pair.Value?.ToLowerInvariant() ?? "";
                        break;
                    }
                }
            }
            catch { }
            // Dedicated-server block metadata should expose the grasscoverage variant, but the
            // block code itself is also stable (soil-medium-normal, soil-low-sparse, ...). Keep
            // this fallback so a trimmed/modded block definition cannot silently turn grass brown.
            if (string.IsNullOrEmpty(grassCoverage) &&
                (code.StartsWith("soil-", StringComparison.Ordinal) || code.StartsWith("cob-", StringComparison.Ordinal)))
            {
                if (code.EndsWith("-verysparse", StringComparison.Ordinal)) grassCoverage = "verysparse";
                else if (code.EndsWith("-sparse", StringComparison.Ordinal)) grassCoverage = "sparse";
                else if (code.EndsWith("-normal", StringComparison.Ordinal)) grassCoverage = "normal";
                else if (code.EndsWith("-none", StringComparison.Ordinal)) grassCoverage = "none";
            }
            if (!string.IsNullOrEmpty(grassCoverage) && grassCoverage != "none")
            {
                Rgb grass = Vary(new Rgb(82, 126, 61), domain, code + ":" + grassCoverage, 8);
                Rgb soil = Vary(new Rgb(111, 91, 61), domain, code, 6);
                return grassCoverage switch
                {
                    "normal" => grass,
                    "sparse" => Blend(soil, grass, 0.75),
                    "verysparse" => Blend(soil, grass, 0.50),
                    _ => Blend(soil, grass, 0.65)
                };
            }

            // Stable semantic overrides first. These work for vanilla and for most mod blocks
            // without needing any client texture atlas or color-map access on the dedicated server.
            if (HasAny(code, "water", "lake", "ocean", "river")) return new Rgb(54, 104, 145);
            if (HasAny(code, "lava", "molten")) return new Rgb(207, 83, 35);
            if (code.Contains("snow")) return new Rgb(226, 233, 237);
            if (code.Contains("ice")) return new Rgb(155, 205, 220);
            if (HasAny(code, "grass", "meadow", "moss")) return Vary(new Rgb(86, 122, 62), domain, code, 9);
            if (HasAny(code, "leaves", "foliage", "fern", "shrub")) return Vary(new Rgb(62, 105, 55), domain, code, 10);
            if (HasAny(code, "flower", "crop", "plant", "sapling")) return Vary(new Rgb(78, 119, 64), domain, code, 10);
            if (HasAny(code, "farmland", "compost")) return Vary(new Rgb(105, 79, 48), domain, code, 8);
            if (HasAny(code, "soil", "dirt", "earth", "mud", "peat")) return Vary(new Rgb(111, 91, 61), domain, code, 9);
            if (code.Contains("clay")) return Vary(new Rgb(139, 103, 78), domain, code, 8);
            if (code.Contains("sand")) return Vary(new Rgb(190, 174, 111), domain, code, 8);
            if (code.Contains("gravel")) return Vary(new Rgb(128, 122, 109), domain, code, 8);
            if (HasAny(code, "path", "road", "packed")) return Vary(new Rgb(130, 112, 82), domain, code, 7);
            if (HasAny(code, "plank", "wood", "log", "board")) return Vary(new Rgb(111, 80, 50), domain, code, 9);
            if (HasAny(code, "brick", "tile", "cobble", "ashlar", "stonebrick")) return Vary(new Rgb(129, 111, 96), domain, code, 10);
            if (HasAny(code, "metal", "steel", "iron", "copper", "bronze", "tin", "brass")) return Vary(new Rgb(128, 138, 145), domain, code, 9);

            // Do not depend on EnumBlockMaterial.Water here. Water exists in the 1.22.7 API,
            // but older API assemblies used by some build setups do not expose that enum name.
            // The fluids-layer property is a better semantic test for water-like blocks anyway;
            // lava/molten and ice were already handled by the overrides above.
            if (block.ForFluidsLayer) return new Rgb(54, 104, 145);

            Rgb baseColor = block.BlockMaterial switch
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
                _ => new Rgb(102, 108, 101)
            };
            return Vary(baseColor, domain, code, 7);
        }

        private static Rgb Blend(Rgb a, Rgb b, double amount)
        {
            amount = Math.Clamp(amount, 0.0, 1.0);
            double inv = 1.0 - amount;
            return new Rgb(
                (byte)Math.Clamp((int)Math.Round(a.R * inv + b.R * amount), 0, 255),
                (byte)Math.Clamp((int)Math.Round(a.G * inv + b.G * amount), 0, 255),
                (byte)Math.Clamp((int)Math.Round(a.B * inv + b.B * amount), 0, 255));
        }

        private static bool HasAny(string value, params string[] terms)
        {
            foreach (string term in terms) if (value.Contains(term, StringComparison.Ordinal)) return true;
            return false;
        }

        private static Rgb Vary(Rgb color, string domain, string code, int amount)
        {
            uint hash = 2166136261u;
            foreach (char c in domain) { hash ^= c; hash *= 16777619u; }
            hash ^= (byte)':'; hash *= 16777619u;
            foreach (char c in code) { hash ^= c; hash *= 16777619u; }
            int delta = (int)(hash % (uint)(amount * 2 + 1)) - amount;
            return new Rgb(
                (byte)Math.Clamp(color.R + delta, 0, 255),
                (byte)Math.Clamp(color.G + delta, 0, 255),
                (byte)Math.Clamp(color.B + delta, 0, 255));
        }
    }

    private readonly record struct Rgb(byte R, byte G, byte B);
    private readonly record struct RenderedTile(Rgb Average, byte[] ChunkRgb);
    private readonly record struct RuntimeRenderedTile(RenderedTile Tile, long ProcessedColumns);
    private readonly record struct DatabaseRenderedTile(RenderedTile Tile, long ProcessedColumns, long WorldReadMilliseconds, long RenderMilliseconds, long PngWriteMilliseconds);

    private void ResetSurfaceDiagnostics()
    {
        surfaceSampleCount = 0;
        resolvedSurfaceSampleCount = 0;
        unresolvedSurfaceSampleCount = 0;
        chunkExistenceChecks = 0;
        existingChunkColumns = 0;
        loadedChunkColumns = 0;
        mapChunksResolved = 0;
        rainHeightMapsResolved = 0;
        terrainHeightMapsResolved = 0;
        accessorHeightFallbackColumns = 0;
        directScanFallbackColumns = 0;
        sampledBlockCounts.Clear();
        blockColorCache.Clear();
    }

    private void ValidateAndLogSurfaceDiagnostics(TerrainCoverage coverage)
    {
        if (coverage.ChunkColumns <= 0) return;

        double unresolvedRatio = surfaceSampleCount <= 0 ? 1.0 : unresolvedSurfaceSampleCount / (double)surfaceSampleCount;
        string topBlocks = string.Join(", ", sampledBlockCounts
            .OrderByDescending(kv => kv.Value)
            .Take(8)
            .Select(kv =>
            {
                Block? block = api.World.GetBlock(kv.Key);
                string code = block?.Code?.ToString() ?? ("id:" + kv.Key);
                return code + "=" + kv.Value.ToString("n0", System.Globalization.CultureInfo.InvariantCulture);
            }));

        api.Logger.Notification(
            "[ServerEssentials/WebMap] terrain surface diagnostics: resolved={0:n0}, unresolved={1:n0} ({2:P1}), uniqueSampledBlocks={3:n0}, paletteEntries={4:n0}, top=[{5}].",
            resolvedSurfaceSampleCount, unresolvedSurfaceSampleCount, unresolvedRatio, sampledBlockCounts.Count, blockColorCache.Count, topBlocks);
        api.Logger.Notification(
            "[ServerEssentials/WebMap] terrain loader diagnostics: redundantExistenceChecks={0:n0}, generatedColumnsVisited={1:n0}, loadedColumns={2:n0}, mapChunks={3:n0}, rainHeightMaps={4:n0}, terrainHeightMaps={5:n0}, accessorHeightFallbackColumns={6:n0}, directScanFallbackColumns={7:n0}.",
            chunkExistenceChecks, existingChunkColumns, loadedChunkColumns, mapChunksResolved, rainHeightMapsResolved, terrainHeightMapsResolved, accessorHeightFallbackColumns, directScanFallbackColumns);

        if (surfaceSampleCount == 0 || resolvedSurfaceSampleCount == 0 || unresolvedRatio >= 0.90)
        {
            throw new InvalidDataException(
                $"Terrain renderer resolved only {resolvedSurfaceSampleCount:n0} of {surfaceSampleCount:n0} sampled surface pixels " +
                $"({unresolvedRatio:P1} unresolved). Loader diagnostics: existenceChecks={chunkExistenceChecks:n0}, " +
                $"existingColumns={existingChunkColumns:n0}, loadedColumns={loadedChunkColumns:n0}, " +
                $"mapChunks={mapChunksResolved:n0}, rainHeightMaps={rainHeightMapsResolved:n0}, " +
                $"terrainHeightMaps={terrainHeightMapsResolved:n0}, accessorHeightFallbackColumns={accessorHeightFallbackColumns:n0}, " +
                $"directScanFallbackColumns={directScanFallbackColumns:n0}. Refusing to publish gray/empty terrain tiles.");
        }
    }

    private void SetProgressPhase(WebMapTerrainExportStatus status, string phase, double percent, string? message = null, bool publish = true)
    {
        status.Phase = string.IsNullOrWhiteSpace(phase) ? "working" : phase;
        status.ProgressPercent = Math.Clamp(percent, 0d, 100d);
        if (!string.IsNullOrWhiteSpace(message)) status.Message = message!;
        if (publish) publishStatus(status.Clone());
    }

    private static void UpdateRenderProgress(WebMapTerrainExportStatus status, int completedTiles, int totalTiles)
    {
        if (!string.Equals(status.Phase, "rendering", StringComparison.OrdinalIgnoreCase)) return;
        if (totalTiles <= 0)
        {
            status.ProgressPercent = 90;
            return;
        }
        double ratio = Math.Clamp(completedTiles / (double)totalTiles, 0d, 1d);
        status.ProgressPercent = 5d + (ratio * 85d);
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
            status.EstimatedRemainingMilliseconds = totalTiles <= 0 ? 0 : -1;
            status.AverageMillisecondsPerTile = 0;
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (completedTiles > etaLastCompletedTiles)
        {
            int deltaTiles = completedTiles - etaLastCompletedTiles;
            long deltaMs = Math.Max(1, now - Math.Max(progressPhaseStartedUnixMilliseconds, etaLastSampleUnixMilliseconds));
            double perTile = deltaMs / (double)deltaTiles;

            // Ignore the first two tiles for ETA because they include JIT/cold filesystem and
            // chunk-deserializer warmup. Then use a rolling window so the estimate adapts as the
            // OS/file caches warm instead of anchoring to the slow beginning of the export.
            if (completedTiles > 2)
            {
                recentTileMilliseconds.Enqueue(perTile);
                while (recentTileMilliseconds.Count > 16) recentTileMilliseconds.Dequeue();
            }

            etaLastCompletedTiles = completedTiles;
            etaLastSampleUnixMilliseconds = now;
        }

        double average;
        if (recentTileMilliseconds.Count > 0)
        {
            average = recentTileMilliseconds.Average();
        }
        else
        {
            long elapsed = Math.Max(1, now - progressPhaseStartedUnixMilliseconds);
            average = elapsed / (double)completedTiles;
        }

        int remaining = Math.Max(0, totalTiles - completedTiles);
        status.AverageMillisecondsPerTile = average;
        status.EstimatedRemainingMilliseconds = remaining == 0 ? 0 : (long)Math.Ceiling(average * remaining);
    }

    private void LogConsoleProgress(string phase, int completedTiles, int totalTiles, long processedColumns, WebMapTerrainExportStatus status, bool force = false)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int percent = totalTiles <= 0 ? 100 : (int)Math.Clamp((long)completedTiles * 100L / totalTiles, 0L, 100L);
        bool finished = totalTiles > 0 && completedTiles >= totalTiles;

        // The Web Map progress bar consumes the same status object, but publishing every tile
        // would create needless lock/cache churn. About once per second is smooth in the UI
        // while remaining negligible next to terrain IO/rendering.
        if (force || finished || now - lastStatusProgressPublishUnixMilliseconds >= 1_000)
        {
            UpdateEta(status, completedTiles, totalTiles);
            publishStatus(status.Clone());
            lastStatusProgressPublishUnixMilliseconds = now;
        }

        bool percentStep = percent >= lastConsoleProgressPercent + 10;
        bool timeStep = now - lastConsoleProgressUnixMilliseconds >= 30_000;
        if (!force && !finished && !percentStep && !timeStep) return;

        UpdateEta(status, completedTiles, totalTiles);
        lastConsoleProgressUnixMilliseconds = now;
        lastConsoleProgressPercent = Math.Max(lastConsoleProgressPercent, percent);
        api.Logger.Notification(
            "[ServerEssentials/WebMap] terrain export progress ({0}): {1}% ({2:n0}/{3:n0} tiles), {4:n0} chunk columns processed, elapsed {5}, ETA {6}.",
            phase, percent, completedTiles, totalTiles, processedColumns, FormatElapsed(now - status.StartedUnixMilliseconds), FormatEta(status.EstimatedRemainingMilliseconds));
    }

    private void LogConsoleTileHeartbeat(TileCoord tile, long processedColumns, WebMapTerrainExportStatus status)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - lastConsoleProgressUnixMilliseconds < 30_000) return;

        lastConsoleProgressUnixMilliseconds = now;
        api.Logger.Notification(
            "[ServerEssentials/WebMap] terrain export still working: rendering tile ({0}, {1}), {2:n0} chunk columns processed, elapsed {3}, ETA {4}.",
            tile.X, tile.Z, processedColumns, FormatElapsed(now - status.StartedUnixMilliseconds), FormatEta(status.EstimatedRemainingMilliseconds));
    }

    private static string FormatEta(long estimatedRemainingMilliseconds)
        => estimatedRemainingMilliseconds < 0 ? "calculating" : FormatElapsed(estimatedRemainingMilliseconds);

    private static string FormatElapsed(long elapsedMilliseconds)
    {
        TimeSpan elapsed = TimeSpan.FromMilliseconds(Math.Max(0, elapsedMilliseconds));
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
    }

    private static class PngReader
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static bool TryReadRgba(string path, out int width, out int height, out byte[] rgba)
        {
            width = height = 0;
            rgba = Array.Empty<byte>();
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                Span<byte> signature = stackalloc byte[8];
                if (!ReadExactly(file, signature) || !signature.SequenceEqual(Signature.AsSpan())) return false;

                using var idat = new MemoryStream();
                bool validHeader = false;
                Span<byte> header = stackalloc byte[8];
                while (true)
                {
                    if (!ReadExactly(file, header)) return false;
                    int length = checked((int)ReadBe32(header[..4]));
                    string type = System.Text.Encoding.ASCII.GetString(header[4..8]);
                    if (length < 0 || length > 256 * 1024 * 1024) return false;

                    if (type == "IHDR")
                    {
                        if (length != 13) return false;
                        byte[] data = new byte[13];
                        if (!ReadExactly(file, data)) return false;
                        width = checked((int)ReadBe32(data.AsSpan(0, 4)));
                        height = checked((int)ReadBe32(data.AsSpan(4, 4)));
                        // Only the RGBA8, no-interlace format emitted by our own PngWriter is supported.
                        validHeader = width > 0 && height > 0 && data[8] == 8 && data[9] == 6 && data[10] == 0 && data[11] == 0 && data[12] == 0;
                    }
                    else if (type == "IDAT")
                    {
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(Math.Max(length, 1), 64 * 1024));
                        try
                        {
                            int remaining = length;
                            while (remaining > 0)
                            {
                                int take = Math.Min(remaining, buffer.Length);
                                int n = file.Read(buffer, 0, take);
                                if (n <= 0) return false;
                                idat.Write(buffer, 0, n);
                                remaining -= n;
                            }
                        }
                        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: false); }
                    }
                    else
                    {
                        file.Seek(length, SeekOrigin.Current);
                    }

                    file.Seek(4, SeekOrigin.Current); // CRC; files were generated locally and are trusted cache artifacts.
                    if (type == "IEND") break;
                }

                if (!validHeader || idat.Length == 0) return false;
                int rowBytes = checked(width * 4);
                byte[] output = new byte[checked(rowBytes * height)];
                idat.Position = 0;
                using var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true);
                for (int y = 0; y < height; y++)
                {
                    int filter = zlib.ReadByte();
                    if (filter != 0) return false;
                    if (!ReadExactly(zlib, output.AsSpan(y * rowBytes, rowBytes))) return false;
                }
                rgba = output;
                return true;
            }
            catch
            {
                width = height = 0;
                rgba = Array.Empty<byte>();
                return false;
            }
        }

        private static bool ReadExactly(Stream stream, Span<byte> buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int n = stream.Read(buffer[offset..]);
                if (n <= 0) return false;
                offset += n;
            }
            return true;
        }

        private static bool ReadExactly(Stream stream, byte[] buffer)
            => ReadExactly(stream, buffer.AsSpan());

        private static uint ReadBe32(ReadOnlySpan<byte> bytes)
            => ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static class PngWriter
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static readonly uint[] CrcTable = BuildCrcTable();

        public static void WriteRgba(string path, int width, int height, byte[] rgba)
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            file.Write(Signature, 0, Signature.Length);

            byte[] ihdr = new byte[13];
            WriteBe32(ihdr, 0, (uint)width);
            WriteBe32(ihdr, 4, (uint)height);
            ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            WriteChunk(file, "IHDR", ihdr);

            // Compress directly from the RGBA rows instead of constructing a second full raw image buffer.
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
            {
                int rowBytes = width * 4;
                for (int y = 0; y < height; y++)
                {
                    zlib.WriteByte(0); // PNG filter: None
                    zlib.Write(rgba, y * rowBytes, rowBytes);
                }
            }
            if (compressed.TryGetBuffer(out ArraySegment<byte> segment) && segment.Array != null)
                WriteChunk(file, "IDAT", segment.Array, segment.Offset, segment.Count);
            else
                WriteChunk(file, "IDAT", compressed.ToArray());
            WriteChunk(file, "IEND", Array.Empty<byte>());
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
            => WriteChunk(stream, type, data, 0, data.Length);

        private static void WriteChunk(Stream stream, string type, byte[] data, int offset, int count)
        {
            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            Span<byte> length = stackalloc byte[4];
            WriteBe32(length, (uint)count);
            stream.Write(length);
            stream.Write(typeBytes, 0, typeBytes.Length);
            if (count > 0) stream.Write(data, offset, count);

            uint crc = 0xffffffffu;
            foreach (byte b in typeBytes) crc = CrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
            int end = offset + count;
            for (int i = offset; i < end; i++) crc = CrcTable[(crc ^ data[i]) & 0xff] ^ (crc >> 8);
            crc ^= 0xffffffffu;
            Span<byte> crcBytes = stackalloc byte[4];
            WriteBe32(crcBytes, crc);
            stream.Write(crcBytes);
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < table.Length; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static void WriteBe32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24); buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8); buffer[offset + 3] = (byte)value;
        }

        private static void WriteBe32(Span<byte> buffer, uint value)
        {
            buffer[0] = (byte)(value >> 24); buffer[1] = (byte)(value >> 16);
            buffer[2] = (byte)(value >> 8); buffer[3] = (byte)value;
        }
    }
}

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
    public Dictionary<string, WebMapTerrainTileMetadata> Tiles { get; set; } = new(StringComparer.Ordinal);
}

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
    public long EstimatedRemainingMilliseconds { get; set; } = -1;
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

    public WebMapTerrainExportStatus Clone() => (WebMapTerrainExportStatus)MemberwiseClone();
}
