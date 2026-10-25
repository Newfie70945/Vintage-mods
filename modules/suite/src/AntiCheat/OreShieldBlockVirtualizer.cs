using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Packets;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace BlockLogger.AntiCheat;

/// <summary>
/// Produces a recipient-specific virtual client view of hidden geology. The authoritative world is never edited.
/// Every protected ore is concealed by default, including cave-exposed ore. A recipient receives the
/// real ore ID only after Ore Shield verifies server-side line of sight from that player's actual
/// character position. Legitimate discoveries are one-way for the lifetime of the current Ore Shield runtime.
/// </summary>
internal sealed class OreShieldBlockVirtualizer : IDisposable
{
    private readonly int chunkSize = GlobalConstants.ChunkSize;
    private readonly int chunkMask = GlobalConstants.ChunkSize - 1;
    private readonly int chunkVolume = GlobalConstants.ChunkSize * GlobalConstants.ChunkSize * GlobalConstants.ChunkSize;
    private const int ChunkBits = 5; // Vintage Story 1.22.7 uses 32^3 chunks.
    private const int CorrectionBatchSize = 64;
    // These limits bound discovery preparation as well as rays. Packet masking remains synchronous
    // and fail-closed; exhausting a discovery budget can only delay a legitimate reveal.
    private const int VisibilityPlayersPerTick = 8;
    private const int VisibilityChunksPerPlayerPass = 64;
    private const int VisibilityCandidatesPerPlayerPass = 1024;
    private const int VisibilityIndexBuildsPerTick = 2;
    private const int BoundaryRefreshesPerTick = 4;
    private const int MaxPendingBoundaryRefreshes = 4096;
    private const int ClientSyncTimeoutDelayMs = 9000;
    private const int RecentProtectedOreBreakWindowMs = 10000;
    private const string PlayerPlacedOreSaveKey = "newfies:oreshield-playerplaced-v1";
    private const int PlayerPlacedOreSaveVersion = 1;

    private readonly ICoreServerAPI api;
    private readonly ServerMain server;
    private readonly AntiCheatConfig config;
    private readonly OreShieldPalette palette;
    private readonly IServerNetworkChannel? oreShieldSyncChannel;
    private readonly System.Func<IServerPlayer, bool> supportsClientSync;
    private readonly object sync = new();
    private readonly ChunkDataPool dataPool;
    private readonly Action<int[], byte[], byte[], int> unpackBlocks;

    private readonly Dictionary<ChunkKey, LinkedListNode<CacheEntry>> cache = new();
    private readonly LinkedList<CacheEntry> lru = new();
    private readonly Dictionary<ChunkKey, long> revisions = new();
    private const int MaxRevisionEntries = 16384;
    private long revisionEpoch, nextRevision, revisionCompactions;
    private readonly Queue<BoundaryRefreshKey> pendingBoundaryRefreshes = new();
    private readonly HashSet<BoundaryRefreshKey> pendingBoundaryRefreshKeys = new();
    private readonly Dictionary<ChunkKey, OreIndexEntry> oreIndexCache = new();
    private readonly LinkedList<ChunkKey> oreIndexLru = new();
    private readonly Dictionary<ChunkKey, LinkedListNode<ChunkKey>> oreIndexLruNodes = new();
    // Discovery state is chunk-local so recipient chunk masking can answer "does this player know
    // anything in this chunk?" in O(1) instead of walking the player's entire runtime discovery history.
    private readonly Dictionary<string, Dictionary<ChunkKey, OreShieldDiscoveryCells>> playerDiscoveries = new(StringComparer.Ordinal);
    // Player-placed ore must never be virtualized as natural geology. These coordinates are
    // persisted with the save so a decorative/storage ore block does not turn into host rock
    // after reconnect, chunk reload or server restart.
    private readonly HashSet<WorldBlockKey> playerPlacedOreExemptions = new();
    private readonly Dictionary<ChunkKey, OreShieldDiscoveryCells> placedOreChunks = new();
    private readonly HashSet<ChunkKey> dirtyPlacedOreChunks = new(), persistedPlacedOreChunks = new();
    private const string PlacedOreManifestKey = "newfies:oreshield-playerplaced-v2";
    private bool migratingPlacedOre, placedManifestDirty, placedOrePersistenceFailed;
    private long placedOreCheckpoints, placedOreCheckpointBytes;
    private readonly Dictionary<ChunkKey, HashSet<string>> discoveryOwners = new();
    private long discoveryChunkRecords, discoveryInvalidationOwnersVisited;
    private readonly Dictionary<string, PlayerVisibilityState> playerVisibilityStates = new(StringComparer.Ordinal);
    private readonly Dictionary<ClientSyncPendingKey, PendingClientSync> pendingClientSync = new();
    private readonly Dictionary<string, int> clientSyncPendingByPlayer = new(StringComparer.Ordinal);
    private readonly Dictionary<ClientSyncRecipientChunk, int> clientSyncPendingByChunk = new();
    private readonly PriorityQueue<ClientSyncPendingKey, long> clientSyncDeadlines = new();
    private readonly Dictionary<ClientSyncOutboxKey, ClientSyncOutbox> clientSyncOutbox = new();
    private readonly Queue<ClientSyncOutboxKey> clientSyncRevealQueue = new(), clientSyncWorldQueue = new();
    private const int ClientSyncMaxPending = 1024, ClientSyncMaxPendingPerPlayer = 32;
    private const int ClientSyncMaxQueuedCells = 65536, ClientSyncMaxQueuedGroups = 1024;
    private int clientSyncQueuedCells;
    private long clientSyncClockMs, clientSyncLastPumpMs = Environment.TickCount64;
    private long clientSyncLastSendWarningMs, clientSyncSendFailures;
    private long clientSyncQueueCoalesces, clientSyncQueueOverflows, clientSyncChunkNotSentSkips, clientSyncStallDeferrals;
    private long clientSyncTimeoutSummaryCount, clientSyncTimeoutSummaryEntries, clientSyncLastTimeoutSummaryMs;
    private readonly Dictionary<string, long> clientSyncLastResyncRequestMs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> clientSyncLastFailureResyncMs = new(StringComparer.Ordinal);
    private readonly Dictionary<WorldBlockKey, long> recentProtectedOreBreaks = new();
    private long clientSyncSequence;
    private readonly long visibilityTickListenerId;
    private readonly long clientSyncTickListenerId;
    private long cacheBytes;
    private int nextVisibilityPlayer;
    private long visibilityChunksVisited;
    private long visibilityCandidatesEvaluated;
    private long visibilityIndexBuilds;
    private long visibilityBudgetDeferrals;
    private long discoveryCells;
    private readonly Dictionary<string, long> discoveryOfflineSinceMs = new(StringComparer.Ordinal);
    private long nextDiscoveryEvictionMs, discoveryEvictedPlayers, discoveryEvictedCells;
    private long boundaryRefreshCellsEvaluated;
    private long boundaryRefreshOverflowInvalidations;

    private long cacheHits;
    private long cacheMisses;
    private long chunkMaskHookCalls;
    private long chunkMaskUniquePackets;
    private long chunkMaskEligiblePackets;
    private long failClosedChunkPackets;
    private long failClosedCells;
    private long chunksInspected;
    private long chunksMasked;
    private long passThroughChunks;
    private long virtualizedBlocks;
    private long hiddenRealOreBlocks;
    private long rewrittenUpdatePackets;
    private long unsupportedCompressionPackets;
    private long oreIndexHits;
    private long oreIndexMisses;
    private long revisionInvalidations;
    private long miningRevealPulses;
    private long miningRevealCorrections;
    private long exposureSyncEvents;
    private long exposureBlocksEvaluated;
    private long exposureCorrectionsSent;
    private long veinDiscoveries;
    private long veinBlocksRevealed;
    private long revealCorrectionPackets;
    private long revealRealBlockPackets;
    private long reconcealBlockPackets;
    private long losPasses;
    private long losPlayersChecked;
    private long losCandidatesConsidered;
    private long losRayChecks;
    private long losDiscoveries;
    private long losMovementSkips;
    private long leakAuditChecks;
    private long leakAuditViolations;
    private long clientSyncBatchesSent;
    private long clientSyncEntriesSent;
    private long clientSyncAcks;
    private long clientSyncDeferredAcks;
    private long clientSyncAppliedEntries;
    private long clientSyncFailedEntries;
    private long clientSyncSkippedEntries;
    private long clientSyncApplyFailureBatches;
    private long clientSyncFailureResyncs;
    private long clientSyncTimeouts;
    private long clientSyncRetries;
    private long clientSyncResyncRequests;
    private long lastClientSyncFailureWarningMs;
    private long clientSyncFailureWarningsSuppressed;
    // Reveal-dispatch diagnostics. Discovery can be correct while delivery still fails; these
    // counters make the handoff observable instead of collapsing every early-return into zero.
    private long revealDispatchAttempts;
    private long revealRecipientResolveFailures;
    private long revealStaleKeySkips;
    private long revealClientSyncUnsupportedSkips;
    private readonly Dictionary<WorldBlockKey, bool> pendingWorldChanges = new();
    private readonly Queue<ChunkKey> pendingExposureBuilds = new();
    private readonly Dictionary<BoundaryRefreshKey, int> boundaryCursors = new();
    private readonly Dictionary<string, VeinJob> pendingVeins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResyncJob> pendingDiscoveryResyncs = new(StringComparer.Ordinal);
    private sealed class VeinJob
    {
        public readonly Queue<WorldBlockKey> Queue = new();
        public readonly HashSet<WorldBlockKey> Visited = new();
        public readonly Queue<(WorldBlockKey Position, int OreId)> Seeds = new();
        public readonly HashSet<WorldBlockKey> SeedKeys = new();
        public int Revealed;
        public long StartedAt = Environment.TickCount64;
        public long LastServed;
    }
    private sealed class ResyncJob
    {
        public required Queue<ChunkKey> Chunks;
        public ChunkKey Chunk;
        public IEnumerator<int>? Cells;
    }
    private readonly HashSet<WorldBlockKey> pendingAuthoritativeCells = new();
    private bool authoritativeFlushQueued;
    private long authoritativeCorrectionFlushes;
    private bool geometryChanged, exposureRefreshRequested;
    private Queue<ChunkKey>? pendingExposureRefreshChunks;
    private long worldChangeCoalesces, worldChangeOverflows, exposureBuildCells;
    private long visibilityStarted;
    private double visibilityLastMs, visibilityMaxMs;
    private bool VisibilityTimeExpired => Stopwatch.GetElapsedTime(visibilityStarted).TotalMilliseconds >= 4;
    private bool disposed;

    internal readonly record struct ChunkKey(int X, int Y, int Z);
    private readonly record struct WorldBlockKey(int X, int Y, int Z, int Dimension);
    private readonly record struct BoundaryRefreshKey(ChunkKey Chunk, int Face);
    private readonly record struct ClientSyncPendingKey(string PlayerUid, long Sequence);
    private readonly record struct ClientSyncRecipientChunk(string PlayerUid, ChunkKey Chunk);
    private readonly record struct ClientSyncOutboxKey(string PlayerUid, ChunkKey Chunk, bool Stabilize);
    private sealed class ClientSyncOutbox
    {
        public readonly Dictionary<int, BlockPos> Positions = new();
        public int RetryCount;
    }

    private sealed class OreIndexEntry
    {
        public required ulong SourceFingerprint { get; set; }
        public required HashSet<int> Indices { get; init; }
        // Only geometrically exposed candidates are considered by periodic LOS discovery. Buried
        // ores stay in Indices for masking, but no longer consume ray-test budget every pass.
        public required OreShieldExposedIndex ExposedIndices { get; init; }
        public int[]? PendingExposure;
        public int ExposureCursor;
    }

    private sealed class PlayerVisibilityState
    {
        public readonly Queue<WorldBlockKey> PendingCandidates = new();
        public double X;
        public double InternalY;
        public double Z;
        public float Yaw;
        public float Pitch;
        public int Dimension;
        public long LastScanMs;
        public bool Initialized;
        public OreShieldScanCursor Cursor { get; } = new();
    }


    private sealed class PendingClientSync
    {
        public required string PlayerUid { get; init; }
        public required long Sequence { get; init; }
        public required ChunkKey Chunk { get; init; }
        public required long Revision { get; init; }
        public required BlockPos[] Positions { get; init; }
        public int RetryCount { get; init; }
        public bool Stabilize { get; init; } = true;
        public int ExpectedEntries => Positions.Length;
    }
    private sealed class CacheEntry
    {
        public required ChunkKey Key { get; init; }
        public required long RevisionSignature { get; init; }
        public required ulong SourceFingerprint { get; init; }
        public byte[]? ClientBlocks { get; init; }
        public bool PassThrough { get; init; }
        public int NeighborDependencyMask { get; init; }
        public int NeighborAvailabilityMask { get; init; }
        public long OwnedBytes => ClientBlocks?.LongLength ?? 0;
    }

    public OreShieldBlockVirtualizer(ICoreServerAPI api, ServerMain server, AntiCheatConfig config, OreShieldPalette palette, IServerNetworkChannel? oreShieldSyncChannel = null, System.Func<IServerPlayer, bool>? supportsClientSync = null)
    {
        this.api = api;
        this.server = server;
        this.config = config;
        this.palette = palette;
        this.oreShieldSyncChannel = oreShieldSyncChannel;
        this.supportsClientSync = supportsClientSync ?? (_ => false);
        dataPool = new ChunkDataPool(chunkSize, server);

        var method = HarmonyLib.AccessTools.Method(typeof(ChunkData), "UnpackBlocksTo", new[]
        {
            typeof(int[]), typeof(byte[]), typeof(byte[]), typeof(int)
        }) ?? throw new MissingMethodException(typeof(ChunkData).FullName, "UnpackBlocksTo");
        unpackBlocks = method.CreateDelegate<Action<int[], byte[], byte[], int>>();
        LoadPlayerPlacedOreExemptions();
        api.Event.GameWorldSave += FlushPlacedOreOnWorldSave;
        visibilityTickListenerId = api.Event.RegisterGameTickListener(OnVisibilityTick, config.OreShieldVisibilityScanIntervalMs);
        clientSyncTickListenerId = api.Event.RegisterGameTickListener(_ => PumpClientSync(), 100);

    }

    public OreShieldWebSnapshot Snapshot(bool active, string status)
    {
        lock (sync)
        {
            return new OreShieldWebSnapshot
            {
                Enabled = config.OreShieldEnabled,
                Active = active,
                Status = status,
                ProtectedOreVariants = palette.ProtectedOreVariants,
                HostVariants = palette.HostVariants,
                SkippedOreCandidates = palette.SkippedOreCandidates,
                ProtectModdedOres = config.OreShieldProtectModdedOres,
                RevealOnUnknownNeighbor = config.OreShieldRevealOnUnknownNeighbor,
                StrictOreMatching = config.OreShieldStrictOreMatching,
                RevealRadiusBlocks = config.OreShieldRevealRadiusBlocks,
                RevealWholeVeinOnMine = config.OreShieldRevealWholeVeinOnMine,
                MaxVeinRevealBlocks = config.OreShieldMaxVeinRevealBlocks,
                DiscoveryRangeBlocks = config.OreShieldDiscoveryRangeBlocks,
                VisibilityScanIntervalMs = config.OreShieldVisibilityScanIntervalMs,
                MaxLosChecksPerPass = config.OreShieldMaxLosChecksPerPass,
                ViewConeDegrees = config.OreShieldViewConeDegrees,
                IdleRescanMs = config.OreShieldIdleRescanMs,
                LeakAuditEnabled = config.OreShieldLeakAuditEnabled,
                ChunkCacheEnabled = config.OreShieldChunkCacheEnabled,
                ChunkCacheMiB = config.OreShieldChunkCacheMiB,
                MaxCachedChunks = config.OreShieldMaxCachedChunks,
                AdditionalOrePatterns = new List<string>(config.OreShieldAdditionalOrePatterns ?? new()),
                ExcludedBlockPatterns = new List<string>(config.OreShieldExcludedBlockPatterns ?? new()),
                VisibilityLastMilliseconds = visibilityLastMs,
                VisibilityMaxMilliseconds = visibilityMaxMs,
                PendingWorldChanges = pendingWorldChanges.Count,
                PendingAuthoritativeCells = pendingAuthoritativeCells.Count,
                AuthoritativeCorrectionFlushes = authoritativeCorrectionFlushes,
                PlacedOreDirtyChunks = dirtyPlacedOreChunks.Count,
                PlacedOreCheckpoints = placedOreCheckpoints,
                PlacedOreCheckpointBytes = placedOreCheckpointBytes,
                WorldChangeCoalesces = worldChangeCoalesces,
                WorldChangeOverflows = worldChangeOverflows,
                ExposureBuildCells = exposureBuildCells,
                CacheEntries = cache.Count,
                CacheBytes = cacheBytes,
                CacheHits = cacheHits,
                CacheMisses = cacheMisses,
                ChunkMaskHookCalls = chunkMaskHookCalls,
                ChunkMaskUniquePackets = chunkMaskUniquePackets,
                ChunkMaskEligiblePackets = chunkMaskEligiblePackets,
                FailClosedChunkPackets = failClosedChunkPackets,
                FailClosedCells = failClosedCells,
                ChunksInspected = chunksInspected,
                ChunksMasked = chunksMasked,
                PassThroughChunks = passThroughChunks,
                VirtualizedBlocks = virtualizedBlocks,
                HiddenRealOreBlocks = hiddenRealOreBlocks,
                DecoyBlocks = 0,
                RewrittenUpdatePackets = rewrittenUpdatePackets,
                UnsupportedCompressionPackets = unsupportedCompressionPackets,
                OreIndexCacheEntries = oreIndexCache.Count,
                OreIndexCacheHits = oreIndexHits,
                OreIndexCacheMisses = oreIndexMisses,
                RevisionInvalidations = revisionInvalidations,
                MiningRevealPulses = miningRevealPulses,
                MiningRevealCorrections = miningRevealCorrections,
                ExposureSyncEvents = exposureSyncEvents,
                ExposureBlocksEvaluated = exposureBlocksEvaluated,
                ExposureCorrectionsSent = exposureCorrectionsSent,
                PendingVeinJobs = pendingVeins.Count,
                OldestVeinJobAgeMs = pendingVeins.Count == 0 ? 0 : Math.Max(0, Environment.TickCount64 - pendingVeins.Values.Min(j => j.StartedAt)),
                VeinLimitReachedJobs = veinBudgetLimitedJobs,
                VeinDiscoveries = veinDiscoveries,
                VeinBlocksRevealed = veinBlocksRevealed,
                // Legacy proximity fields remain in the API model for compatibility but v2 never runs
                // a background radius scanner. Keep them explicitly zero so diagnostics cannot imply otherwise.
                RevealRadiusPasses = 0,
                RevealPlayersChecked = 0,
                RevealDesiredBlocks = 0,
                RevealCorrectionPackets = revealCorrectionPackets,
                RevealRealBlockPackets = revealRealBlockPackets,
                ReconcealBlockPackets = reconcealBlockPackets,
                LosPasses = losPasses,
                LosPlayersChecked = losPlayersChecked,
                LosCandidatesConsidered = losCandidatesConsidered,
                LosRayChecks = losRayChecks,
                LosDiscoveries = losDiscoveries,
                LosMovementSkips = losMovementSkips,
                VisibilityChunksVisited = visibilityChunksVisited,
                VisibilityCandidatesEvaluated = visibilityCandidatesEvaluated,
                VisibilityIndexBuilds = visibilityIndexBuilds,
                VisibilityBudgetDeferrals = visibilityBudgetDeferrals,
                VisibilityPlayersPerTickLimit = VisibilityPlayersPerTick,
                VisibilityChunksPerPlayerPassLimit = VisibilityChunksPerPlayerPass,
                VisibilityCandidatesPerPlayerPassLimit = VisibilityCandidatesPerPlayerPass,
                VisibilityIndexBuildsPerTickLimit = VisibilityIndexBuildsPerTick,
                BoundaryRefreshesPending = pendingBoundaryRefreshes.Count,
                BoundaryRefreshCellsEvaluated = boundaryRefreshCellsEvaluated,
                BoundaryRefreshOverflowInvalidations = boundaryRefreshOverflowInvalidations,
                BoundaryRefreshesPerTickLimit = BoundaryRefreshesPerTick,
                ExposedIndexCandidates = oreIndexCache.Values.Sum(x => (long)x.ExposedIndices.Count),
                LeakAuditChecks = leakAuditChecks,
                LeakAuditViolations = leakAuditViolations,
                ClientSyncBatchesSent = clientSyncBatchesSent,
                ClientSyncEntriesSent = clientSyncEntriesSent,
                ClientSyncAcks = clientSyncAcks,
                ClientSyncDeferredAcks = clientSyncDeferredAcks,
                ClientSyncAppliedEntries = clientSyncAppliedEntries,
                ClientSyncFailedEntries = clientSyncFailedEntries,
                ClientSyncSkippedEntries = clientSyncSkippedEntries,
                ClientSyncApplyFailureBatches = clientSyncApplyFailureBatches,
                ClientSyncFailureResyncs = clientSyncFailureResyncs,
                ClientSyncFallbacks = 0, // legacy Web API field: native fallback retired in v3.2.8
                ClientSyncTimeouts = clientSyncTimeouts,
                ClientSyncRetries = clientSyncRetries,
                ClientSyncResyncRequests = clientSyncResyncRequests,
                ClientSyncPendingBatches = pendingClientSync.Count,
                ClientSyncQueuedCells = clientSyncQueuedCells,
                ClientSyncQueueCoalesces = clientSyncQueueCoalesces,
                ClientSyncQueueOverflows = clientSyncQueueOverflows,
                ClientSyncStallDeferrals = clientSyncStallDeferrals,
                DiscoveryPlayers = playerDiscoveries.Count,
                DiscoveryCells = discoveryCells,
                DiscoveryChunkRecords = discoveryChunkRecords,
                DiscoveryEvictedPlayers = discoveryEvictedPlayers,
                DiscoveryEvictedCells = discoveryEvictedCells,
                DiscoveryDistinctChunks = discoveryOwners.Count,
                DiscoveryInvalidationOwnersVisited = discoveryInvalidationOwnersVisited,
                RevisionEntries = revisions.Count,
                RevisionCompactions = revisionCompactions,
                SyncProtocolVersion = OreShieldSyncProtocol.Version,
                SyncCapability = OreShieldSyncProtocol.Capability,
                SyncBuildMarker = OreShieldSyncProtocol.BuildMarker,
                RevealDispatchAttempts = revealDispatchAttempts,
                RevealRecipientResolveFailures = revealRecipientResolveFailures,
                RevealChunkNotSentSkips = clientSyncChunkNotSentSkips,
                RevealStaleKeySkips = revealStaleKeySkips,
                RevealDirectPacketsSent = 0, // legacy field: native SetBlock retired
                RevealDirectPacketErrors = 0,
                RevealClientSyncUnsupportedSkips = revealClientSyncUnsupportedSkips
            };
        }
    }

    // Cache validity is driven by packet-content fingerprints plus targeted Ore Shield
    // revisions. Generic ServerChunk.MarkModified() signals are intentionally ignored because
    // block-entity/save dirtiness can fire millions of times without changing block IDs.

    public void RecordChunkHookInvocation()
    {
        lock (sync) chunkMaskHookCalls++;
    }

    public void RecordUniqueChunkPacket()
    {
        lock (sync) chunkMaskUniquePackets++;
    }

    internal Dictionary<string, Dictionary<ChunkKey, OreShieldDiscoveryCells>> ExportCompactDiscoveryState()
    {
        lock (sync) return playerDiscoveries.ToDictionary(p => p.Key,
            p => p.Value.ToDictionary(c => c.Key, c => c.Value.Clone()), StringComparer.Ordinal);
    }
    internal void ImportCompactDiscoveryState(Dictionary<string, Dictionary<ChunkKey, OreShieldDiscoveryCells>> state)
    {
        lock (sync)
            foreach (var (uid, records) in state)
            {
                var chunks = new Dictionary<ChunkKey, OreShieldDiscoveryCells>();
                foreach (var (key, cells) in records)
                {
                    chunks[key] = cells.Clone();
                    discoveryCells += cells.Count;
                    TrackDiscoveryOwnerNoLock(uid, key);
                }
                playerDiscoveries[uid] = chunks;
            }
    }

    public Dictionary<string, List<BlockPos>> ExportDiscoveryState()
    {
        var result = new Dictionary<string, List<BlockPos>>(StringComparer.Ordinal);
        lock (sync)
        {
            foreach ((string uid, Dictionary<ChunkKey, OreShieldDiscoveryCells> chunks) in playerDiscoveries)
            {
                var positions = new List<BlockPos>();
                foreach ((ChunkKey chunk, OreShieldDiscoveryCells indices) in chunks)
                {
                    foreach (int index in indices)
                    {
                        if ((uint)index >= (uint)chunkVolume) continue;
                        positions.Add(BlockPosFromChunkLocalIndex(chunk, index));
                    }
                }
                if (positions.Count > 0) result[uid] = positions;
            }
        }
        return result;
    }

    public void ImportDiscoveryState(Dictionary<string, List<BlockPos>>? state)
    {
        if (state == null || state.Count == 0) return;
        lock (sync)
        {
            foreach ((string uid, List<BlockPos> positions) in state)
            {
                if (string.IsNullOrWhiteSpace(uid) || positions == null || positions.Count == 0) continue;
                if (!playerDiscoveries.TryGetValue(uid, out Dictionary<ChunkKey, OreShieldDiscoveryCells>? chunks))
                    playerDiscoveries[uid] = chunks = new Dictionary<ChunkKey, OreShieldDiscoveryCells>();

                foreach (BlockPos pos in positions)
                {
                    if (pos == null) continue;
                    var key = new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension);
                    ChunkKey chunk = ChunkFor(key);
                    if (!chunks.TryGetValue(chunk, out OreShieldDiscoveryCells? indices))
                        {chunks[chunk] = indices = new OreShieldDiscoveryCells();TrackDiscoveryOwnerNoLock(uid,chunk);}
                    if (indices.Add(LocalIndex(key))) discoveryCells++;
                }
            }
        }
    }

    public void FailClosedChunkPacket(string? playerUid, ServerChunk sourceChunk, Packet_ServerChunk packet, string reason)
    {
        if (packet == null || sourceChunk == null) return;
        int[] compressionBuffer = ArrayPool<int>.Shared.Rent(ChunkDataLayer.DATASLICES * ChunkDataLayer.SLICESIZE);
        var output = new ChunkDataLayer(dataPool);
        try
        {
            output.PopulateWithAir();
            IWorldChunk authoritative = sourceChunk;
            var key = new ChunkKey(packet.X, packet.Y, packet.Z);
            OreShieldDiscoveryCells? discovered = GetDiscoveryIndicesSnapshot(playerUid, key);
            int protectedCells = 0;
            long failedCells = 0, auditChecks = 0, auditViolations = 0;

            for (int index = 0; index < chunkVolume; index++)
            {
                int actual = authoritative.UnpackAndReadBlock(index, BlockLayersAccess.Solid);
                int outgoing = actual;
                if (IsProtectedOreId(actual))
                {
                    BlockPos pos = BlockPosFromChunkLocalIndex(key, index);
                    if (!IsPlayerPlacedOre(pos) && (discovered == null || !discovered.Contains(index)))
                    {
                        protectedCells++;
                        if (!palette.TryGetHost(actual, out outgoing) || outgoing == actual)
                        {
                            outgoing = 0;
                            failedCells++;
                            if (config.OreShieldLeakAuditEnabled)
                            {
                                auditChecks++; auditViolations++;
                            }
                        }
                        else if (config.OreShieldLeakAuditEnabled)
                        {
                            auditChecks++;
                        }
                    }
                }
                output.SetUnsafe(index, outgoing);
            }

            byte[] safe = ChunkDataLayer.Compress(output, compressionBuffer);
            packet.Compver = 2;
            packet.Empty = 0;
            packet.SetBlocks(safe);
            lock (sync)
            {
                failClosedChunkPackets++;
                failClosedCells += failedCells;
                leakAuditChecks += auditChecks;
                leakAuditViolations += auditViolations;
                if (protectedCells > 0)
                {
                    chunksMasked++;
                    virtualizedBlocks += protectedCells;
                    hiddenRealOreBlocks += protectedCells;
                }
            }
        }
        finally
        {
            try { dataPool.FreeArrays(output); } catch { }
            ArrayPool<int>.Shared.Return(compressionBuffer, clearArray: false);
        }
    }

    public void MaskChunkForPlayer(IServerPlayer? player, ServerChunk sourceChunk, Packet_ServerChunk packet)
        => MaskChunkForPlayerUid(player?.PlayerUID, sourceChunk, packet);

    public void MaskChunkForPlayerUid(string? playerUid, ServerChunk sourceChunk, Packet_ServerChunk packet)
    {
        if (!config.OreShieldEnabled || packet.Empty != 0 || packet.Blocks == null) return;
        lock (sync) chunkMaskEligiblePackets++;
        if (packet.Compver != 2)
        {
            lock (sync) unsupportedCompressionPackets++;
            FailClosedChunkPacket(playerUid, sourceChunk, packet, $"unsupported compression v{packet.Compver}");
            return;
        }

        var key = new ChunkKey(packet.X, packet.Y, packet.Z);
        ulong sourceFingerprint = FingerprintBlocks(packet.Blocks);
        bool hasDiscoveries = HasAnyDiscoveryInChunk(playerUid, key);

        long ownRevision;
        lock (sync)
        {
            ownRevision = RevisionNoLock(key);
            // The cached payload is the fully-concealed baseline and is therefore safe only for a
            // recipient that has not legitimately discovered any protected ore in this chunk.
            if (!hasDiscoveries && config.OreShieldChunkCacheEnabled && cache.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
            {
                CacheEntry cached = node.Value;
                bool haveIndex = oreIndexCache.TryGetValue(key, out OreIndexEntry? cachedIndex) && cachedIndex.SourceFingerprint == sourceFingerprint;
                if (cached.SourceFingerprint == sourceFingerprint && cached.RevisionSignature == ownRevision && haveIndex)
                {
                    TouchNoLock(node);
                    TouchOreIndexNoLock(key);
                    cacheHits++;
                    oreIndexHits++;
                    if (!cached.PassThrough && cached.ClientBlocks != null) packet.SetBlocks(cached.ClientBlocks);
                    return;
                }
                // V3 visibility discovery depends on the protected-ore candidate index. If that
                // index was evicted, rebuild it from the authoritative packet instead of serving a
                // cached chunk that the LOS scanner could no longer reason about.
                RemoveNoLock(node);
            }
            cacheMisses++;
            chunksInspected++;
        }

        int[] source = ArrayPool<int>.Shared.Rent(chunkVolume);
        int[] compressionBuffer = ArrayPool<int>.Shared.Rent(ChunkDataLayer.DATASLICES * ChunkDataLayer.SLICESIZE);
        var output = new ChunkDataLayer(dataPool);

        try
        {
            byte[] original = packet.Blocks;
            unpackBlocks(source, original, packet.LightSat, packet.Compver);

            HashSet<int> candidateIndexes;
            bool haveOreIndex;
            lock (sync)
            {
                if (oreIndexCache.TryGetValue(key, out OreIndexEntry? existing) && existing.SourceFingerprint == sourceFingerprint)
                {
                    candidateIndexes = existing.Indices;
                    haveOreIndex = true;
                    TouchOreIndexNoLock(key);
                    oreIndexHits++;
                }
                else
                {
                    candidateIndexes = new HashSet<int>();
                    haveOreIndex = false;
                    oreIndexMisses++;
                }
            }

            if (!haveOreIndex)
            {
                candidateIndexes = new HashSet<int>();
                for (int i = 0; i < chunkVolume; i++)
                {
                    // The recipient hook supplies the untouched, server-generated packet.
                    // It is already unpacked above: do not acquire the chunk's lock 32,768
                    // times or mix a later live revision into an earlier packet snapshot.
                    int id = source[i];
                    if (!IsProtectedOreId(id)) continue;
                    var candidatePos = BlockPosFromChunkLocalIndex(key, i);
                    if (!IsPlayerPlacedOre(candidatePos)) candidateIndexes.Add(i);
                }

                OreShieldExposedIndex exposedIndexes = new();
                lock (sync)
                {
                    if (RevisionNoLock(key) == ownRevision)
                        SetOreIndexNoLock(key, sourceFingerprint, candidateIndexes, exposedIndexes);
                }
            }

            if (candidateIndexes.Count == 0)
            {
                if (!hasDiscoveries)
                {
                    lock (sync)
                    {
                        if (RevisionNoLock(key) == ownRevision)
                            AddCacheNoLock(key, ownRevision, sourceFingerprint, null, passThrough: true, neighborDependencyMask: 0, neighborAvailabilityMask: 0);
                        passThroughChunks++;
                    }
                }
                return;
            }

            output.PopulateWithAir();
            for (int i = 0; i < chunkVolume; i++) output.SetUnsafe(i, source[i]);

            OreShieldDiscoveryCells? discoveredInChunk = GetDiscoveryIndicesSnapshot(playerUid, key);
            int changed = 0;
            int hiddenOre = 0;
            long failedCells = 0, auditChecks = 0, auditViolations = 0;
            foreach (int index in candidateIndexes)
            {
                if ((uint)index >= (uint)chunkVolume) continue;
                int actual = source[index];
                if (!IsProtectedOreId(actual)) continue;

                var pos = BlockPosFromChunkLocalIndex(key, index);

                // Player-placed ore is never geology and must remain vanilla-visible to every
                // recipient, including after reconnect/chunk reload/server restart.
                if (IsPlayerPlacedOre(pos)) continue;

                // The canonical packet retains the server's ore for this recipient's discoveries.
                // Recipient copies and wrapper deduplication are owned by OreShieldService.
                if (discoveredInChunk != null && discoveredInChunk.Contains(index))
                {
                    continue;
                }

                if (!palette.TryGetHost(actual, out int host) || host == actual)
                {
                    // A protected natural ore without a safe host must never leak. Air is intentionally
                    // degraded but secure and will be replaced by normal data once the mapping is fixed.
                    output.SetUnsafe(index, 0);
                    failedCells++;
                    if (config.OreShieldLeakAuditEnabled)
                    {
                        auditChecks++; auditViolations++;
                    }
                    changed++;
                    hiddenOre++;
                    continue;
                }

                if (source[index] != host)
                {
                    output.SetUnsafe(index, host);
                    changed++;
                }
                if (config.OreShieldLeakAuditEnabled)
                {
                    auditChecks++;
                }
                hiddenOre++;
            }

            lock (sync)
            {
                failClosedCells += failedCells;
                leakAuditChecks += auditChecks;
                leakAuditViolations += auditViolations;
            }
            if (changed == 0) return;

            byte[] masked = ChunkDataLayer.Compress(output, compressionBuffer);
            packet.SetBlocks(masked);
            lock (sync)
            {
                // Per-player discovered chunks are intentionally not cached because their payload is
                // recipient-specific. The common fully-concealed baseline remains highly cacheable.
                if (!hasDiscoveries && RevisionNoLock(key) == ownRevision)
                    AddCacheNoLock(key, ownRevision, sourceFingerprint, masked, passThrough: false, neighborDependencyMask: 0, neighborAvailabilityMask: 0);

                chunksMasked++;
                virtualizedBlocks += changed;
                hiddenRealOreBlocks += hiddenOre;
            }
        }
        finally
        {
            try { dataPool.FreeArrays(output); } catch { }
            ArrayPool<int>.Shared.Return(source, clearArray: false);
            ArrayPool<int>.Shared.Return(compressionBuffer, clearArray: false);
        }

        ServerMain.FrameProfiler.Mark("newfies-oreshield-v3-chunks");
    }

    /// <summary>
    /// Returns true only when a single outgoing block update can affect Ore Shield's client view.
    /// Ordinary block/container placements should stay on Vintage Story's vanilla packet path.
    /// </summary>
    public bool IsProtectedOreIdForSafety(int blockId) => IsProtectedOreId(blockId);

    public bool FailClosedSingleIfProtected(int blockId, BlockPos position)
    {
        if (position == null || !IsProtectedOreId(blockId) || IsPlayerPlacedOre(position)) return false;
        return true;
    }

    public bool FailClosedBulkProtectedUpdates(List<BlockPos> positions)
    {
        if (positions == null || positions.Count == 0) return false;
        for (int i = positions.Count - 1; i >= 0; i--)
        {
            BlockPos? pos = positions[i];
            if (pos == null) continue;
            int actual = ReadWorldBlock(pos);
            if (actual >= 0 && IsMaskableProtectedOre(actual, pos)) positions.RemoveAt(i);
        }
        return positions.Count > 0;
    }

    public bool RequiresSingleVirtualization(int blockId, int x, int y, int z)
        => RequiresSingleVirtualization(blockId, new BlockPos(x, y, z));

    public bool RequiresSingleVirtualization(int blockId, BlockPos changed)
    {
        if (!config.OreShieldEnabled || changed == null) return false;
        if (IsMaskableProtectedOre(blockId, changed)) return true;

        var adjacent = changed.Copy();
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            adjacent.Set(changed);
            face.IterateThruFacingOffsets(adjacent);
            int adjacentId = ReadWorldBlock(adjacent);
            if (IsMaskableProtectedOre(adjacentId, adjacent)) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true only when a bulk block update contains protected ore or changes a block
    /// directly adjacent to protected ore. This preserves the complete vanilla bulk-update path
    /// for unrelated block entities/containers/backpacks and avoids unnecessary packet rewriting.
    /// </summary>
    public bool RequiresBulkVirtualization(List<BlockPos> positions)
    {
        if (!config.OreShieldEnabled || positions == null || positions.Count == 0) return false;

        BlockPos adjacent = positions[0].Copy();
        foreach (BlockPos changed in positions)
        {
            if (changed == null) continue;
            int changedId = ReadWorldBlock(changed);
            if (IsMaskableProtectedOre(changedId, changed)) return true;

            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                adjacent.Set(changed);
                face.IterateThruFacingOffsets(adjacent);
                int adjacentId = ReadWorldBlock(adjacent);
                if (IsMaskableProtectedOre(adjacentId, adjacent)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Removes only maskable natural-ore coordinates from a vanilla bulk update and sends those
    /// coordinates recipient-by-recipient through the OreShield correction dispatcher. All ordinary coordinates
    /// stay in the original list so Vintage Story performs its own serialization and relight mode.
    /// Returns true when the original SendSetBlocksPacket should continue.
    /// </summary>
    public bool FilterBulkProtectedUpdates(List<BlockPos> positions, int packetId)
    {
        if (!config.OreShieldEnabled || positions == null || positions.Count == 0) return true;

        var protectedPositions = new List<BlockPos>();
        for (int i = positions.Count - 1; i >= 0; i--)
        {
            BlockPos? pos = positions[i];
            if (pos == null) continue;
            int actual = ReadWorldBlock(pos);
            if (!IsMaskableProtectedOre(actual, pos)) continue;
            protectedPositions.Add(pos.Copy());
            positions.RemoveAt(i);
        }

        if (protectedPositions.Count == 0) return true;

        // Invalidate masking/index state for the actual ore coordinates, then send each client's
        // authorized view through the public IServerPlayer correction path. Client Integrity sync
        // is the sole recipient reveal transport.
        foreach (BlockPos pos in protectedPositions) InvalidateAround(pos);

        WorldBlockKey[] protectedKeys = protectedPositions
            .Select(pos => new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension))
            .ToArray();

        foreach (IServerPlayer player in OnlineServerPlayers())
        {
            SendPlayerViewCorrections(player, protectedKeys, reassertSmallSet: false);
            lock (sync) rewrittenUpdatePackets += protectedKeys.Length;
        }

        // If every position was protected ore, suppress the now-empty vanilla packet. Otherwise
        // let Vintage Story serialize the untouched normal positions using the original packet ID.
        return positions.Count > 0;
    }

    private bool IsProtectedOreId(int blockId)
        => blockId >= 0 && blockId < palette.ProtectedOre.Length && palette.ProtectedOre[blockId];

    private bool IsMaskableProtectedOre(int blockId, BlockPos position)
        => IsProtectedOreId(blockId) && !IsPlayerPlacedOre(position);

    private void RememberProtectedOreBreak(BlockPos position)
    {
        if (position == null) return;
        long now = Environment.TickCount64;
        var key = new WorldBlockKey(position.X, position.Y, position.Z, position.dimension);
        lock (sync)
        {
            recentProtectedOreBreaks[key] = now + RecentProtectedOreBreakWindowMs;
            if (recentProtectedOreBreaks.Count > 512)
            {
                foreach (WorldBlockKey stale in recentProtectedOreBreaks
                             .Where(pair => pair.Value < now)
                             .Select(pair => pair.Key)
                             .Take(256)
                             .ToArray())
                    recentProtectedOreBreaks.Remove(stale);
            }
        }
    }

    private bool ConsumeRecentProtectedOreBreak(BlockPos position)
    {
        if (position == null) return false;
        long now = Environment.TickCount64;
        var key = new WorldBlockKey(position.X, position.Y, position.Z, position.dimension);
        lock (sync)
        {
            if (!recentProtectedOreBreaks.TryGetValue(key, out long until)) return false;
            recentProtectedOreBreaks.Remove(key);
            return until >= now;
        }
    }

    private bool IsPlayerPlacedOre(BlockPos position)
    {
        if (position == null) return false;
        var key = new WorldBlockKey(position.X, position.Y, position.Z, position.dimension);
        lock (sync) return playerPlacedOreExemptions.Contains(key);
    }

    public bool IsPlayerPlacedOreExempt(BlockPos position) => IsPlayerPlacedOre(position);

    /// <summary>
    /// Called before the engine performs a player block placement. Pre-registering protected ore
    /// here ensures the very first outgoing SetBlock/SetBlocks packet is never virtualized to host
    /// rock. The caller must complete/rollback this provisional exemption after placement returns.
    /// </summary>
    public bool BeginPlayerOrePlacement(BlockPos position, int blockId)
    {
        if (!config.OreShieldEnabled || position == null || !IsProtectedOreId(blockId)) return false;
        bool added = MarkPlayerPlacedOreExemption(position, persist: false);
        InvalidateAround(position);
        return added;
    }

    public void CompletePlayerOrePlacement(BlockPos position, bool addedExemption, bool succeeded)
    {
        if (position == null || !config.OreShieldEnabled) return;
        int actual = ReadWorldBlock(position);
        if (succeeded && IsProtectedOreId(actual))
        {
            // Persist even if the coordinate was already provisionally present.
            MarkPlayerPlacedOreExemption(position, persist: true);
            InvalidateAround(position);
            return;
        }

        // Only roll back state created by this placement attempt. Existing player-placed ore
        // exemptions must survive a failed attempt to place something else into the same cell.
        if (addedExemption) RemovePlayerPlacedOreExemption(position, persist: false);
        InvalidateAround(position);
    }

    public int MaskSingle(IServerPlayer player, int blockId, int x, int y, int z)
        => MaskSingle(player, blockId, new BlockPos(x, y, z));

    public int MaskSingle(IServerPlayer player, int blockId, BlockPos changed)
    {
        if (!config.OreShieldEnabled || blockId < 0 || changed == null) return blockId;

        // Invalidate only Ore Shield's targeted packet revision. The ore index remains available
        // for exposure correction and is fingerprint-validated before it is ever reused for masking.
        InvalidateAround(changed);

        var corrections = new List<(BlockPos Pos, int BlockId)>(6);
        var adjacent = changed.Copy();
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            adjacent.Set(changed);
            face.IterateThruFacingOffsets(adjacent);
            int actual = ReadWorldBlock(adjacent);
            if (actual < 0 || actual >= palette.ProtectedOre.Length || !palette.ProtectedOre[actual]) continue;
            corrections.Add((adjacent.Copy(), ClientViewForPlayer(player, actual, adjacent)));
        }

        int result = ClientViewForPlayer(player, blockId, changed);
        if (config.OreShieldLeakAuditEnabled && IsMaskableProtectedOre(blockId, changed))
        {
            var wk = new WorldBlockKey(changed.X, changed.Y, changed.Z, changed.dimension);
            lock (sync)
            {
                leakAuditChecks++;
                if (!IsDiscovered(player.PlayerUID, wk) && result == blockId) leakAuditViolations++;
            }
        }
        if (corrections.Count > 0)
        {
            SendPlayerViewCorrections(
                player,
                corrections.Select(c => new WorldBlockKey(c.Pos.X, c.Pos.Y, c.Pos.Z, c.Pos.dimension)),
                reassertSmallSet: true);
        }

        return result;
    }

    public void OnColumnLoaded(ChunkColumnLoadRequest request, int dimension)
    {
        if (!config.OreShieldEnabled || server.ShuttingDown) return;
        int dimensionOffset = dimension * GlobalConstants.DimensionSizeInChunks;
        lock (sync)
        {
            for (int localY = 0; localY < server.WorldMap.ChunkMapSizeY; localY++)
            {
                int y = localY + dimensionOffset;
                QueueBoundaryRefreshNoLock(new ChunkKey(request.ChunkX - 1, y, request.ChunkZ), BlockFacing.EAST);
                QueueBoundaryRefreshNoLock(new ChunkKey(request.ChunkX + 1, y, request.ChunkZ), BlockFacing.WEST);
                QueueBoundaryRefreshNoLock(new ChunkKey(request.ChunkX, y, request.ChunkZ - 1), BlockFacing.SOUTH);
                QueueBoundaryRefreshNoLock(new ChunkKey(request.ChunkX, y, request.ChunkZ + 1), BlockFacing.NORTH);
            }
        }
    }

    private void QueueBoundaryRefreshNoLock(ChunkKey chunk, BlockFacing face)
    {
        if (!oreIndexCache.TryGetValue(chunk, out OreIndexEntry? entry) || entry.Indices.Count == 0) return;
        var key = new BoundaryRefreshKey(chunk, face.Index);
        if (pendingBoundaryRefreshKeys.Contains(key)) return;
        if (pendingBoundaryRefreshes.Count >= MaxPendingBoundaryRefreshes)
        {
            // Overflow must not leave a permanently stale exposure index. The normal bounded
            // scanner or the synchronous fail-closed packet path will rebuild the removed index.
            RemoveOreIndexNoLock(chunk);
            boundaryRefreshOverflowInvalidations++;
            return;
        }
        pendingBoundaryRefreshKeys.Add(key);
        pendingBoundaryRefreshes.Enqueue(key);
    }

    private void ProcessBoundaryRefreshes()
    {
        bool changed = false;
        for (int job = 0; job < BoundaryRefreshesPerTick; job++)
        {
            BoundaryRefreshKey key;
            lock (sync)
            {
                if (pendingBoundaryRefreshes.Count == 0) break;
                key = pendingBoundaryRefreshes.Dequeue();
                pendingBoundaryRefreshKeys.Remove(key);
            }
            changed |= RefreshBoundaryExposure(key.Chunk, BlockFacing.ALLFACES[key.Face]);
        }
        if (changed)
        {
            // New exposure is only a candidate. Reuse the shared LOS budget instead of doing
            // synchronous boundary ray tests for every player while installing a column.
            lock (sync)
                foreach (PlayerVisibilityState state in playerVisibilityStates.Values) state.LastScanMs = 0;
        }
    }

    private bool RefreshBoundaryExposure(ChunkKey key, BlockFacing face)
    {
        Span<int> candidates = stackalloc int[32 * 32];
        int count = 0;
        OreIndexEntry entry;
        lock (sync)
        {
            if (!oreIndexCache.TryGetValue(key, out entry!) || entry.Indices.Count == 0) return false;
            bool fixedX = face == BlockFacing.WEST || face == BlockFacing.EAST;
            int coordinate = face == BlockFacing.WEST || face == BlockFacing.NORTH ? 0 : chunkMask;
            count = OreShieldBoundaryWindow.CopyHorizontalFace(entry.Indices, fixedX, coordinate, candidates);
        }

        var workKey = new BoundaryRefreshKey(key, face.Index);
        int offset;
        lock (sync) offset = boundaryCursors.GetValueOrDefault(workKey);
        bool changed = false;
        int processed = 0;
        for (; offset < count && processed < 128 && !VisibilityTimeExpired; offset++, processed++)
        {
            int index = candidates[offset];
            BlockPos pos = BlockPosFromChunkLocalIndex(key, index);
            int actual = ReadWorldBlock(pos);
            bool exposed = IsMaskableProtectedOre(actual, pos) && IsWorldExposed(pos);
            lock (sync)
            {
                if (!oreIndexCache.TryGetValue(key, out OreIndexEntry? current) || !ReferenceEquals(current, entry)) continue;
                changed |= exposed ? current.ExposedIndices.Add(index) : current.ExposedIndices.Remove(index);
            }
        }
        lock (sync)
        {
            boundaryRefreshCellsEvaluated += processed;
            if (offset < count)
            {
                boundaryCursors[workKey] = offset;
                if (pendingBoundaryRefreshKeys.Add(workKey)) pendingBoundaryRefreshes.Enqueue(workKey);
            }
            else boundaryCursors.Remove(workKey);
        }
        return changed;
    }

    /// <summary>
    /// Post-break synchronization for Ore Shield v3.
    /// The public DidBreakBlock event fires after the authoritative block is already gone, so this is
    /// the reliable point to recompute which neighboring protected ore cells are now exposed.
    /// Exposure is global world state: every connected client that already has the ore chunk receives
    /// the correct exposed/concealed view. The breaking player may additionally receive the complete
    /// connected vein when whole-vein reveal is enabled.
    /// </summary>
    public void RevealAfterPlayerBreak(IServerPlayer player, BlockPos brokenPos, int oldBlockId)
    {
        if (!config.OreShieldEnabled || player == null || brokenPos == null) return;

        lock (sync) miningRevealPulses++;
        bool brokeProtectedOre = IsProtectedOreId(oldBlockId);
        if (brokeProtectedOre) RemovePlayerPlacedOreExemption(brokenPos, persist: true);
        InvalidateAround(brokenPos);

        // A revealed/concealed ore cell can still be inside the client's short stabilization
        // window when the player mines it. Without an explicit authoritative cancellation, that
        // stale pending cell sees the vanilla air update and writes the ore/host block back,
        // making the player break the same block twice. Supersede the stale cell with the server's
        // post-break state (normally air) using apply-once mode so we cancel stabilization without
        // pinning air for another 2.5 seconds. Only protected ore breaks need this extra packet.
        if (brokeProtectedOre)
        {
            RememberProtectedOreBreak(brokenPos);
            SyncAuthoritativeWorldCellToAllClients(brokenPos);
        }

        // The player who actually removed this block has authoritative proof of discovery for all
        // six face-adjacent protected ore cells. Read those six cells directly from the server world
        // instead of relying on the exposure index/affected list: stale index state must never stop
        // the miner from seeing ore they have just physically uncovered.
        List<WorldBlockKey> breakerDiscoveries = AuthorizeBreakerAdjacentDiscoveries(player, brokenPos);

        // Synchronize every recipient after the breaker authorization is recorded. The breaker now
        // receives real ore while other recipients keep their own discovered/hidden view. One
        // delayed reassert covers ordering races with the vanilla post-break block update.
        QueueWorldChange(brokenPos, true);
        if (breakerDiscoveries.Count > 0)
            SendPlayerViewCorrections(player, breakerDiscoveries, reassertSmallSet: true);

        // Other nearby players still need genuine server LOS before this world change can reveal
        // the ore to them. The breaker is already discovered and is skipped naturally here.
        geometryChanged = true;

        if (config.OreShieldRevealWholeVeinOnMine && brokeProtectedOre)
            RevealConnectedVeinAfterMine(player, brokenPos, oldBlockId);
    }

    /// <summary>
    /// Placement changes geometry but never revokes a legitimate discovery. Undiscovered protected
    /// ore remains concealed; newly visible ore is authorized only by server-side LOS.
    /// </summary>
    public void RevealAfterPlayerPlace(IServerPlayer player, BlockPos placedPos)
    {
        if (!config.OreShieldEnabled || placedPos == null) return;

        int placedId = ReadWorldBlock(placedPos);
        bool placedProtectedOre = IsProtectedOreId(placedId);
        bool followsRecentProtectedOreBreak = ConsumeRecentProtectedOreBreak(placedPos);
        if (placedProtectedOre)
            MarkPlayerPlacedOreExemption(placedPos, persist: true);
        else
            RemovePlayerPlacedOreExemption(placedPos, persist: true);

        InvalidateAround(placedPos);

        // If the player immediately places into a cell where protected ore was just mined, send
        // the new authoritative state after the break-cancellation packet. This closes the
        // break -> place ordering race without adding an extra OreShield packet to every normal
        // building action on the server.
        if (followsRecentProtectedOreBreak)
            SyncAuthoritativeWorldCellToAllClients(placedPos);
        QueueWorldChange(placedPos, placedProtectedOre);
        if (placedProtectedOre && player != null)
        {
            var placedKey = new WorldBlockKey(placedPos.X, placedPos.Y, placedPos.Z, placedPos.dimension);
            SendPlayerViewCorrections(player, new[] { placedKey }, reassertSmallSet: true);
        }

        // Player-placed ore itself is globally exempt rather than "discovered"; only natural ore
        // around the placement remains subject to LOS discovery.
        geometryChanged = true;
    }

    /// <summary>
    /// Non-player world changes (physics/explosions/mod systems) use the same strict discovery rule:
    /// update hidden/discovered views, then reveal only to recipients with genuine server LOS.
    /// </summary>
    public void SyncExposureAfterWorldChange(BlockPos changedPos)
    {
        if (!config.OreShieldEnabled || changedPos == null) return;
        InvalidateAround(changedPos);
        QueueWorldChange(changedPos, false);
    }

    private List<WorldBlockKey> AuthorizeBreakerAdjacentDiscoveries(IServerPlayer player, BlockPos brokenPos)
    {
        var discovered = new List<WorldBlockKey>(6);
        if (player == null || brokenPos == null) return discovered;

        string uid = player.PlayerUID ?? string.Empty;
        if (uid.Length == 0) return discovered;

        BlockPos adjacent = brokenPos.Copy();
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            adjacent.Set(brokenPos);
            face.IterateThruFacingOffsets(adjacent);
            int actual = ReadWorldBlock(adjacent);
            if (!IsMaskableProtectedOre(actual, adjacent)) continue;

            // The block at brokenPos is already gone when DidBreakBlock fires. Therefore this
            // adjacent ore face is unquestionably exposed by the player's own server-authorized
            // mining action. Always include it in the correction set even if it was already
            // discovered earlier: discovery state and the client's currently rendered block can
            // diverge after chunk reloads or packet-order races.
            var key = new WorldBlockKey(adjacent.X, adjacent.Y, adjacent.Z, adjacent.dimension);
            MarkDiscoveredWithoutLosCounter(uid, key);
            discovered.Add(key);
        }

        if (discovered.Count > 0)
        {
            lock (sync) miningRevealCorrections += discovered.Count;
        }
        return discovered;
    }

    private List<WorldBlockKey> CollectAffectedProtectedOre(BlockPos changedPos)
    {
        var result = new List<WorldBlockKey>(7);
        var seen = new HashSet<WorldBlockKey>();

        void Consider(BlockPos pos)
        {
            int actual = ReadWorldBlock(pos);
            if (!IsMaskableProtectedOre(actual, pos)) return;
            var key = new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension);
            if (seen.Add(key)) result.Add(key);
        }

        Consider(changedPos);
        BlockPos adjacent = changedPos.Copy();
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            adjacent.Set(changedPos);
            face.IterateThruFacingOffsets(adjacent);
            Consider(adjacent);
        }

        lock (sync)
        {
            exposureSyncEvents++;
            exposureBlocksEvaluated += result.Count;
        }
        return result;
    }

    private void SyncExposureToAllClients(IEnumerable<WorldBlockKey> keys, bool reassertSmallSet)
    {
        WorldBlockKey[] materialized = keys?.Distinct().ToArray() ?? Array.Empty<WorldBlockKey>();
        if (materialized.Length == 0) return;

        long sentBefore;
        lock (sync) sentBefore = revealCorrectionPackets;

        foreach (IServerPlayer player in OnlineServerPlayers())
        {
            SendPlayerViewCorrections(player, materialized, reassertSmallSet: reassertSmallSet);
        }

        lock (sync)
        {
            long delta = Math.Max(0, revealCorrectionPackets - sentBefore);
            exposureCorrectionsSent += delta;
        }
    }

    /// <summary>
    /// Sends the current authoritative block value for one world cell to every sync-ready client
    /// without a stabilization window. This is used to cancel stale pending virtual ore state when
    /// the authoritative world has just changed (most importantly, protected ore -> air on break).
    /// </summary>
    private void SyncAuthoritativeWorldCellToAllClients(BlockPos position)
    {
        if (disposed || position == null) return;
        lock (sync)
        {
            var key = new WorldBlockKey(position.X, position.Y, position.Z, position.dimension);
            if (pendingAuthoritativeCells.Count >= 4096 && !pendingAuthoritativeCells.Contains(key))
            { worldChangeOverflows++; return; }
            pendingAuthoritativeCells.Add(key);
            if (authoritativeFlushQueued) return;
            authoritativeFlushQueued = true;
        }
        // A short window combines adjacent breaks from vein-miner perks into chunk batches.
        api.Event.RegisterCallback(_ => FlushAuthoritativeCells(), 25);
    }
    private void FlushAuthoritativeCells()
    {
        List<BlockPos> positions;
        lock (sync)
        {
            authoritativeFlushQueued = false;
            if (disposed || pendingAuthoritativeCells.Count == 0) return;
            var keys = pendingAuthoritativeCells.Take(256).ToArray();
            foreach (var key in keys) pendingAuthoritativeCells.Remove(key);
            positions = keys.Select(key => new BlockPos(key.X, key.Y, key.Z, key.Dimension)).ToList();
            authoritativeCorrectionFlushes++;
        }
        foreach (var player in OnlineServerPlayers())
            TrySendClientSync(player, positions, retryCount: 0, stabilize: false);
        lock (sync)
        {
            if (pendingAuthoritativeCells.Count == 0 || disposed || authoritativeFlushQueued) return;
            authoritativeFlushQueued = true;
        }
        // A huge burst yields between cancellation batches; ordinary perk bursts use one batch.
        api.Event.RegisterCallback(_ => FlushAuthoritativeCells(), 10);
    }

    private void RevealConnectedVeinAfterMine(IServerPlayer player, BlockPos mined, int minedId)
    {
        string uid = player.PlayerUID;
        if (!pendingVeins.TryGetValue(uid, out var job))
        {
            if (pendingVeins.Count >= 64) { worldChangeOverflows++; return; }
            pendingVeins[uid] = job = new();
        }
        var key = new WorldBlockKey(mined.X, mined.Y, mined.Z, mined.dimension);
        // Vein Miner may break many cells in one native call chain. Defer the 26-cell
        // neighborhood reads as well as the traversal, rather than paying for each here.
        if (job.SeedKeys.Count < 64 && job.SeedKeys.Add(key)) job.Seeds.Enqueue((key, minedId));
    }
    private long veinServiceSequence;
    private long veinBudgetLimitedJobs;
    private void ProcessVeins()
    {
        if (!config.OreShieldRevealWholeVeinOnMine) { pendingVeins.Clear(); return; }
        if (pendingVeins.Count == 0 || VisibilityTimeExpired) return;
        var item = pendingVeins.MinBy(p => p.Value.LastServed); var job = item.Value;
        job.LastServed = ++veinServiceSequence;
        if (api.World.PlayerByUid(item.Key) is not IServerPlayer player)
        { pendingVeins.Remove(item.Key); return; }
        var revealed = new List<WorldBlockKey>();
        int max = Math.Clamp(config.OreShieldMaxVeinRevealBlocks, 64, 8192);
        for (int i = 0; i < 2 && job.Seeds.Count > 0 && job.Visited.Count < max && !VisibilityTimeExpired; i++)
        {
            var seed = job.Seeds.Dequeue();
            EnqueueTouchingProtectedOre(new BlockPos(seed.Position.X, seed.Position.Y, seed.Position.Z, seed.Position.Dimension),
                seed.OreId, job.Visited, job.Queue);
        }
        for (int i = 0; i < 64 && job.Queue.Count > 0 && job.Revealed < max && !VisibilityTimeExpired; i++)
        {
            var key = job.Queue.Dequeue();
            var pos = new BlockPos(key.X, key.Y, key.Z, key.Dimension);
            int actual = ReadWorldBlock(pos);
            if (!IsMaskableProtectedOre(actual, pos)) continue;
            job.Revealed++;
            if (MarkDiscoveredWithoutLosCounter(item.Key, key)) revealed.Add(key);
            if (job.Visited.Count < max)
                EnqueueTouchingProtectedOre(pos, actual, job.Visited, job.Queue);
        }
        if (revealed.Count > 0)
        {
            lock (sync) { veinBlocksRevealed += revealed.Count; miningRevealCorrections += revealed.Count; }
            SendPlayerViewCorrections(player, revealed, false);
        }
        pendingVeins.Remove(item.Key);
        if ((job.Queue.Count > 0 || job.Seeds.Count > 0 && job.Visited.Count < max) && job.Revealed < max) pendingVeins[item.Key] = job;
        else { lock (sync) { veinDiscoveries++; if (job.Revealed >= max || job.Visited.Count >= max) veinBudgetLimitedJobs++; } }
    }

    public void ResyncKnownDiscoveries(IServerPlayer player)
    {
        if (disposed || player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;
        lock (sync)
        {
            if (pendingDiscoveryResyncs.ContainsKey(player.PlayerUID) ||
                !playerDiscoveries.TryGetValue(player.PlayerUID, out var chunks) || chunks.Count == 0) return;
            pendingDiscoveryResyncs[player.PlayerUID] = new ResyncJob { Chunks = new Queue<ChunkKey>(chunks.Keys) };
        }
    }
    private void ProcessDiscoveryResyncs()
    {
        KeyValuePair<string, ResyncJob> item;
        lock (sync)
        { if (pendingDiscoveryResyncs.Count == 0 || VisibilityTimeExpired) return; item = pendingDiscoveryResyncs.First(); }
        var job = item.Value;
        if (api.World.PlayerByUid(item.Key) is not IServerPlayer player)
        { lock (sync) { pendingDiscoveryResyncs.Remove(item.Key); job.Cells?.Dispose(); } return; }
        var cells = new List<WorldBlockKey>(64);
        while (cells.Count < 64 && !VisibilityTimeExpired)
        {
            if (job.Cells == null)
            {
                lock (sync)
                {
                    if (job.Chunks.Count == 0) break;
                    job.Chunk = job.Chunks.Dequeue();
                    if (playerDiscoveries.TryGetValue(item.Key, out var records) && records.TryGetValue(job.Chunk, out var known))
                        job.Cells = known.Clone().GetEnumerator();
                }
                if (job.Cells == null) continue;
            }
            if (!job.Cells.MoveNext()) { job.Cells.Dispose(); job.Cells = null; continue; }
            var pos = BlockPosFromChunkLocalIndex(job.Chunk, job.Cells.Current);
            cells.Add(new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension));
        }
        if (cells.Count > 0) SendPlayerViewCorrections(player, cells, false);
        lock (sync)
        {
            pendingDiscoveryResyncs.Remove(item.Key);
            if (job.Cells != null || job.Chunks.Count > 0) pendingDiscoveryResyncs[item.Key] = job;
        }
    }

    public void RemovePlayerConnectionState(string? playerUid)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return;
        lock (sync)
        {
            // Discovery is intentionally preserved until the OreShield runtime stops. This makes a
            // legitimate one-way discovery survive disconnect/reconnect as well as chunk unload/reload.
            if (playerDiscoveries.ContainsKey(playerUid)) discoveryOfflineSinceMs[playerUid] = Environment.TickCount64;
            playerVisibilityStates.Remove(playerUid);
            pendingVeins.Remove(playerUid);
            if (pendingDiscoveryResyncs.Remove(playerUid, out var resyncJob)) resyncJob.Cells?.Dispose();
            clientSyncLastResyncRequestMs.Remove(playerUid);
            string failurePrefix = playerUid + "|";
            foreach (string key in clientSyncLastFailureResyncMs.Keys.Where(k => k.StartsWith(failurePrefix, StringComparison.Ordinal)).ToArray())
                clientSyncLastFailureResyncMs.Remove(key);
            foreach (ClientSyncPendingKey key in pendingClientSync.Keys.Where(k => string.Equals(k.PlayerUid, playerUid, StringComparison.Ordinal)).ToArray())
                RemovePendingClientSyncNoLock(key);
            RemoveQueuedClientSyncNoLock(playerUid);
        }
    }

    public void RemovePlayerState(string? playerUid)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return;
        lock (sync)
        {
            if (playerDiscoveries.Remove(playerUid, out Dictionary<ChunkKey, OreShieldDiscoveryCells>? removed))
                foreach (var (chunk,indices) in removed)
                {discoveryCells-=indices.Count;UntrackDiscoveryOwnerNoLock(playerUid,chunk);}
            discoveryOfflineSinceMs.Remove(playerUid);
            RemovePlayerConnectionState(playerUid);
        }
    }

    // Bounds retained discovery memory. Discoveries intentionally outlive a disconnect, so the only
    // growth control is releasing the longest-offline players once the configured total is exceeded.
    // Runs on the main thread from the visibility pass, at most every 30 seconds.
    private void EvictOfflineDiscoveriesIfNeeded()
    {
        int cap = config.OreShieldMaxDiscoveryCells;
        long now = Environment.TickCount64;
        if (cap <= 0 || now < nextDiscoveryEvictionMs) return;
        nextDiscoveryEvictionMs = now + 30_000;

        var online = new HashSet<string>(StringComparer.Ordinal);
        foreach (IPlayer player in api.World.AllOnlinePlayers)
            if (!string.IsNullOrEmpty(player?.PlayerUID)) online.Add(player.PlayerUID);

        lock (sync)
        {
            foreach (string uid in discoveryOfflineSinceMs.Keys.ToArray())
                if (online.Contains(uid) || !playerDiscoveries.ContainsKey(uid)) discoveryOfflineSinceMs.Remove(uid);
            foreach (string uid in playerDiscoveries.Keys)
                if (!online.Contains(uid)) discoveryOfflineSinceMs.TryAdd(uid, now);
            if (discoveryCells <= cap) return;

            foreach (KeyValuePair<string, long> offline in discoveryOfflineSinceMs.OrderBy(entry => entry.Value).ToArray())
            {
                if (discoveryCells <= cap) break;
                long before = discoveryCells;
                RemovePlayerState(offline.Key);
                discoveryEvictedPlayers++;
                discoveryEvictedCells += before - discoveryCells;
            }
        }
    }

    private void TrackPendingClientSyncNoLock(PendingClientSync pending)
    {
        pendingClientSync[new ClientSyncPendingKey(pending.PlayerUid, pending.Sequence)] = pending;
        clientSyncPendingByPlayer[pending.PlayerUid] = clientSyncPendingByPlayer.GetValueOrDefault(pending.PlayerUid) + 1;
        var chunk = new ClientSyncRecipientChunk(pending.PlayerUid, pending.Chunk);
        clientSyncPendingByChunk[chunk] = clientSyncPendingByChunk.GetValueOrDefault(chunk) + 1;
    }

    private void RemovePendingClientSyncNoLock(ClientSyncPendingKey key)
    {
        if (!pendingClientSync.Remove(key, out var pending)) return;
        int remaining = clientSyncPendingByPlayer.GetValueOrDefault(pending.PlayerUid) - 1;
        if (remaining <= 0) clientSyncPendingByPlayer.Remove(pending.PlayerUid); else clientSyncPendingByPlayer[pending.PlayerUid] = remaining;
        var chunk = new ClientSyncRecipientChunk(pending.PlayerUid, pending.Chunk);
        remaining = clientSyncPendingByChunk.GetValueOrDefault(chunk) - 1;
        if (remaining <= 0) clientSyncPendingByChunk.Remove(chunk); else clientSyncPendingByChunk[chunk] = remaining;
    }

    private void RemoveQueuedClientSyncNoLock(string playerUid)
    {
        foreach (var key in clientSyncOutbox.Keys.Where(k => k.PlayerUid == playerUid).ToArray())
        {
            clientSyncQueuedCells -= clientSyncOutbox[key].Positions.Count;
            clientSyncOutbox.Remove(key);
        }
        // Queue tokens and completed deadline entries are discarded by the bounded pump.
    }

    private void EnqueueTouchingProtectedOre(BlockPos center, int familyBlockId, HashSet<WorldBlockKey> visited, Queue<WorldBlockKey> queue)
    {
        BlockPos next = center.Copy();
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            next.Set(center.X + dx, center.Y + dy, center.Z + dz);
            var nextKey = new WorldBlockKey(next.X, next.Y, next.Z, next.dimension);
            if (visited.Count >= Math.Clamp(config.OreShieldMaxVeinRevealBlocks, 64, 8192)) break;
            if (visited.Contains(nextKey)) continue;
            int nextActual = ReadWorldBlock(next);
            if (!IsMaskableProtectedOre(nextActual, next) || !palette.SameVeinFamily(familyBlockId, nextActual)) continue;
            visited.Add(nextKey);
            queue.Enqueue(nextKey);
        }
    }

    private ChunkKey ChunkFor(WorldBlockKey key)
    {
        int internalY = key.Y + key.Dimension * BlockPos.DimensionBoundary;
        return new ChunkKey(key.X >> ChunkBits, internalY >> ChunkBits, key.Z >> ChunkBits);
    }

    private int LocalIndex(WorldBlockKey key)
    {
        int internalY = key.Y + key.Dimension * BlockPos.DimensionBoundary;
        return Index(key.X & chunkMask, internalY & chunkMask, key.Z & chunkMask);
    }

    private OreShieldDiscoveryCells? GetDiscoveryIndicesSnapshot(string? playerUid, ChunkKey chunk)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return null;
        lock (sync)
        {
            if (!playerDiscoveries.TryGetValue(playerUid, out Dictionary<ChunkKey, OreShieldDiscoveryCells>? chunks) ||
                !chunks.TryGetValue(chunk, out OreShieldDiscoveryCells? indices) || indices.Count == 0) return null;
            return indices.Clone();
        }
    }

    private bool IsDiscovered(string? playerUid, WorldBlockKey key)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return false;
        ChunkKey chunk = ChunkFor(key);
        int index = LocalIndex(key);
        lock (sync)
            return playerDiscoveries.TryGetValue(playerUid, out Dictionary<ChunkKey, OreShieldDiscoveryCells>? chunks) &&
                   chunks.TryGetValue(chunk, out OreShieldDiscoveryCells? indices) && indices.Contains(index);
    }

    private bool HasAnyDiscoveryInChunk(string? playerUid, ChunkKey chunk)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return false;
        lock (sync)
            return playerDiscoveries.TryGetValue(playerUid, out Dictionary<ChunkKey, OreShieldDiscoveryCells>? chunks) &&
                   chunks.TryGetValue(chunk, out OreShieldDiscoveryCells? indices) && indices.Count > 0;
    }

    private void TrackDiscoveryOwnerNoLock(string uid,ChunkKey chunk)
    {
        if(!discoveryOwners.TryGetValue(chunk,out var owners))discoveryOwners[chunk]=owners=new(StringComparer.Ordinal);
        if(owners.Add(uid))discoveryChunkRecords++;
    }
    private void UntrackDiscoveryOwnerNoLock(string uid,ChunkKey chunk)
    {
        if(!discoveryOwners.TryGetValue(chunk,out var owners)||!owners.Remove(uid))return;
        discoveryChunkRecords--;if(owners.Count==0)discoveryOwners.Remove(chunk);
    }

    private bool MarkDiscovered(IServerPlayer player, BlockPos pos)
    {
        string uid = player?.PlayerUID ?? string.Empty;
        if (uid.Length == 0) return false;
        var worldKey = new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension);
        ChunkKey chunk = ChunkFor(worldKey);
        int index = LocalIndex(worldKey);
        lock (sync)
        {
            if (!playerDiscoveries.TryGetValue(uid, out Dictionary<ChunkKey, OreShieldDiscoveryCells>? chunks))
                playerDiscoveries[uid] = chunks = new Dictionary<ChunkKey, OreShieldDiscoveryCells>();
            if (!chunks.TryGetValue(chunk, out OreShieldDiscoveryCells? indices))
                {chunks[chunk] = indices = new OreShieldDiscoveryCells();TrackDiscoveryOwnerNoLock(uid,chunk);}
            if (!indices.Add(index)) return false;
            discoveryCells++;
            losDiscoveries++;
            return true;
        }
    }

    private bool MarkDiscoveredWithoutLosCounter(string uid, WorldBlockKey key)
    {
        if (string.IsNullOrWhiteSpace(uid)) return false;
        ChunkKey chunk = ChunkFor(key);
        int index = LocalIndex(key);
        lock (sync)
        {
            if (!playerDiscoveries.TryGetValue(uid, out Dictionary<ChunkKey, OreShieldDiscoveryCells>? chunks))
                playerDiscoveries[uid] = chunks = new Dictionary<ChunkKey, OreShieldDiscoveryCells>();
            if (!chunks.TryGetValue(chunk, out OreShieldDiscoveryCells? indices))
                {chunks[chunk] = indices = new OreShieldDiscoveryCells();TrackDiscoveryOwnerNoLock(uid,chunk);}
            bool added = indices.Add(index);
            if (added) discoveryCells++;
            return added;
        }
    }

    private void QueueWorldChange(BlockPos pos, bool reassert)
    {
        var key = new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension);
        lock (sync)
        {
            geometryChanged = true;
            if (pendingWorldChanges.TryGetValue(key, out bool old))
            { pendingWorldChanges[key] = old || reassert; worldChangeCoalesces++; return; }
            if (pendingWorldChanges.Count >= 4096) { worldChangeOverflows++; exposureRefreshRequested = true; return; }
            pendingWorldChanges[key] = reassert;
        }
    }
    private void ProcessWorldChanges()
    {
        var affected = new HashSet<WorldBlockKey>();
        bool reassert = false;
        for (int i = 0; i < 16 && !VisibilityTimeExpired; i++)
        {
            KeyValuePair<WorldBlockKey, bool> change;
            lock (sync)
            {
                if (pendingWorldChanges.Count == 0) break;
                change = pendingWorldChanges.First(); pendingWorldChanges.Remove(change.Key);
            }
            var pos = new BlockPos(change.Key.X, change.Key.Y, change.Key.Z, change.Key.Dimension);
            RefreshExposureIndexAround(pos);
            foreach (var key in CollectAffectedProtectedOre(pos)) affected.Add(key);
            reassert |= change.Value;
        }
        if (affected.Count > 0) SyncExposureToAllClients(affected, reassert);
        lock (sync)
            if (geometryChanged)
            {
                foreach (var state in playerVisibilityStates.Values) state.LastScanMs = 0;
                geometryChanged = false;
            }
    }

    private void OnVisibilityTick(float dt)
    {
        if (disposed || !config.OreShieldEnabled) return;
        visibilityStarted = Stopwatch.GetTimestamp();
        try { RunVisibilityPass(); }
        finally
        {
            lock (sync)
            {
                visibilityLastMs = Stopwatch.GetElapsedTime(visibilityStarted).TotalMilliseconds;
                visibilityMaxMs = Math.Max(visibilityMaxMs, visibilityLastMs);
            }
        }
    }
    private void RunVisibilityPass()
    {
        lock (sync) losPasses++;
        EvictOfflineDiscoveriesIfNeeded();
        // Make progress before geometry/exposure work consumes the cooperative budget.
        ProcessVeins();
        FlushPlacedOreCheckpoints(2);
        ProcessWorldChanges();
        ProcessExposureBuilds();
        ProcessDiscoveryResyncs();
        if (!VisibilityTimeExpired) ProcessBoundaryRefreshes();

        IPlayer[] online = api.World.AllOnlinePlayers;
        if (online.Length == 0) { nextVisibilityPlayer = 0; return; }
        int configuredPerPlayer = Math.Clamp(config.OreShieldMaxLosChecksPerPass, 16, 512);
        var rayBudget = new OreShieldWorkBudget(Math.Min(configuredPerPlayer * 4, 2048));
        var indexBudget = new OreShieldWorkBudget(VisibilityIndexBuildsPerTick);
        int fairPerPlayer = Math.Min(configuredPerPlayer, Math.Max(8, rayBudget.Remaining / Math.Min(online.Length, VisibilityPlayersPerTick)));
        int processed = 0;
        int visited = 0;
        int start = nextVisibilityPlayer % online.Length;
        while (visited < online.Length && processed < VisibilityPlayersPerTick && rayBudget.Remaining > 0 && !VisibilityTimeExpired)
        {
            int index = (start + visited++) % online.Length;
            nextVisibilityPlayer = (index + 1) % online.Length;
            if (online[index] is not IServerPlayer player || player.Entity == null) continue;
            if (player.ConnectionState != EnumClientState.Connected && player.ConnectionState != EnumClientState.Playing) continue;
            EntityPos? entityPos = EntityRuntimeCompat.GetServerPos(player.Entity) ?? EntityRuntimeCompat.GetPos(player.Entity);
            if (entityPos == null || !ShouldRunVisibilityScan(player, entityPos)) continue;
            processed++;
            DiscoverVisibleOreForPlayer(player, entityPos, fairPerPlayer, rayBudget, indexBudget);
        }
        lock (sync)
        {
            visibilityIndexBuilds += indexBudget.Used;
            if (visited < online.Length) visibilityBudgetDeferrals++;
        }
    }

    private bool ShouldRunVisibilityScan(IServerPlayer player, EntityPos entityPos)
    {
        string uid = player.PlayerUID ?? string.Empty;
        if (uid.Length == 0) return false;
        long now = Environment.TickCount64;
        const double moveThresholdSq = 0.25 * 0.25;
        float turnThreshold = (float)(5.0 * Math.PI / 180.0);
        int idleRescanMs = Math.Clamp(config.OreShieldIdleRescanMs, 500, 15000);

        lock (sync)
        {
            if (!playerVisibilityStates.TryGetValue(uid, out PlayerVisibilityState? state))
                playerVisibilityStates[uid] = state = new PlayerVisibilityState();

            double dx = entityPos.X - state.X;
            double dy = entityPos.InternalY - state.InternalY;
            double dz = entityPos.Z - state.Z;
            bool moved = !state.Initialized || state.Dimension != entityPos.Dimension || dx * dx + dy * dy + dz * dz >= moveThresholdSq;
            bool turned = !state.Initialized || AngleDelta(entityPos.Yaw, state.Yaw) >= turnThreshold || AngleDelta(entityPos.Pitch, state.Pitch) >= turnThreshold;
            bool idleDue = !state.Initialized || now - state.LastScanMs >= idleRescanMs;
            if (state.PendingCandidates.Count == 0 && !state.Cursor.HasPendingSweep && !moved && !turned && !idleDue)
            {
                losMovementSkips++;
                return false;
            }

            state.X = entityPos.X;
            state.InternalY = entityPos.InternalY;
            state.Z = entityPos.Z;
            state.Yaw = entityPos.Yaw;
            state.Pitch = entityPos.Pitch;
            state.Dimension = entityPos.Dimension;
            state.LastScanMs = now;
            state.Initialized = true;
            return true;
        }
    }

    private static float AngleDelta(float a, float b)
    {
        float twoPi = (float)(Math.PI * 2.0);
        float pi = (float)Math.PI;
        float delta = Math.Abs(a - b) % twoPi;
        return delta > pi ? twoPi - delta : delta;
    }

    private void DiscoverVisibleOreForPlayer(IServerPlayer player, EntityPos entityPos, int maxChecks,
        OreShieldWorkBudget rayBudget, OreShieldWorkBudget indexBudget)
    {
        string uid = player.PlayerUID ?? string.Empty;
        if (uid.Length == 0) return;
        PlayerVisibilityState state;
        lock (sync)
        {
            losPlayersChecked++;
            if (!playerVisibilityStates.TryGetValue(uid, out state!)) return;
        }
        Vec3d eyeOffset = player.Entity.LocalEyePos;
        var eye = new Vec3d(entityPos.X + eyeOffset.X, entityPos.InternalY + eyeOffset.Y, entityPos.Z + eyeOffset.Z);
        int range = Math.Clamp(config.OreShieldDiscoveryRangeBlocks, 16, 192);
        maxChecks = Math.Min(maxChecks, rayBudget.Remaining);
        double rangeSq = (double)range * range;
        int chunkRadius = (range + chunkSize - 1) / chunkSize;
        int centerChunkX = (int)Math.Floor(entityPos.X / chunkSize);
        int centerChunkZ = (int)Math.Floor(entityPos.Z / chunkSize);
        int centerInternalY = ((int)Math.Floor(entityPos.InternalY)) >> ChunkBits;
        int dimChunkOffset = entityPos.Dimension * GlobalConstants.DimensionSizeInChunks;
        OreShieldScanCursor cursor = state.Cursor;
        cursor.Configure(centerChunkX, centerInternalY, centerChunkZ, chunkRadius, entityPos.Dimension);
        cursor.BeginSweep();

        var candidates = new List<(double D2, WorldBlockKey Key)>(maxChecks);
        Span<int> exposed = stackalloc int[128];
        Span<bool> alreadyDiscovered = stackalloc bool[128];
        int chunksVisited = 0;
        int evaluated = 0;
        while (state.PendingCandidates.Count == 0 && !VisibilityTimeExpired && cursor.HasPendingSweep && chunksVisited < VisibilityChunksPerPlayerPass &&
            evaluated < VisibilityCandidatesPerPlayerPass && candidates.Count < maxChecks)
        {
            OreShieldScanCursor.Offset offset = cursor.Current;
            int cx = centerChunkX + offset.X, cy = centerInternalY + offset.Y, cz = centerChunkZ + offset.Z;
            chunksVisited++;
            if (cy < dimChunkOffset || cy >= dimChunkOffset + server.WorldMap.ChunkMapSizeY)
            {
                cursor.AdvanceChunk();
                continue;
            }
            var ck = new ChunkKey(cx, cy, cz);
            // Do not advance an unindexed chunk when the shared build budget is exhausted. It
            // resumes on a later tick, rather than repeatedly rebuilding whole radius scans.
            if (!EnsureOreIndexForLoadedChunk(ck, indexBudget)) break;
            int take = Math.Min(exposed.Length, VisibilityCandidatesPerPlayerPass - evaluated);
            int count, nextIndex;
            lock (sync)
            {
                if (!oreIndexCache.TryGetValue(ck, out OreIndexEntry? entry))
                {
                    cursor.AdvanceChunk();
                    continue;
                }
                TouchOreIndexNoLock(ck);
                count = entry.ExposedIndices.CopyWindow(cursor.LocalIndex, exposed[..take], out nextIndex);
                OreShieldDiscoveryCells? known = null;
                if (playerDiscoveries.TryGetValue(uid, out Dictionary<ChunkKey, OreShieldDiscoveryCells>? chunks))
                    chunks.TryGetValue(ck, out known);
                for (int i = 0; i < count; i++) alreadyDiscovered[i] = known?.Contains(exposed[i]) == true;
            }
            int consumed = 0;
            for (; consumed < count && candidates.Count < maxChecks; consumed++)
            {
                int index = exposed[consumed];
                cursor.LocalIndex = index + 1;
                evaluated++;
                if (alreadyDiscovered[consumed]) continue;
                BlockPos bp = BlockPosFromChunkLocalIndex(ck, index);
                double dx = bp.X + 0.5 - eye.X, dy = bp.InternalY + 0.5 - eye.Y, dz = bp.Z + 0.5 - eye.Z;
                double d2 = dx * dx + dy * dy + dz * dz;
                if (d2 > rangeSq || !IsWithinViewCone(entityPos, eye, bp)) continue;
                candidates.Add((d2, new WorldBlockKey(bp.X, bp.Y, bp.Z, bp.dimension)));
            }
            if (consumed == count)
            {
                cursor.LocalIndex = nextIndex;
                if (nextIndex >= chunkVolume) cursor.AdvanceChunk();
            }
        }

        // Keep prepared candidates when a native ray consumes the remaining time budget.
        // They resume next pass instead of disappearing after the chunk cursor advanced.
        candidates.Sort((a, b) => a.D2.CompareTo(b.D2));
        foreach (var candidate in candidates) state.PendingCandidates.Enqueue(candidate.Key);
        var newlyVisible = new List<WorldBlockKey>();
        int checks = 0;
        while (state.PendingCandidates.Count > 0 && checks < maxChecks && !VisibilityTimeExpired)
        {
            WorldBlockKey key = state.PendingCandidates.Dequeue();
            if (IsDiscovered(uid, key) || key.Dimension != entityPos.Dimension) continue;
            var pos = new BlockPos(key.X, key.Y, key.Z, key.Dimension);
            double dx = pos.X + 0.5 - eye.X, dy = pos.InternalY + 0.5 - eye.Y, dz = pos.Z + 0.5 - eye.Z;
            if (dx * dx + dy * dy + dz * dz > rangeSq || !IsWithinViewCone(entityPos, eye, pos)) continue;
            int actual = ReadWorldBlock(pos);
            if (!IsMaskableProtectedOre(actual, pos) || !IsWorldExposed(pos)) continue;
            if (!rayBudget.TryTake()) { state.PendingCandidates.Enqueue(key); break; }
            checks++;
            if (!HasServerLineOfSight(player, eye, pos)) continue;
            if (MarkDiscovered(player, pos)) newlyVisible.Add(key);
        }
        lock (sync)
        {
            visibilityChunksVisited += chunksVisited;
            visibilityCandidatesEvaluated += evaluated;
            losCandidatesConsidered += checks;
            losRayChecks += checks;
            if (cursor.HasPendingSweep || state.PendingCandidates.Count > 0) visibilityBudgetDeferrals++;
        }
        if (newlyVisible.Count > 0)
            SendPlayerViewCorrections(player, newlyVisible, reassertSmallSet: false);
    }

    private bool IsWithinViewCone(EntityPos entityPos, Vec3d eye, BlockPos target)
    {
        int cone = Math.Clamp(config.OreShieldViewConeDegrees, 90, 180);
        // 180 degrees means the forward hemisphere (half-angle 90°, min dot 0), not
        // "disable the cone". The old shortcut effectively turned 180 into 360-degree discovery.
        double dx = target.X + 0.5 - eye.X;
        double dy = target.InternalY + 0.5 - eye.Y;
        double dz = target.Z + 0.5 - eye.Z;
        double lenSq = dx * dx + dy * dy + dz * dz;
        if (lenSq <= 0.0001) return true;
        double invLen = 1.0 / Math.Sqrt(lenSq);
        double minDot = Math.Cos((cone * 0.5) * Math.PI / 180.0);

        static double Dot(Vec3f view, double x, double y, double z, double inv)
            => (view.X * x + view.Y * y + view.Z * z) * inv;

        // Player head angles and body angles are synchronized through slightly different engine
        // paths. Accept either representation (plus body+head for runtimes that expose head yaw as
        // an offset) and still require the authoritative world ray below. This prevents false
        // negatives from angle-representation differences without revealing through solid blocks.
        Vec3f bodyView = EntityPos.GetViewVector(entityPos.Pitch, entityPos.Yaw);
        if (Dot(bodyView, dx, dy, dz, invLen) >= minDot) return true;

        Vec3f headView = EntityPos.GetViewVector(entityPos.HeadPitch, entityPos.HeadYaw);
        if (Dot(headView, dx, dy, dz, invLen) >= minDot) return true;

        Vec3f combinedView = EntityPos.GetViewVector(entityPos.Pitch + entityPos.HeadPitch, entityPos.Yaw + entityPos.HeadYaw);
        return Dot(combinedView, dx, dy, dz, invLen) >= minDot;
    }

    private bool EnsureOreIndexForLoadedChunk(ChunkKey key, OreShieldWorkBudget budget)
    {
        lock (sync)
        {
            if (oreIndexCache.ContainsKey(key)) return true;
        }

        ServerChunk? chunk = server.GetLoadedChunk(ChunkIndex3D(key));
        if (chunk == null || chunk.Disposed) return true;
        if (!budget.TryTake()) return false;
        long revision;
        lock (sync) revision = RevisionNoLock(key);
        try { chunk.Unpack(); } catch { return true; }
        IChunkBlocks? data = chunk.Data;
        if (data == null) return true;

        var indices = new HashSet<int>();
        for (int i = 0; i < chunkVolume; i++)
        {
            int id = data.GetBlockIdUnsafe(i);
            if (!IsProtectedOreId(id)) continue;
            int ly = i / (chunkSize * chunkSize);
            int rem = i - ly * chunkSize * chunkSize;
            int lz = rem / chunkSize;
            int lx = rem - lz * chunkSize;
            var pos = BlockPosFromChunkLocalIndex(key, i);
            if (!IsPlayerPlacedOre(pos)) indices.Add(i);
        }

        OreShieldExposedIndex exposed = new();
        lock (sync)
        {
            if (!oreIndexCache.ContainsKey(key) && RevisionNoLock(key) == revision)
                SetOreIndexNoLock(key, 0, indices, exposed);
        }
        return true;
    }

    private void ProcessExposureBuilds()
    {
        lock (sync)
        {
            if (pendingExposureBuilds.Count >= Math.Max(512, config.OreShieldMaxCachedChunks * 2))
            {
                pendingExposureBuilds.Clear();
                foreach (var pair in oreIndexCache) if (pair.Value.PendingExposure != null) pendingExposureBuilds.Enqueue(pair.Key);
            }
            if (exposureRefreshRequested && pendingExposureRefreshChunks == null)
            { pendingExposureRefreshChunks = new Queue<ChunkKey>(oreIndexCache.Keys); exposureRefreshRequested = false; }
            if (pendingExposureRefreshChunks?.Count > 0)
            {
                var key = pendingExposureRefreshChunks.Dequeue();
                if (oreIndexCache.TryGetValue(key, out var entry))
                { entry.PendingExposure = entry.Indices.ToArray(); entry.ExposureCursor = 0; pendingExposureBuilds.Enqueue(key); }
            }
            if (pendingExposureRefreshChunks?.Count == 0) pendingExposureRefreshChunks = null;
        }
        int evaluated = 0;
        while (evaluated < 256 && Stopwatch.GetElapsedTime(visibilityStarted).TotalMilliseconds < 2)
        {
            ChunkKey key;
            OreIndexEntry? entry;
            int index;
            lock (sync)
            {
                if (pendingExposureBuilds.Count == 0) break;
                key = pendingExposureBuilds.Peek();
                if (!oreIndexCache.TryGetValue(key, out entry) || entry.PendingExposure == null)
                { pendingExposureBuilds.Dequeue(); continue; }
                if (entry.ExposureCursor >= entry.PendingExposure.Length)
                { entry.PendingExposure = null; pendingExposureBuilds.Dequeue(); continue; }
                index = entry.PendingExposure[entry.ExposureCursor++];
            }
            var pos = BlockPosFromChunkLocalIndex(key, index);
            bool exposed = IsMaskableProtectedOre(ReadWorldBlock(pos), pos) && IsWorldExposed(pos);
            lock (sync)
                if (oreIndexCache.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    if (exposed) current.ExposedIndices.Add(index); else current.ExposedIndices.Remove(index);
                    exposureBuildCells++;
                    if (exposed) geometryChanged = true;
                    // Round-robin unfinished dense chunks instead of starving later recipients.
                    if (current.ExposureCursor % 64 == 0 && pendingExposureBuilds.Count > 1)
                    { pendingExposureBuilds.Dequeue(); pendingExposureBuilds.Enqueue(key); }
                }
            evaluated++;
        }
    }

    private void RefreshExposureIndexAround(BlockPos changedPos)
    {
        if (changedPos == null) return;
        var positions = new List<BlockPos>(7) { changedPos.Copy() };
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            BlockPos p = changedPos.Copy();
            face.IterateThruFacingOffsets(p);
            positions.Add(p);
        }

        foreach (BlockPos pos in positions)
        {
            var ck = new ChunkKey(pos.X >> ChunkBits, pos.InternalY >> ChunkBits, pos.Z >> ChunkBits);
            int localIndex = Index(pos.X & chunkMask, pos.InternalY & chunkMask, pos.Z & chunkMask);
            int actual = ReadWorldBlock(pos);
            bool isProtected = IsMaskableProtectedOre(actual, pos);
            bool isExposed = isProtected && IsWorldExposed(pos);
            lock (sync)
            {
                if (!oreIndexCache.TryGetValue(ck, out OreIndexEntry? entry)) continue;
                if (isProtected) entry.Indices.Add(localIndex); else entry.Indices.Remove(localIndex);
                if (isExposed) entry.ExposedIndices.Add(localIndex); else entry.ExposedIndices.Remove(localIndex);
            }
        }
    }

    private bool HasServerLineOfSight(IServerPlayer player, Vec3d eye, BlockPos target)
    {
        EntityPos? entityPos = EntityRuntimeCompat.GetPos(player.Entity);
        if (entityPos == null || entityPos.Dimension != target.dimension) return false;

        bool RayHitsTarget(Vec3d to)
        {
            BlockSelection? blockSel = null;
            EntitySelection? entitySel = null;
            api.World.RayTraceForSelection(
                eye,
                to,
                ref blockSel,
                ref entitySel,
                null,
                _ => false);
            BlockPos? hit = blockSel?.Position;
            return hit != null && hit.X == target.X && hit.Y == target.Y && hit.Z == target.Z && hit.dimension == target.dimension;
        }

        // Center first, then exposed face centers. Face samples avoid false negatives at shallow
        // viewing angles where a center ray can clip a neighboring solid block even though a
        // legitimate portion of the ore face is visible to the player.
        if (RayHitsTarget(new Vec3d(target.X + 0.5, target.InternalY + 0.5, target.Z + 0.5))) return true;

        BlockPos adjacent = target.Copy();
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            adjacent.Set(target.X, target.Y, target.Z).SetDimension(target.dimension);
            face.IterateThruFacingOffsets(adjacent);
            int neighbor = ReadWorldBlock(adjacent);
            if (neighbor < 0 || palette.IsOpaque(neighbor)) continue;

            Vec3f n = face.Normalf;
            var faceCenter = new Vec3d(
                target.X + 0.5 + n.X * 0.499,
                target.InternalY + 0.5 + n.Y * 0.499,
                target.Z + 0.5 + n.Z * 0.499);
            if (RayHitsTarget(faceCenter)) return true;
        }
        return false;
    }

    private int ClientViewForPlayer(IServerPlayer player, int actualId, BlockPos position)
    {
        if (!IsProtectedOreId(actualId)) return actualId;
        if (IsPlayerPlacedOre(position)) return actualId;
        if (!palette.TryGetHost(actualId, out int host)) return actualId;
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return host;

        var key = new WorldBlockKey(position.X, position.Y, position.Z, position.dimension);
        return IsDiscovered(player.PlayerUID, key) ? actualId : host;
    }

    private bool IsWorldExposed(BlockPos position)
    {
        BlockPos adjacent = position.Copy();
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            adjacent.Set(position);
            face.IterateThruFacingOffsets(adjacent);
            int neighbor = ReadWorldBlock(adjacent);
            if (neighbor >= 0 && !palette.IsOpaque(neighbor)) return true;
        }
        return false;
    }

    /// <summary>
    /// Sends recipient-specific block corrections without modifying authoritative world data.
    /// Client Integrity sync is the sole reveal transport. Large vein reveals are bounded/batched.
    /// </summary>
    private void SendPlayerViewCorrections(IServerPlayer player, IEnumerable<WorldBlockKey> keys, bool reassertSmallSet = false)
    {
        if (disposed || player == null || keys == null) return;

        WorldBlockKey[] materialized = keys.Distinct().ToArray();
        if (materialized.Length == 0) return;

        string playerUid = player.PlayerUID ?? string.Empty;
        if (playerUid.Length == 0) return;

        int offset = 0;
        int batchNumber = 0;
        while (offset < materialized.Length)
        {
            int take = Math.Min(CorrectionBatchSize, materialized.Length - offset);
            var batch = new WorldBlockKey[take];
            Array.Copy(materialized, offset, batch, 0, take);
            int delay = batchNumber * 40;

            if (delay == 0)
                SendPlayerViewCorrectionsBatch(playerUid, batch);
            else
                api.Event.RegisterCallback(_ => SendPlayerViewCorrectionsBatch(playerUid, batch), delay);

            offset += take;
            batchNumber++;
        }

        // The client sync layer performs its own stabilization/remesh window, so no native
        // SetBlock reassert pulse is needed here. `reassertSmallSet` is retained only to avoid
        // widening call-site churn while the old native-correction path is retired.
    }

    private void SendPlayerViewCorrectionsBatch(string playerUid, WorldBlockKey[] keys)
    {
        if (disposed || string.IsNullOrWhiteSpace(playerUid) || keys == null || keys.Length == 0) return;

        lock (sync) revealDispatchAttempts += keys.Length;

        IServerPlayer? player = FindOnlinePlayerByUid(playerUid);
        if (player == null)
        {
            lock (sync) revealRecipientResolveFailures += keys.Length;
            return;
        }

        var positions = new List<BlockPos>(keys.Length);
        long real = 0;
        long concealed = 0;
        long staleKeySkips = 0;

        foreach (WorldBlockKey key in keys)
        {
            var pos = new BlockPos(key.X, key.Y, key.Z, key.Dimension);
            int actual = ReadWorldBlock(pos);
            if (!IsProtectedOreId(actual))
            {
                staleKeySkips++;
                continue;
            }

            int clientBlockId = ClientViewForPlayer(player, actual, pos);
            positions.Add(pos);
            if (clientBlockId == actual) real++; else concealed++;
        }

        if (staleKeySkips != 0)
        {
            lock (sync) revealStaleKeySkips += staleKeySkips;
        }

        if (positions.Count == 0) return;

        // Sole transport: public IServerPlayer + Client Integrity sync. The client defers unloaded
        // chunks and stabilizes/remeshes loaded cells. Native SetBlock was proven ineffective on
        // the target 1.22.7 runtime and is intentionally not used as a fallback.
        if (!TrySendClientSync(player, positions)) return;

        lock (sync)
        {
            revealCorrectionPackets += positions.Count;
            revealRealBlockPackets += real;
            reconcealBlockPackets += concealed;
        }
    }

    internal bool TryGetOnlineServerPlayerForClient(ConnectedClient client, out IServerPlayer player)
    {
        player = null!;
        if (client == null || (client.State != EnumClientState.Connected && client.State != EnumClientState.Playing)) return false;

        string uid = client.Entityplayer?.PlayerUID ?? string.Empty;
        if (uid.Length > 0)
        {
            IServerPlayer? byUid = FindOnlinePlayerByUid(uid);
            if (byUid != null)
            {
                player = byUid;
                return true;
            }
        }

        foreach (IPlayer raw in api.World.AllOnlinePlayers)
        {
            if (raw is not IServerPlayer online) continue;
            if (online.ClientId != client.Id) continue;
            player = online;
            return true;
        }

        return false;
    }


    private IEnumerable<IServerPlayer> OnlineServerPlayers()
    {
        foreach (IPlayer raw in api.World.AllOnlinePlayers)
        {
            if (raw is not IServerPlayer player) continue;
            if (player.ConnectionState != EnumClientState.Connected && player.ConnectionState != EnumClientState.Playing) continue;
            yield return player;
        }
    }

    private IServerPlayer? FindOnlinePlayerByUid(string playerUid)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return null;

        foreach (IPlayer raw in api.World.AllOnlinePlayers)
        {
            if (raw is not IServerPlayer online) continue;
            if (string.Equals(online.PlayerUID, playerUid, StringComparison.OrdinalIgnoreCase)) return online;
        }

        // PlayerByUid may return an offline server player, so accept it only when the public
        // connection state says the client is actually connected/playing.
        if (api.World.PlayerByUid(playerUid) is IServerPlayer byUid &&
            (byUid.ConnectionState == EnumClientState.Connected || byUid.ConnectionState == EnumClientState.Playing))
            return byUid;

        return null;
    }

    private bool TrySendClientSync(IServerPlayer player, List<BlockPos> positions, int retryCount = 0, bool stabilize = true)
    {
        if (disposed || oreShieldSyncChannel == null || player == null || positions.Count == 0) return false;
        if (!supportsClientSync(player))
        {
            lock (sync) revealClientSyncUnsupportedSkips += positions.Count;
            return false;
        }
        string uid = player.PlayerUID ?? string.Empty;
        if (uid.Length == 0) return false;
        bool accepted = true;
        lock (sync)
        {
            foreach (BlockPos pos in positions)
            {
                var chunk = new ChunkKey(pos.X >> ChunkBits, pos.InternalY >> ChunkBits, pos.Z >> ChunkBits);
                if (!CanReceiveClientSync(player, chunk, stabilize))
                { clientSyncChunkNotSentSkips++; continue; }
                var key = new ClientSyncOutboxKey(uid, chunk, stabilize);
                int index = Index(pos.X & chunkMask, pos.InternalY & chunkMask, pos.Z & chunkMask);
                // New world state must supersede a queued reveal, and vice versa, for the same cell.
                var opposite = new ClientSyncOutboxKey(uid, chunk, !stabilize);
                // A timeout is older than any freshly queued update for this cell.
                if (retryCount > 0 && clientSyncOutbox.TryGetValue(opposite, out var newer) && newer.Positions.ContainsKey(index)) continue;
                if (!clientSyncOutbox.ContainsKey(key) && clientSyncOutbox.Count >= ClientSyncMaxQueuedGroups)
                { clientSyncQueueOverflows++; accepted = false; continue; }
                if (clientSyncOutbox.TryGetValue(opposite, out var previous) && previous.Positions.Remove(index))
                { clientSyncQueuedCells--; clientSyncQueueCoalesces++; }
                if (!clientSyncOutbox.TryGetValue(key, out var work))
                {
                    if (clientSyncOutbox.Count >= ClientSyncMaxQueuedGroups)
                    { clientSyncQueueOverflows++; accepted = false; continue; }
                    clientSyncOutbox[key] = work = new() { RetryCount = retryCount };
                    (stabilize ? clientSyncRevealQueue : clientSyncWorldQueue).Enqueue(key);
                }
                if (work.Positions.ContainsKey(index)) clientSyncQueueCoalesces++;
                else
                {
                    if (clientSyncQueuedCells >= ClientSyncMaxQueuedCells)
                    { clientSyncQueueOverflows++; accepted = false; continue; }
                    clientSyncQueuedCells++;
                }
                work.RetryCount = Math.Min(work.RetryCount, retryCount);
                work.Positions[index] = pos.Copy();
            }
        }
        if (!accepted && stabilize) ResyncKnownDiscoveries(player);
        return accepted;
    }

    private bool CanReceiveClientSync(IServerPlayer player, ChunkKey chunk, bool stabilize)
    {
        // Authorized discoveries can arrive before chunk installation and use client deferral.
        // Other corrections are useful only for a sent chunk or an outstanding reveal.
        if (server.Clients != null && server.Clients.TryGetValue(player.ClientId, out ConnectedClient? client) &&
            client.ChunkSent != null && client.ChunkSent.Contains(ChunkIndex3D(chunk))) return true;
        lock (sync) return clientSyncPendingByChunk.ContainsKey(new ClientSyncRecipientChunk(player.PlayerUID, chunk)) ||
            (stabilize && playerDiscoveries.TryGetValue(player.PlayerUID, out var discoveries) && discoveries.ContainsKey(chunk));
    }

    private void PumpClientSync()
    {
        if (disposed || !config.OreShieldEnabled) return;
        long now = Environment.TickCount64;
        lock (sync)
        {
            long gap = Math.Max(0, now - clientSyncLastPumpMs);
            // Main-thread pauses delay ACK processing too. Advance a service clock by at most
            // 500ms per pump so a long server stall cannot expire every sequence at once.
            clientSyncClockMs += Math.Min(gap, 500);
            if (gap > 2000) clientSyncStallDeferrals++;
            clientSyncLastPumpMs = now;
        }
        long started = Stopwatch.GetTimestamp();
        for (int handled = 0; handled < 16 && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 1; handled++)
        {
            ClientSyncPendingKey key;
            lock (sync)
            {
                if (!clientSyncDeadlines.TryPeek(out key, out long deadline) || deadline > clientSyncClockMs) break;
                clientSyncDeadlines.Dequeue();
            }
            HandleClientSyncTimeout(key.PlayerUid, key.Sequence);
        }
        int visits;
        lock (sync) visits = clientSyncWorldQueue.Count + clientSyncRevealQueue.Count;
        for (int sent = 0, visited = 0; sent < 16 && visited < visits && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 3; visited++)
        {
            ClientSyncOutboxKey key;
            ClientSyncOutbox work;
            List<BlockPos> positions;
            lock (sync)
            {
                // Favor cancellations without starving reveals behind an ACK-blocked player.
                var queue = clientSyncWorldQueue.Count > 0 && (visited % 4 != 3 || clientSyncRevealQueue.Count == 0)
                    ? clientSyncWorldQueue : clientSyncRevealQueue;
                if (queue.Count == 0) break;
                key = queue.Dequeue();
                if (!clientSyncOutbox.TryGetValue(key, out work!)) continue;
                if (work.Positions.Count == 0) { clientSyncOutbox.Remove(key); continue; }
                if (pendingClientSync.Count >= ClientSyncMaxPending || clientSyncPendingByPlayer.GetValueOrDefault(key.PlayerUid) >= ClientSyncMaxPendingPerPlayer)
                { queue.Enqueue(key); continue; }
                positions = work.Positions.Values.Take(OreShieldSyncProtocol.MaxBatchEntries).ToList();
                foreach (var pos in positions) work.Positions.Remove(Index(pos.X & chunkMask, pos.InternalY & chunkMask, pos.Z & chunkMask));
                clientSyncQueuedCells -= positions.Count;
                if (work.Positions.Count == 0) clientSyncOutbox.Remove(key); else queue.Enqueue(key);
            }
            IServerPlayer? player = FindOnlinePlayerByUid(key.PlayerUid);
            if (player == null || !supportsClientSync(player)) continue;
            if (!CanReceiveClientSync(player, key.Chunk, key.Stabilize))
            { lock (sync) clientSyncChunkNotSentSkips += positions.Count; continue; }
            // Recompute current state at dispatch. A queued/retried stabilized ore reveal must
            // never pin air or a replacement block after the player has mined the ore.
            if (key.Stabilize) positions.RemoveAll(pos => !IsProtectedOreId(ReadWorldBlock(pos)));
            if (positions.Count > 0)
            {
                if (!SendClientSyncNow(player, positions, work.RetryCount, key.Stabilize) && work.RetryCount < 1)
                    TrySendClientSync(player, positions, 1, key.Stabilize);
                sent++;
            }
        }
        lock (sync)
        {
            if (clientSyncTimeoutSummaryCount == 0 || now - clientSyncLastTimeoutSummaryMs < 30000) return;
            api.Logger.Debug("[ServerEssentials/AntiCheat/OreShield] {0} sync batch timeout(s), {1} block(s) since the last summary. Recovery is queued and bounded; pending={2}, queued cells={3}. These are delivery timeouts, not cheating findings.",
                clientSyncTimeoutSummaryCount, clientSyncTimeoutSummaryEntries, pendingClientSync.Count, clientSyncQueuedCells);
            clientSyncTimeoutSummaryCount = clientSyncTimeoutSummaryEntries = 0;
            clientSyncLastTimeoutSummaryMs = now;
        }
    }

    private bool SendClientSyncNow(IServerPlayer player, List<BlockPos> positions, int retryCount = 0, bool stabilize = true)
    {
        if (oreShieldSyncChannel == null || player == null || positions.Count == 0) return false;
        if (!supportsClientSync(player))
        {
            lock (sync) revealClientSyncUnsupportedSkips += positions.Count;
            return false;
        }
        try
        {
            foreach (IGrouping<ChunkKey, BlockPos> group in positions.GroupBy(p => new ChunkKey(p.X >> ChunkBits, p.InternalY >> ChunkBits, p.Z >> ChunkBits)))
            {
                BlockPos[] allChunkPositions = group.ToArray();
                for (int offset = 0; offset < allChunkPositions.Length; offset += OreShieldSyncProtocol.MaxBatchEntries)
                {
                    BlockPos[] chunkPositions = allChunkPositions.Skip(offset).Take(OreShieldSyncProtocol.MaxBatchEntries).ToArray();
                    if (chunkPositions.Length == 0) continue;
                    int[] localIndices = new int[chunkPositions.Length];
                    int[] blockIds = new int[chunkPositions.Length];
                    for (int i = 0; i < chunkPositions.Length; i++)
                    {
                        BlockPos pos = chunkPositions[i];
                        localIndices[i] = Index(pos.X & chunkMask, pos.InternalY & chunkMask, pos.Z & chunkMask);
                        int actual = server.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid).Id;
                        blockIds[i] = ClientViewForPlayer(player, actual, pos);
                    }

                    long sequence = System.Threading.Interlocked.Increment(ref clientSyncSequence);
                    long revision;
                    lock (sync) revision = RevisionNoLock(group.Key);
                    var packet = new OreShieldSyncBatchPacket
                    {
                        Sequence = sequence,
                        ChunkX = group.Key.X,
                        ChunkY = group.Key.Y,
                        ChunkZ = group.Key.Z,
                        Revision = revision,
                        LocalIndices = localIndices,
                        BlockIds = blockIds,
                        ApplyMode = stabilize ? 0 : 1
                    };
                    var pending = new PendingClientSync
                    {
                        PlayerUid = player.PlayerUID ?? string.Empty,
                        Sequence = sequence,
                        Chunk = group.Key,
                        Revision = revision,
                        Positions = chunkPositions,
                        RetryCount = retryCount,
                        Stabilize = stabilize
                    };
                    var pendingKey = new ClientSyncPendingKey(pending.PlayerUid, sequence);
                    lock (sync)
                    {
                        // Store pending state before the send so an integrated/single-player client can
                        // ACK synchronously without racing ahead of server bookkeeping.
                        TrackPendingClientSyncNoLock(pending);
                        clientSyncDeadlines.Enqueue(pendingKey, clientSyncClockMs + ClientSyncTimeoutDelayMs);
                    }
                    try
                    {
                        oreShieldSyncChannel.SendPacket(packet, player);
                        lock (sync)
                        {
                            clientSyncBatchesSent++;
                            clientSyncEntriesSent += chunkPositions.Length;
                        }
                    }
                    catch
                    {
                        lock (sync) RemovePendingClientSyncNoLock(pendingKey);
                        throw;
                    }

                }
            }
            return true;
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                clientSyncSendFailures++;
                long now = Environment.TickCount64;
                if (now - clientSyncLastSendWarningMs >= 30000)
                {
                    clientSyncLastSendWarningMs = now;
                    api.Logger.Warning("[ServerEssentials/AntiCheat/OreShield] Sync transport failures: {0} total. Recovery is bounded. Latest: {1}: {2}", clientSyncSendFailures, ex.GetType().Name, ex.Message);
                }
            }
            return false;
        }
    }

    private void HandleClientSyncTimeout(string playerUid, long sequence)
    {
        PendingClientSync? pending;
        lock (sync)
        {
            var key = new ClientSyncPendingKey(playerUid, sequence);
            if (!pendingClientSync.TryGetValue(key, out pending)) return;
            RemovePendingClientSyncNoLock(key);
            clientSyncTimeouts++;
            clientSyncTimeoutSummaryCount++;
            clientSyncTimeoutSummaryEntries += pending.Positions.Length;
        }

        // Retry the same already-authorized state once on the dedicated sync channel. Do not fall
        // back to native SetBlock: that path is ineffective on the target runtime and previously
        // obscured the real failure mode. A second timeout remains visible in telemetry/logs.
        if (pending.RetryCount >= 1) return;

        IServerPlayer? player = FindOnlinePlayerByUid(playerUid);
        if (player == null || !supportsClientSync(player)) return;

        lock (sync) clientSyncRetries++;
        TrySendClientSync(player, pending.Positions.ToList(), pending.RetryCount + 1, pending.Stabilize);
    }

    public void HandleClientSyncAck(IServerPlayer player, OreShieldSyncAckPacket packet)
    {
        if (disposed || player == null || packet == null || packet.ProtocolVersion != OreShieldSyncProtocol.Version) return;
        string uid = player.PlayerUID ?? string.Empty;
        if (uid.Length == 0) return;
        var key = new ClientSyncPendingKey(uid, packet.Sequence);
        PendingClientSync? failedPending = null;

        lock (sync)
        {
            clientSyncAcks++;
            if (packet.Deferred > 0) clientSyncDeferredAcks++;
            if (!pendingClientSync.TryGetValue(key, out PendingClientSync? pending)) return;
            if (packet.ChunkX != pending.Chunk.X || packet.ChunkY != pending.Chunk.Y || packet.ChunkZ != pending.Chunk.Z || packet.Revision != pending.Revision) return;

            // Receipt ACKs may report deferred work. Only a cumulative final ACK (Deferred == 0)
            // closes the sequence and contributes applied/failed delivery totals.
            if (packet.Deferred != 0 || packet.Applied + packet.Failed + packet.Skipped < pending.ExpectedEntries) return;

            RemovePendingClientSyncNoLock(key);
            clientSyncAppliedEntries += Math.Max(0, packet.Applied);
            clientSyncFailedEntries += Math.Max(0, packet.Failed);
            clientSyncSkippedEntries += Math.Max(0, packet.Skipped);
            if (packet.Failed > 0)
            {
                clientSyncApplyFailureBatches++;
                failedPending = pending;
            }
        }

        if (failedPending == null) return;

        api.Logger.Debug(
            "[ServerEssentials/AntiCheat/OreShield] Client reported {0} failed apply(s) in sync sequence {1} for {2} (chunk {3},{4},{5}).",
            packet.Failed, packet.Sequence, uid, failedPending.Chunk.X, failedPending.Chunk.Y, failedPending.Chunk.Z);

        // Keep normal consoles quiet under load: aggregate apply-failure warnings instead of printing
        // one line per sequence/player. Detailed sequence information remains available at Debug.
        long failureNow = Environment.TickCount64;
        long summarizedFailures = 0;
        bool logFailureSummary = false;
        lock (sync)
        {
            clientSyncFailureWarningsSuppressed += Math.Max(1, packet.Failed);
            if (failureNow - lastClientSyncFailureWarningMs >= 30000)
            {
                summarizedFailures = clientSyncFailureWarningsSuppressed;
                clientSyncFailureWarningsSuppressed = 0;
                lastClientSyncFailureWarningMs = failureNow;
                logFailureSummary = true;
            }
        }
        if (logFailureSummary)
            api.Logger.Warning("[ServerEssentials/AntiCheat/OreShield] {0} client apply failure(s) observed in the last interval. Recovery is rate-limited; see Ore Shield telemetry for totals.", summarizedFailures);

        // A final apply failure is not success. Apply-once world-state corrections (such as
        // ore -> air after mining) retry their exact coordinates. Normal stabilized reveals retry the bounded batch using
        // current authoritative discovery state at dispatch. Only one recovery per player/chunk is allowed every five
        // seconds so many stale sequences cannot fan out into a re-sync storm.
        if (failedPending.RetryCount >= 1 || !supportsClientSync(player)) return;
        string failureCooldownKey = uid + "|" + failedPending.Chunk.X + "|" + failedPending.Chunk.Y + "|" + failedPending.Chunk.Z;
        lock (sync)
        {
            if (clientSyncLastFailureResyncMs.TryGetValue(failureCooldownKey, out long lastFailureResync) && failureNow - lastFailureResync < 5000) return;
            clientSyncLastFailureResyncMs[failureCooldownKey] = failureNow;
        }
        // Retry this bounded batch. Rebuilding every discovery in the chunk synchronously from an
        // ACK handler can turn a client apply failure into a server tick stall.
        List<BlockPos> retryPositions = failedPending.Positions.Select(pos => pos.Copy()).ToList();
        if (retryPositions.Count == 0) return;

        lock (sync)
        {
            clientSyncFailureResyncs++;
            clientSyncRetries++;
        }
        TrySendClientSync(player, retryPositions, failedPending.RetryCount + 1, failedPending.Stabilize);
    }

    public void HandleClientSyncRequest(IServerPlayer player, OreShieldChunkSyncRequestPacket packet)
    {
        if (disposed || player == null || packet == null || packet.ProtocolVersion != OreShieldSyncProtocol.Version || !supportsClientSync(player)) return;
        var chunk = new ChunkKey(packet.ChunkX, packet.ChunkY, packet.ChunkZ);
        string uid = player.PlayerUID ?? string.Empty;
        long now = Environment.TickCount64;
        lock (sync)
        {
            if (clientSyncLastResyncRequestMs.TryGetValue(uid, out long last) && now - last < 250) return;
            clientSyncLastResyncRequestMs[uid] = now;
            clientSyncResyncRequests++;
        }
        // A client request must not synchronously expand an entire retained discovery chunk.
        // Reuse the cooperative loaded-world resync worker, bounded to 64 cells per pass.
        lock (sync)
        {
            if (!playerDiscoveries.TryGetValue(uid, out var known) || !known.ContainsKey(chunk)) return;
            if (!pendingDiscoveryResyncs.TryGetValue(uid, out var job))
                pendingDiscoveryResyncs[uid] = new ResyncJob { Chunks = new Queue<ChunkKey>(new[] { chunk }) };
            else if ((job.Cells == null || job.Chunk != chunk) && job.Chunks.Count < 128 && !job.Chunks.Contains(chunk)) job.Chunks.Enqueue(chunk);
        }
    }

    private BlockPos BlockPosFromChunkLocalIndex(ChunkKey chunk, int index)
    {
        int ly = index / (chunkSize * chunkSize);
        int rem = index - ly * chunkSize * chunkSize;
        int lz = rem / chunkSize;
        int lx = rem - lz * chunkSize;
        DecodeChunkY(chunk.Y, out int localChunkY, out int dimension);
        return new BlockPos(
            chunk.X * chunkSize + lx,
            localChunkY * chunkSize + ly,
            chunk.Z * chunkSize + lz,
            dimension);
    }

    private int ReadWorldBlock(BlockPos position)
    {
        IWorldChunk? chunk = server.BlockAccessor.GetChunk(position.X >> ChunkBits, position.InternalY >> ChunkBits, position.Z >> ChunkBits);
        if (chunk == null || chunk.Disposed) return -1;
        return chunk.UnpackAndReadBlock(Index(position.X & chunkMask, position.InternalY & chunkMask, position.Z & chunkMask), BlockLayersAccess.Solid);
    }

    private void LoadNeighbors(int chunkX, int internalChunkY, int chunkZ, IWorldChunk?[] neighbors, int dependencyMask)
    {
        DecodeChunkY(internalChunkY, out int localY, out _);
        if ((dependencyMask & (1 << BlockFacing.NORTH.Index)) != 0)
            neighbors[BlockFacing.NORTH.Index] = GetChunkAndBind(chunkX, internalChunkY, chunkZ - 1);
        if ((dependencyMask & (1 << BlockFacing.EAST.Index)) != 0)
            neighbors[BlockFacing.EAST.Index] = GetChunkAndBind(chunkX + 1, internalChunkY, chunkZ);
        if ((dependencyMask & (1 << BlockFacing.SOUTH.Index)) != 0)
            neighbors[BlockFacing.SOUTH.Index] = GetChunkAndBind(chunkX, internalChunkY, chunkZ + 1);
        if ((dependencyMask & (1 << BlockFacing.WEST.Index)) != 0)
            neighbors[BlockFacing.WEST.Index] = GetChunkAndBind(chunkX - 1, internalChunkY, chunkZ);
        if ((dependencyMask & (1 << BlockFacing.UP.Index)) != 0 && localY + 1 < server.WorldMap.ChunkMapSizeY)
            neighbors[BlockFacing.UP.Index] = GetChunkAndBind(chunkX, internalChunkY + 1, chunkZ);
        if ((dependencyMask & (1 << BlockFacing.DOWN.Index)) != 0 && localY > 0)
            neighbors[BlockFacing.DOWN.Index] = GetChunkAndBind(chunkX, internalChunkY - 1, chunkZ);
    }

    private IWorldChunk? GetChunkAndBind(int chunkX, int internalChunkY, int chunkZ)
        => server.BlockAccessor.GetChunk(chunkX, internalChunkY, chunkZ);

    private int NeighborAvailabilityMask(IWorldChunk?[] neighbors, int dependencyMask)
    {
        int mask = 0;
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            int bit = 1 << face.Index;
            if ((dependencyMask & bit) == 0) continue;
            IWorldChunk? chunk = neighbors[face.Index];
            if (chunk != null && !chunk.Disposed) mask |= bit;
        }
        return mask;
    }

    private int CurrentNeighborAvailabilityMask(ChunkKey key, int dependencyMask)
    {
        DecodeChunkY(key.Y, out int localY, out _);
        int mask = 0;

        void Check(BlockFacing face, int x, int y, int z)
        {
            int bit = 1 << face.Index;
            if ((dependencyMask & bit) == 0) return;
            IWorldChunk? chunk = server.BlockAccessor.GetChunk(x, y, z);
            if (chunk != null && !chunk.Disposed) mask |= bit;
        }

        Check(BlockFacing.NORTH, key.X, key.Y, key.Z - 1);
        Check(BlockFacing.EAST, key.X + 1, key.Y, key.Z);
        Check(BlockFacing.SOUTH, key.X, key.Y, key.Z + 1);
        Check(BlockFacing.WEST, key.X - 1, key.Y, key.Z);
        if (localY + 1 < server.WorldMap.ChunkMapSizeY) Check(BlockFacing.UP, key.X, key.Y + 1, key.Z);
        if (localY > 0) Check(BlockFacing.DOWN, key.X, key.Y - 1, key.Z);
        return mask;
    }

    private int Index(int x, int y, int z) => (y * chunkSize + z) * chunkSize + x;

    private long ChunkIndex3D(ChunkKey key)
    {
        // ChunkKey.Y includes the dimension offset; the current overload expects local Y.
        DecodeChunkY(key.Y, out int localY, out int dimension);
        return server.WorldMap.ChunkIndex3D(key.X, localY, key.Z, dimension);
    }

    private static void DecodeChunkY(int internalChunkY, out int localY, out int dimension)
    {
        int size = GlobalConstants.DimensionSizeInChunks;
        dimension = internalChunkY / size;
        localY = internalChunkY % size;
        if (localY < 0)
        {
            localY += size;
            dimension--;
        }
    }

    private static string PlacedOreChunkSaveKey(ChunkKey chunk) => FormattableString.Invariant($"{PlacedOreManifestKey}:{chunk.X}:{chunk.Y}:{chunk.Z}");
    private void UpdatePlacedOreChunkNoLock(WorldBlockKey key, bool added)
    {
        ChunkKey chunk = ChunkFor(key);
        if (!placedOreChunks.TryGetValue(chunk, out var cells)) placedOreChunks[chunk] = cells = new();
        if (added) cells.Add(LocalIndex(key)); else cells.Remove(LocalIndex(key));
        if (cells.Count == 0) placedOreChunks.Remove(chunk);
        dirtyPlacedOreChunks.Add(chunk);
    }
    private void LoadPlayerPlacedOreExemptions()
    {
        try
        {
            byte[]? manifest = api.WorldManager.SaveGame.GetData(PlacedOreManifestKey);
            if (manifest?.Length >= 8)
            {
                using var input = new BinaryReader(new MemoryStream(manifest, false));
                int version = input.ReadInt32(), count = input.ReadInt32();
                if (version != 2 || count < 0 || manifest.Length != 8L + count * 12L) throw new InvalidDataException("Invalid placed-ore manifest.");
                for (int i = 0; i < count; i++)
                {
                    var chunk = new ChunkKey(input.ReadInt32(), input.ReadInt32(), input.ReadInt32());
                    byte[] bytes = api.WorldManager.SaveGame.GetData(PlacedOreChunkSaveKey(chunk)) ?? throw new InvalidDataException("Missing placed-ore shard.");
                    using var shard = new BinaryReader(new MemoryStream(bytes, false));
                    if (bytes.Length < 8 || shard.ReadInt32() != 2) throw new InvalidDataException("Invalid placed-ore shard.");
                    int countCells = shard.ReadInt32();
                    if (countCells < 0 || countCells > chunkVolume || bytes.Length != 8L + countCells * 2L) throw new InvalidDataException("Invalid placed-ore shard length.");
                    for (int j = 0; j < countCells; j++)
                    {
                        int index = shard.ReadUInt16();
                        if (index >= chunkVolume) throw new InvalidDataException("Invalid placed-ore cell.");
                        var pos = BlockPosFromChunkLocalIndex(chunk, index);
                        var key = new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension);
                        if (playerPlacedOreExemptions.Add(key)) UpdatePlacedOreChunkNoLock(key, true);
                    }
                    persistedPlacedOreChunks.Add(chunk);
                }
                dirtyPlacedOreChunks.Clear(); return;
            }
            migratingPlacedOre = true;
            byte[]? legacy = api.WorldManager.SaveGame.GetData(PlayerPlacedOreSaveKey);
            if (legacy == null || legacy.Length == 0) return;
            if (legacy.Length < 8) throw new InvalidDataException("Invalid legacy placed-ore header.");
            using var reader = new BinaryReader(new MemoryStream(legacy, false));
            if (reader.ReadInt32() != PlayerPlacedOreSaveVersion) throw new InvalidDataException("Invalid legacy placed-ore version.");
            int countLegacy = reader.ReadInt32();
            if (countLegacy < 0 || legacy.Length != 8L + countLegacy * 16L) throw new InvalidDataException("Invalid legacy placed-ore length.");
            for (int i = 0; i < countLegacy; i++)
            {
                var key = new WorldBlockKey(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                if (playerPlacedOreExemptions.Add(key)) UpdatePlacedOreChunkNoLock(key, true);
            }
        }
        catch (Exception ex)
        {
            // Do not overwrite a damaged authoritative exemption save with partial state.
            placedOrePersistenceFailed = true;
            api.Logger.Warning("[OreShield] Placed-ore save could not be read; preserve it before repair: {0}", ex.Message);
        }
    }
    private void FlushPlacedOreOnWorldSave() => FlushPlacedOreCheckpoints(int.MaxValue);
    private void FlushPlacedOreCheckpoints(int maxChunks)
    {
        if (placedOrePersistenceFailed) return;
        try
        {
            for (int i = 0; i < maxChunks; i++)
            {
                ChunkKey chunk;
                OreShieldDiscoveryCells? cells;
                lock (sync)
                {
                    if (dirtyPlacedOreChunks.Count == 0) break;
                    chunk = dirtyPlacedOreChunks.First();
                    cells = placedOreChunks.TryGetValue(chunk, out var current) ? current.Clone() : null;
                }
                using var stream = new MemoryStream(8 + (cells?.Count ?? 0) * 2);
                using var writer = new BinaryWriter(stream);
                writer.Write(2); writer.Write(cells?.Count ?? 0);
                if (cells != null) foreach (int index in cells) writer.Write((ushort)index);
                writer.Flush(); byte[] bytes = stream.ToArray();
                api.WorldManager.SaveGame.StoreData(PlacedOreChunkSaveKey(chunk), bytes);
                lock (sync)
                {
                    dirtyPlacedOreChunks.Remove(chunk);
                    if (cells?.Count > 0) persistedPlacedOreChunks.Add(chunk); else persistedPlacedOreChunks.Remove(chunk);
                    placedManifestDirty = true;
                    placedOreCheckpoints++; placedOreCheckpointBytes += bytes.Length;
                }
            }
            lock (sync)
            {
                // Publish v2 only after all legacy shards are copied. World-save hooks flush
                // every dirty shard before the engine persists its save-data checkpoint.
                if (migratingPlacedOre && dirtyPlacedOreChunks.Count > 0 || !placedManifestDirty) return;
                using var stream = new MemoryStream(8 + persistedPlacedOreChunks.Count * 12);
                using var writer = new BinaryWriter(stream);
                writer.Write(2); writer.Write(persistedPlacedOreChunks.Count);
                foreach (var chunk in persistedPlacedOreChunks) { writer.Write(chunk.X); writer.Write(chunk.Y); writer.Write(chunk.Z); }
                writer.Flush(); api.WorldManager.SaveGame.StoreData(PlacedOreManifestKey, stream.ToArray());
                placedManifestDirty = false; migratingPlacedOre = false;
            }
        }
        catch (Exception ex)
        { api.Logger.Warning("[OreShield] Player-placed ore checkpoint retained for retry: {0}", ex.Message); }
    }

    private bool MarkPlayerPlacedOreExemption(BlockPos pos, bool persist)
    {
        if (pos == null) return false;
        var key = new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension);
        ChunkKey chunk = ChunkFor(key);
        bool added;
        lock (sync)
        {
            added = playerPlacedOreExemptions.Add(key);
            if (added)
            {
                UpdatePlacedOreChunkNoLock(key, true);
                BumpRevisionNoLock(chunk);
                InvalidateNoLock(chunk);
                RemoveOreIndexNoLock(chunk);
            }
        }

        return added;
    }

    private bool RemovePlayerPlacedOreExemption(BlockPos pos, bool persist)
    {
        if (pos == null) return false;
        var key = new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension);
        ChunkKey chunk = ChunkFor(key);
        bool removed;
        lock (sync)
        {
            removed = playerPlacedOreExemptions.Remove(key);
            if (removed)
            {
                UpdatePlacedOreChunkNoLock(key, false);
                BumpRevisionNoLock(chunk);
                InvalidateNoLock(chunk);
                RemoveOreIndexNoLock(chunk);
            }
        }

        return removed;
    }

    private void InvalidateAround(BlockPos pos)
    {
        int x = pos.X;
        int y = pos.InternalY;
        int z = pos.Z;
        var changedKey = new ChunkKey(x >> ChunkBits, y >> ChunkBits, z >> ChunkBits);
        bool stillProtectedOre = IsProtectedOreId(ReadWorldBlock(pos));
        var changedBlock = new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension);
        bool removedPlacedExemption = false;

        lock (sync)
        {
            if (!stillProtectedOre) removedPlacedExemption = playerPlacedOreExemptions.Remove(changedBlock);
            if (removedPlacedExemption) UpdatePlacedOreChunkNoLock(changedBlock, false);
        }

        lock (sync)
        {
            BumpRevisionNoLock(changedKey);
            InvalidateNoLock(changedKey);

            // Whole-vein reveal state is runtime-local and should not retain coordinates after the
            // ore has actually been mined/replaced. This keeps long mining sessions bounded without
            // changing the visibility of any remaining ore in legitimately discovered veins.
            if (!stillProtectedOre)
            {
                ChunkKey discoveryChunk = ChunkFor(changedBlock);
                int discoveryIndex = LocalIndex(changedBlock);
                if(discoveryOwners.TryGetValue(discoveryChunk,out var owners))
                    foreach(string uid in owners.ToArray())
                    {
                        discoveryInvalidationOwnersVisited++;
                        var chunks=playerDiscoveries[uid];var indices=chunks[discoveryChunk];
                        if(indices.Remove(discoveryIndex))discoveryCells--;
                        if(indices.Count==0)
                        {
                            chunks.Remove(discoveryChunk);UntrackDiscoveryOwnerNoLock(uid,discoveryChunk);
                            if(chunks.Count==0)playerDiscoveries.Remove(uid);
                        }
                    }
            }
        }
        QueueWorldChange(pos, false);
    }

    private void BumpRevisionNoLock(ChunkKey key)
    {
        if(!revisions.ContainsKey(key) && revisions.Count>=MaxRevisionEntries)
        {
            // Advance the fallback epoch before clearing: no in-flight read may
            // mistake a compacted key for its old revision (ABA).
            revisionEpoch=++nextRevision;revisions.Clear();revisionCompactions++;
        }
        revisions[key]=++nextRevision;
        revisionInvalidations++;
        // Do not discard the protected-ore candidate index here. It is fingerprint-validated
        // against the next outgoing chunk payload before masking reuse.
    }

    private long RevisionNoLock(ChunkKey key) => revisions.TryGetValue(key, out long revision) ? revision : revisionEpoch;

    private long RevisionSignatureNoLock(ChunkKey key, int neighborDependencyMask)
    {
        unchecked
        {
            long hash = 1469598103934665603L;
            void Mix(long value) { hash ^= value; hash *= 1099511628211L; }
            Mix(RevisionNoLock(key));

            DecodeChunkY(key.Y, out int localY, out _);
            if ((neighborDependencyMask & (1 << BlockFacing.WEST.Index)) != 0)
                Mix(RevisionNoLock(new ChunkKey(key.X - 1, key.Y, key.Z)));
            if ((neighborDependencyMask & (1 << BlockFacing.EAST.Index)) != 0)
                Mix(RevisionNoLock(new ChunkKey(key.X + 1, key.Y, key.Z)));
            if ((neighborDependencyMask & (1 << BlockFacing.NORTH.Index)) != 0)
                Mix(RevisionNoLock(new ChunkKey(key.X, key.Y, key.Z - 1)));
            if ((neighborDependencyMask & (1 << BlockFacing.SOUTH.Index)) != 0)
                Mix(RevisionNoLock(new ChunkKey(key.X, key.Y, key.Z + 1)));
            if ((neighborDependencyMask & (1 << BlockFacing.DOWN.Index)) != 0 && localY > 0)
                Mix(RevisionNoLock(new ChunkKey(key.X, key.Y - 1, key.Z)));
            if ((neighborDependencyMask & (1 << BlockFacing.UP.Index)) != 0 && localY + 1 < server.WorldMap.ChunkMapSizeY)
                Mix(RevisionNoLock(new ChunkKey(key.X, key.Y + 1, key.Z)));
            return hash;
        }
    }

    private int ComputeNeighborDependencyMask(int[] candidateIndexes)
    {
        int mask = 0;
        foreach (int index in candidateIndexes)
        {
            if ((uint)index >= (uint)chunkVolume) continue;
            int x = index & chunkMask;
            int z = (index >> ChunkBits) & chunkMask;
            int y = index >> (ChunkBits * 2);

            if (x == 0) mask |= 1 << BlockFacing.WEST.Index;
            if (x == chunkMask) mask |= 1 << BlockFacing.EAST.Index;
            if (z == 0) mask |= 1 << BlockFacing.NORTH.Index;
            if (z == chunkMask) mask |= 1 << BlockFacing.SOUTH.Index;
            if (y == 0) mask |= 1 << BlockFacing.DOWN.Index;
            if (y == chunkMask) mask |= 1 << BlockFacing.UP.Index;
            if (mask == ((1 << BlockFacing.NumberOfFaces) - 1)) break;
        }
        return mask;
    }

    private void InvalidateNeighborDependencyNoLock(ChunkKey key, BlockFacing requiredFace)
    {
        if (!cache.TryGetValue(key, out LinkedListNode<CacheEntry>? node)) return;
        if ((node.Value.NeighborDependencyMask & (1 << requiredFace.Index)) == 0) return;
        RemoveNoLock(node);
        revisionInvalidations++;
    }

    private bool InvalidateNoLock(ChunkKey key)
    {
        if (!cache.TryGetValue(key, out LinkedListNode<CacheEntry>? node)) return false;
        RemoveNoLock(node);
        return true;
    }

    private void AddCacheNoLock(ChunkKey key, long signature, ulong sourceFingerprint, byte[]? clientBlocks, bool passThrough, int neighborDependencyMask, int neighborAvailabilityMask)
    {
        if (!config.OreShieldChunkCacheEnabled) return;
        if (cache.TryGetValue(key, out LinkedListNode<CacheEntry>? existing)) RemoveNoLock(existing);
        var entry = new CacheEntry
        {
            Key = key,
            RevisionSignature = signature,
            SourceFingerprint = sourceFingerprint,
            ClientBlocks = clientBlocks,
            PassThrough = passThrough,
            NeighborDependencyMask = neighborDependencyMask,
            NeighborAvailabilityMask = neighborAvailabilityMask
        };
        var node = lru.AddLast(entry);
        cache[key] = node;
        cacheBytes += entry.OwnedBytes;
        TrimCacheNoLock();
    }

    private void TouchNoLock(LinkedListNode<CacheEntry> node)
    {
        lru.Remove(node);
        lru.AddLast(node);
    }

    private void TrimCacheNoLock()
    {
        long limit = Math.Clamp(config.OreShieldChunkCacheMiB, 8, 1024) * 1024L * 1024L;
        int countLimit = Math.Clamp(config.OreShieldMaxCachedChunks, 256, 16384);
        while (lru.First != null && (cacheBytes > limit || cache.Count > countLimit)) RemoveNoLock(lru.First);
    }

    private void SetOreIndexNoLock(ChunkKey key, ulong sourceFingerprint, HashSet<int> indices, OreShieldExposedIndex exposedIndices)
    {
        oreIndexCache[key] = new OreIndexEntry
        {
            SourceFingerprint = sourceFingerprint,
            Indices = indices,
            ExposedIndices = exposedIndices,
            PendingExposure = indices.ToArray()
        };
        if (pendingExposureBuilds.Count >= Math.Max(512, config.OreShieldMaxCachedChunks * 2))
        {
            pendingExposureBuilds.Clear();
            foreach (var pair in oreIndexCache) if (pair.Value.PendingExposure != null) pendingExposureBuilds.Enqueue(pair.Key);
        }
        pendingExposureBuilds.Enqueue(key);
        TouchOreIndexNoLock(key);
        TrimOreIndexCacheNoLock();
    }

    private void TouchOreIndexNoLock(ChunkKey key)
    {
        if (oreIndexLruNodes.TryGetValue(key, out LinkedListNode<ChunkKey>? node))
        {
            oreIndexLru.Remove(node);
            oreIndexLru.AddLast(node);
            return;
        }

        LinkedListNode<ChunkKey> added = oreIndexLru.AddLast(key);
        oreIndexLruNodes[key] = added;
    }

    private void RemoveOreIndexNoLock(ChunkKey key)
    {
        oreIndexCache.Remove(key);
        foreach (var face in BlockFacing.ALLFACES) boundaryCursors.Remove(new BoundaryRefreshKey(key, face.Index));
        if (!oreIndexLruNodes.TryGetValue(key, out LinkedListNode<ChunkKey>? node)) return;
        oreIndexLruNodes.Remove(key);
        oreIndexLru.Remove(node);
    }

    private void TrimOreIndexCacheNoLock()
    {
        int countLimit = Math.Clamp(config.OreShieldMaxCachedChunks, 256, 16384);
        while (oreIndexCache.Count > countLimit && oreIndexLru.First != null)
            RemoveOreIndexNoLock(oreIndexLru.First.Value);
    }

    private static ulong FingerprintBlocks(byte[] bytes)
    {
        // FNV-1a 64-bit over the already-compressed block payload. This is much cheaper than
        // decompressing 32,768 block IDs and prevents stale-cache reuse when a block mutation
        // bypasses Ore Shield's targeted update hooks.
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= prime;
        }
        hash ^= (ulong)bytes.Length;
        hash *= prime;
        return hash;
    }

    private void RemoveNoLock(LinkedListNode<CacheEntry> node)
    {
        cache.Remove(node.Value.Key);
        lru.Remove(node);
        cacheBytes -= node.Value.OwnedBytes;
        if (cacheBytes < 0) cacheBytes = 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        FlushPlacedOreOnWorldSave();
        try { api.Event.GameWorldSave -= FlushPlacedOreOnWorldSave; } catch { }
        try { api.Event.UnregisterGameTickListener(visibilityTickListenerId); } catch { }
        try { api.Event.UnregisterGameTickListener(clientSyncTickListenerId); } catch { }
        lock (sync)
        {
            cache.Clear();
            lru.Clear();
            revisions.Clear();
            pendingAuthoritativeCells.Clear(); pendingExposureRefreshChunks = null;
            pendingWorldChanges.Clear(); pendingExposureBuilds.Clear(); boundaryCursors.Clear(); pendingVeins.Clear();
            foreach (var job in pendingDiscoveryResyncs.Values) job.Cells?.Dispose();
            pendingDiscoveryResyncs.Clear();
            pendingBoundaryRefreshes.Clear();
            pendingBoundaryRefreshKeys.Clear();
            oreIndexCache.Clear();
            oreIndexLru.Clear();
            oreIndexLruNodes.Clear();
            playerDiscoveries.Clear();
            discoveryOfflineSinceMs.Clear();
            discoveryOwners.Clear();discoveryChunkRecords=0;
            playerPlacedOreExemptions.Clear(); placedOreChunks.Clear(); dirtyPlacedOreChunks.Clear(); persistedPlacedOreChunks.Clear();
            playerVisibilityStates.Clear();
            pendingClientSync.Clear(); clientSyncPendingByPlayer.Clear(); clientSyncPendingByChunk.Clear();
            clientSyncDeadlines.Clear(); clientSyncOutbox.Clear(); clientSyncRevealQueue.Clear(); clientSyncWorldQueue.Clear(); clientSyncQueuedCells = 0;
            clientSyncLastResyncRequestMs.Clear();
            clientSyncLastFailureResyncMs.Clear();
            recentProtectedOreBreaks.Clear();
            cacheBytes = 0;
            discoveryCells = 0;
        }
    }
}
