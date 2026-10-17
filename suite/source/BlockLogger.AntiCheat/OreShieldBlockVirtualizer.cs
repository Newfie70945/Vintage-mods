using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using NewfiesAntiCheat.Shared.Packets;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace BlockLogger.AntiCheat;

internal sealed class OreShieldBlockVirtualizer : IDisposable
{
	private sealed class VeinJob
	{
		public readonly Queue<WorldBlockKey> Queue = new Queue<WorldBlockKey>();

		public readonly HashSet<WorldBlockKey> Visited = new HashSet<WorldBlockKey>();

		public readonly Queue<(WorldBlockKey Position, int OreId)> Seeds = new Queue<(WorldBlockKey, int)>();

		public readonly HashSet<WorldBlockKey> SeedKeys = new HashSet<WorldBlockKey>();

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

	internal readonly record struct ChunkKey(int X, int Y, int Z);

	private readonly record struct WorldBlockKey(int X, int Y, int Z, int Dimension);

	private readonly record struct BoundaryRefreshKey(ChunkKey Chunk, int Face);

	private readonly record struct ClientSyncPendingKey(string PlayerUid, long Sequence);

	private readonly record struct ClientSyncRecipientChunk(string PlayerUid, ChunkKey Chunk);

	private readonly record struct ClientSyncOutboxKey(string PlayerUid, ChunkKey Chunk, bool Stabilize);

	private sealed class ClientSyncOutbox
	{
		public readonly Dictionary<int, BlockPos> Positions = new Dictionary<int, BlockPos>();

		public int RetryCount;
	}

	private sealed class OreIndexEntry
	{
		public int[]? PendingExposure;

		public int ExposureCursor;

		public required ulong SourceFingerprint { get; set; }

		public required HashSet<int> Indices { get; init; }

		public required OreShieldExposedIndex ExposedIndices { get; init; }
	}

	private sealed class PlayerVisibilityState
	{
		public readonly Queue<WorldBlockKey> PendingCandidates = new Queue<WorldBlockKey>();

		public double X;

		public double InternalY;

		public double Z;

		public float Yaw;

		public float Pitch;

		public int Dimension;

		public long LastScanMs;

		public bool Initialized;

		public OreShieldScanCursor Cursor { get; } = new OreShieldScanCursor();
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

	private readonly int chunkSize = 32;

	private readonly int chunkMask = 31;

	private readonly int chunkVolume = 32768;

	private const int ChunkBits = 5;

	private const int CorrectionBatchSize = 64;

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

	private readonly object sync = new object();

	private readonly ChunkDataPool dataPool;

	private readonly Action<int[], byte[], byte[], int> unpackBlocks;

	private readonly Dictionary<ChunkKey, LinkedListNode<CacheEntry>> cache = new Dictionary<ChunkKey, LinkedListNode<CacheEntry>>();

	private readonly LinkedList<CacheEntry> lru = new LinkedList<CacheEntry>();

	private readonly Dictionary<ChunkKey, long> revisions = new Dictionary<ChunkKey, long>();

	private const int MaxRevisionEntries = 16384;

	private long revisionEpoch;

	private long nextRevision;

	private long revisionCompactions;

	private readonly Queue<BoundaryRefreshKey> pendingBoundaryRefreshes = new Queue<BoundaryRefreshKey>();

	private readonly HashSet<BoundaryRefreshKey> pendingBoundaryRefreshKeys = new HashSet<BoundaryRefreshKey>();

	private readonly Dictionary<ChunkKey, OreIndexEntry> oreIndexCache = new Dictionary<ChunkKey, OreIndexEntry>();

	private readonly LinkedList<ChunkKey> oreIndexLru = new LinkedList<ChunkKey>();

	private readonly Dictionary<ChunkKey, LinkedListNode<ChunkKey>> oreIndexLruNodes = new Dictionary<ChunkKey, LinkedListNode<ChunkKey>>();

	private readonly Dictionary<string, Dictionary<ChunkKey, OreShieldDiscoveryCells>> playerDiscoveries = new Dictionary<string, Dictionary<ChunkKey, OreShieldDiscoveryCells>>(StringComparer.Ordinal);

	private readonly HashSet<WorldBlockKey> playerPlacedOreExemptions = new HashSet<WorldBlockKey>();

	private readonly Dictionary<ChunkKey, OreShieldDiscoveryCells> placedOreChunks = new Dictionary<ChunkKey, OreShieldDiscoveryCells>();

	private readonly HashSet<ChunkKey> dirtyPlacedOreChunks = new HashSet<ChunkKey>();

	private readonly HashSet<ChunkKey> persistedPlacedOreChunks = new HashSet<ChunkKey>();

	private const string PlacedOreManifestKey = "newfies:oreshield-playerplaced-v2";

	private bool migratingPlacedOre;

	private bool placedManifestDirty;

	private bool placedOrePersistenceFailed;

	private long placedOreCheckpoints;

	private long placedOreCheckpointBytes;

	private readonly Dictionary<ChunkKey, HashSet<string>> discoveryOwners = new Dictionary<ChunkKey, HashSet<string>>();

	private long discoveryChunkRecords;

	private long discoveryInvalidationOwnersVisited;

	private readonly Dictionary<string, PlayerVisibilityState> playerVisibilityStates = new Dictionary<string, PlayerVisibilityState>(StringComparer.Ordinal);

	private readonly Dictionary<ClientSyncPendingKey, PendingClientSync> pendingClientSync = new Dictionary<ClientSyncPendingKey, PendingClientSync>();

	private readonly Dictionary<string, int> clientSyncPendingByPlayer = new Dictionary<string, int>(StringComparer.Ordinal);

	private readonly Dictionary<ClientSyncRecipientChunk, int> clientSyncPendingByChunk = new Dictionary<ClientSyncRecipientChunk, int>();

	private readonly PriorityQueue<ClientSyncPendingKey, long> clientSyncDeadlines = new PriorityQueue<ClientSyncPendingKey, long>();

	private readonly Dictionary<ClientSyncOutboxKey, ClientSyncOutbox> clientSyncOutbox = new Dictionary<ClientSyncOutboxKey, ClientSyncOutbox>();

	private readonly Queue<ClientSyncOutboxKey> clientSyncRevealQueue = new Queue<ClientSyncOutboxKey>();

	private readonly Queue<ClientSyncOutboxKey> clientSyncWorldQueue = new Queue<ClientSyncOutboxKey>();

	private const int ClientSyncMaxPending = 1024;

	private const int ClientSyncMaxPendingPerPlayer = 32;

	private const int ClientSyncMaxQueuedCells = 65536;

	private const int ClientSyncMaxQueuedGroups = 1024;

	private int clientSyncQueuedCells;

	private long clientSyncClockMs;

	private long clientSyncLastPumpMs = Environment.TickCount64;

	private long clientSyncLastSendWarningMs;

	private long clientSyncSendFailures;

	private long clientSyncQueueCoalesces;

	private long clientSyncQueueOverflows;

	private long clientSyncChunkNotSentSkips;

	private long clientSyncStallDeferrals;

	private long clientSyncTimeoutSummaryCount;

	private long clientSyncTimeoutSummaryEntries;

	private long clientSyncLastTimeoutSummaryMs;

	private readonly Dictionary<string, long> clientSyncLastResyncRequestMs = new Dictionary<string, long>(StringComparer.Ordinal);

	private readonly Dictionary<string, long> clientSyncLastFailureResyncMs = new Dictionary<string, long>(StringComparer.Ordinal);

	private readonly Dictionary<WorldBlockKey, long> recentProtectedOreBreaks = new Dictionary<WorldBlockKey, long>();

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

	private long revealDispatchAttempts;

	private long revealRecipientResolveFailures;

	private long revealStaleKeySkips;

	private long revealClientSyncUnsupportedSkips;

	private readonly Dictionary<WorldBlockKey, bool> pendingWorldChanges = new Dictionary<WorldBlockKey, bool>();

	private readonly Queue<ChunkKey> pendingExposureBuilds = new Queue<ChunkKey>();

	private readonly Dictionary<BoundaryRefreshKey, int> boundaryCursors = new Dictionary<BoundaryRefreshKey, int>();

	private readonly Dictionary<string, VeinJob> pendingVeins = new Dictionary<string, VeinJob>(StringComparer.Ordinal);

	private readonly Dictionary<string, ResyncJob> pendingDiscoveryResyncs = new Dictionary<string, ResyncJob>(StringComparer.Ordinal);

	private readonly HashSet<WorldBlockKey> pendingAuthoritativeCells = new HashSet<WorldBlockKey>();

	private bool authoritativeFlushQueued;

	private long authoritativeCorrectionFlushes;

	private bool geometryChanged;

	private bool exposureRefreshRequested;

	private Queue<ChunkKey>? pendingExposureRefreshChunks;

	private long worldChangeCoalesces;

	private long worldChangeOverflows;

	private long exposureBuildCells;

	private long visibilityStarted;

	private double visibilityLastMs;

	private double visibilityMaxMs;

	private bool disposed;

	private long veinServiceSequence;

	private long veinBudgetLimitedJobs;

	private bool VisibilityTimeExpired => Stopwatch.GetElapsedTime(visibilityStarted).TotalMilliseconds >= 4.0;

	public OreShieldBlockVirtualizer(ICoreServerAPI api, ServerMain server, AntiCheatConfig config, OreShieldPalette palette, IServerNetworkChannel? oreShieldSyncChannel = null, System.Func<IServerPlayer, bool>? supportsClientSync = null)
	{
		this.api = api;
		this.server = server;
		this.config = config;
		this.palette = palette;
		this.oreShieldSyncChannel = oreShieldSyncChannel;
		this.supportsClientSync = supportsClientSync ?? ((System.Func<IServerPlayer, bool>)((IServerPlayer _) => false));
		dataPool = new ChunkDataPool(chunkSize, server);
		MethodInfo methodInfo = AccessTools.Method(typeof(ChunkData), "UnpackBlocksTo", new Type[4]
		{
			typeof(int[]),
			typeof(byte[]),
			typeof(byte[]),
			typeof(int)
		}) ?? throw new MissingMethodException(typeof(ChunkData).FullName, "UnpackBlocksTo");
		unpackBlocks = methodInfo.CreateDelegate<Action<int[], byte[], byte[], int>>();
		LoadPlayerPlacedOreExemptions();
		api.Event.GameWorldSave += FlushPlacedOreOnWorldSave;
		visibilityTickListenerId = api.Event.RegisterGameTickListener(OnVisibilityTick, config.OreShieldVisibilityScanIntervalMs);
		clientSyncTickListenerId = api.Event.RegisterGameTickListener((float _) =>
		{
			PumpClientSync();
		}, 100);
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
				AdditionalOrePatterns = new List<string>(config.OreShieldAdditionalOrePatterns ?? new List<string>()),
				ExcludedBlockPatterns = new List<string>(config.OreShieldExcludedBlockPatterns ?? new List<string>()),
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
				DecoyBlocks = 0L,
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
				OldestVeinJobAgeMs = ((pendingVeins.Count == 0) ? 0 : Math.Max(0L, Environment.TickCount64 - pendingVeins.Values.Min((VeinJob j) => j.StartedAt))),
				VeinLimitReachedJobs = veinBudgetLimitedJobs,
				VeinDiscoveries = veinDiscoveries,
				VeinBlocksRevealed = veinBlocksRevealed,
				RevealRadiusPasses = 0L,
				RevealPlayersChecked = 0L,
				RevealDesiredBlocks = 0L,
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
				VisibilityPlayersPerTickLimit = 8,
				VisibilityChunksPerPlayerPassLimit = 64,
				VisibilityCandidatesPerPlayerPassLimit = 1024,
				VisibilityIndexBuildsPerTickLimit = 2,
				BoundaryRefreshesPending = pendingBoundaryRefreshes.Count,
				BoundaryRefreshCellsEvaluated = boundaryRefreshCellsEvaluated,
				BoundaryRefreshOverflowInvalidations = boundaryRefreshOverflowInvalidations,
				BoundaryRefreshesPerTickLimit = 4,
				ExposedIndexCandidates = ((IEnumerable<OreIndexEntry>)oreIndexCache.Values).Sum((System.Func<OreIndexEntry, long>)((OreIndexEntry x) => x.ExposedIndices.Count)),
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
				ClientSyncFallbacks = 0L,
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
				DiscoveryDistinctChunks = discoveryOwners.Count,
				DiscoveryInvalidationOwnersVisited = discoveryInvalidationOwnersVisited,
				RevisionEntries = revisions.Count,
				RevisionCompactions = revisionCompactions,
				SyncProtocolVersion = 3,
				SyncCapability = "sync-remesh-v3",
				SyncBuildMarker = "ac1127-oreshield-v329-perf1",
				RevealDispatchAttempts = revealDispatchAttempts,
				RevealRecipientResolveFailures = revealRecipientResolveFailures,
				RevealChunkNotSentSkips = clientSyncChunkNotSentSkips,
				RevealStaleKeySkips = revealStaleKeySkips,
				RevealDirectPacketsSent = 0L,
				RevealDirectPacketErrors = 0L,
				RevealClientSyncUnsupportedSkips = revealClientSyncUnsupportedSkips
			};
		}
	}

	public void RecordChunkHookInvocation()
	{
		lock (sync)
		{
			chunkMaskHookCalls++;
		}
	}

	public void RecordUniqueChunkPacket()
	{
		lock (sync)
		{
			chunkMaskUniquePackets++;
		}
	}

	internal Dictionary<string, Dictionary<ChunkKey, OreShieldDiscoveryCells>> ExportCompactDiscoveryState()
	{
		lock (sync)
		{
			return playerDiscoveries.ToDictionary((KeyValuePair<string, Dictionary<ChunkKey, OreShieldDiscoveryCells>> p) => p.Key, (KeyValuePair<string, Dictionary<ChunkKey, OreShieldDiscoveryCells>> p) => p.Value.ToDictionary((KeyValuePair<ChunkKey, OreShieldDiscoveryCells> c) => c.Key, (KeyValuePair<ChunkKey, OreShieldDiscoveryCells> c) => c.Value.Clone()), StringComparer.Ordinal);
		}
	}

	internal void ImportCompactDiscoveryState(Dictionary<string, Dictionary<ChunkKey, OreShieldDiscoveryCells>> state)
	{
		lock (sync)
		{
			foreach (var (text2, dictionary2) in state)
			{
				Dictionary<ChunkKey, OreShieldDiscoveryCells> dictionary3 = new Dictionary<ChunkKey, OreShieldDiscoveryCells>();
				foreach (var (chunkKey2, oreShieldDiscoveryCells2) in dictionary2)
				{
					dictionary3[chunkKey2] = oreShieldDiscoveryCells2.Clone();
					discoveryCells += oreShieldDiscoveryCells2.Count;
					TrackDiscoveryOwnerNoLock(text2, chunkKey2);
				}
				playerDiscoveries[text2] = dictionary3;
			}
		}
	}

	public Dictionary<string, List<BlockPos>> ExportDiscoveryState()
	{
		Dictionary<string, List<BlockPos>> dictionary = new Dictionary<string, List<BlockPos>>(StringComparer.Ordinal);
		lock (sync)
		{
			foreach (var (key, dictionary3) in playerDiscoveries)
			{
				List<BlockPos> list = new List<BlockPos>();
				foreach (var (chunk, oreShieldDiscoveryCells2) in dictionary3)
				{
					foreach (int item in oreShieldDiscoveryCells2)
					{
						if ((uint)item < (uint)chunkVolume)
						{
							list.Add(BlockPosFromChunkLocalIndex(chunk, item));
						}
					}
				}
				if (list.Count > 0)
				{
					dictionary[key] = list;
				}
			}
		}
		return dictionary;
	}

	public void ImportDiscoveryState(Dictionary<string, List<BlockPos>>? state)
	{
		if (state == null || state.Count == 0)
		{
			return;
		}
		lock (sync)
		{
			foreach (var (text2, list2) in state)
			{
				if (string.IsNullOrWhiteSpace(text2) || list2 == null || list2.Count == 0)
				{
					continue;
				}
				if (!playerDiscoveries.TryGetValue(text2, out Dictionary<ChunkKey, OreShieldDiscoveryCells> value))
				{
					value = (playerDiscoveries[text2] = new Dictionary<ChunkKey, OreShieldDiscoveryCells>());
				}
				foreach (BlockPos item in list2)
				{
					if (!(item == null))
					{
						WorldBlockKey key = new WorldBlockKey(item.X, item.Y, item.Z, item.dimension);
						ChunkKey chunkKey = ChunkFor(key);
						if (!value.TryGetValue(chunkKey, out var value2))
						{
							value2 = (value[chunkKey] = new OreShieldDiscoveryCells());
							TrackDiscoveryOwnerNoLock(text2, chunkKey);
						}
						if (value2.Add(LocalIndex(key)))
						{
							discoveryCells++;
						}
					}
				}
			}
		}
	}

	public void FailClosedChunkPacket(string? playerUid, ServerChunk sourceChunk, Packet_ServerChunk packet, string reason)
	{
		if (packet == null || sourceChunk == null)
		{
			return;
		}
		int[] array = ArrayPool<int>.Shared.Rent(15360);
		ChunkDataLayer chunkDataLayer = new ChunkDataLayer(dataPool);
		try
		{
			chunkDataLayer.PopulateWithAir();
			ChunkKey chunk = new ChunkKey(packet.X, packet.Y, packet.Z);
			OreShieldDiscoveryCells discoveryIndicesSnapshot = GetDiscoveryIndicesSnapshot(playerUid, chunk);
			int num = 0;
			long num2 = 0L;
			long num3 = 0L;
			long num4 = 0L;
			for (int i = 0; i < chunkVolume; i++)
			{
				int num5 = ((IWorldChunk)sourceChunk).UnpackAndReadBlock(i, 1);
				int hostId = num5;
				if (IsProtectedOreId(num5))
				{
					BlockPos position = BlockPosFromChunkLocalIndex(chunk, i);
					if (!IsPlayerPlacedOre(position) && (discoveryIndicesSnapshot == null || !discoveryIndicesSnapshot.Contains(i)))
					{
						num++;
						if (!palette.TryGetHost(num5, out hostId) || hostId == num5)
						{
							hostId = 0;
							num2++;
							if (config.OreShieldLeakAuditEnabled)
							{
								num3++;
								num4++;
							}
						}
						else if (config.OreShieldLeakAuditEnabled)
						{
							num3++;
						}
					}
				}
				chunkDataLayer.SetUnsafe(i, hostId);
			}
			byte[] blocks = ChunkDataLayer.Compress(chunkDataLayer, array);
			packet.Compver = 2;
			packet.Empty = 0;
			packet.SetBlocks(blocks);
			lock (sync)
			{
				failClosedChunkPackets++;
				failClosedCells += num2;
				leakAuditChecks += num3;
				leakAuditViolations += num4;
				if (num > 0)
				{
					chunksMasked++;
					virtualizedBlocks += num;
					hiddenRealOreBlocks += num;
				}
			}
		}
		finally
		{
			try
			{
				dataPool.FreeArrays(chunkDataLayer);
			}
			catch
			{
			}
			ArrayPool<int>.Shared.Return(array);
		}
	}

	public void MaskChunkForPlayer(IServerPlayer? player, ServerChunk sourceChunk, Packet_ServerChunk packet)
	{
		MaskChunkForPlayerUid(player?.PlayerUID, sourceChunk, packet);
	}

	public void MaskChunkForPlayerUid(string? playerUid, ServerChunk sourceChunk, Packet_ServerChunk packet)
	{
		if (!config.OreShieldEnabled || packet.Empty != 0 || packet.Blocks == null)
		{
			return;
		}
		lock (sync)
		{
			chunkMaskEligiblePackets++;
		}
		if (packet.Compver != 2)
		{
			lock (sync)
			{
				unsupportedCompressionPackets++;
			}
			FailClosedChunkPacket(playerUid, sourceChunk, packet, $"unsupported compression v{packet.Compver}");
			return;
		}
		ChunkKey chunkKey = new ChunkKey(packet.X, packet.Y, packet.Z);
		ulong num = FingerprintBlocks(packet.Blocks);
		bool flag = HasAnyDiscoveryInChunk(playerUid, chunkKey);
		long num2;
		lock (sync)
		{
			num2 = RevisionNoLock(chunkKey);
			if (!flag && config.OreShieldChunkCacheEnabled && cache.TryGetValue(chunkKey, out LinkedListNode<CacheEntry> value))
			{
				CacheEntry value2 = value.Value;
				bool flag2 = oreIndexCache.TryGetValue(chunkKey, out OreIndexEntry value3) && value3.SourceFingerprint == num;
				if ((value2.SourceFingerprint == num && value2.RevisionSignature == num2) & flag2)
				{
					TouchNoLock(value);
					TouchOreIndexNoLock(chunkKey);
					cacheHits++;
					oreIndexHits++;
					if (!value2.PassThrough && value2.ClientBlocks != null)
					{
						packet.SetBlocks(value2.ClientBlocks);
					}
					return;
				}
				RemoveNoLock(value);
			}
			cacheMisses++;
			chunksInspected++;
		}
		int[] array = ArrayPool<int>.Shared.Rent(chunkVolume);
		int[] array2 = ArrayPool<int>.Shared.Rent(15360);
		ChunkDataLayer chunkDataLayer = new ChunkDataLayer(dataPool);
		try
		{
			byte[] blocks = packet.Blocks;
			unpackBlocks(array, blocks, packet.LightSat, packet.Compver);
			HashSet<int> hashSet;
			bool flag3;
			lock (sync)
			{
				if (oreIndexCache.TryGetValue(chunkKey, out OreIndexEntry value4) && value4.SourceFingerprint == num)
				{
					hashSet = value4.Indices;
					flag3 = true;
					TouchOreIndexNoLock(chunkKey);
					oreIndexHits++;
				}
				else
				{
					hashSet = new HashSet<int>();
					flag3 = false;
					oreIndexMisses++;
				}
			}
			if (!flag3)
			{
				hashSet = new HashSet<int>();
				for (int i = 0; i < chunkVolume; i++)
				{
					int blockId = array[i];
					if (IsProtectedOreId(blockId))
					{
						BlockPos position = BlockPosFromChunkLocalIndex(chunkKey, i);
						if (!IsPlayerPlacedOre(position))
						{
							hashSet.Add(i);
						}
					}
				}
				OreShieldExposedIndex exposedIndices = new OreShieldExposedIndex();
				lock (sync)
				{
					if (RevisionNoLock(chunkKey) == num2)
					{
						SetOreIndexNoLock(chunkKey, num, hashSet, exposedIndices);
					}
				}
			}
			if (hashSet.Count == 0)
			{
				if (flag)
				{
					return;
				}
				lock (sync)
				{
					if (RevisionNoLock(chunkKey) == num2)
					{
						AddCacheNoLock(chunkKey, num2, num, null, passThrough: true, 0, 0);
					}
					passThroughChunks++;
					return;
				}
			}
			chunkDataLayer.PopulateWithAir();
			for (int j = 0; j < chunkVolume; j++)
			{
				chunkDataLayer.SetUnsafe(j, array[j]);
			}
			OreShieldDiscoveryCells discoveryIndicesSnapshot = GetDiscoveryIndicesSnapshot(playerUid, chunkKey);
			int num3 = 0;
			int num4 = 0;
			long num5 = 0L;
			long num6 = 0L;
			long num7 = 0L;
			foreach (int item in hashSet)
			{
				if ((uint)item >= (uint)chunkVolume)
				{
					continue;
				}
				int num8 = array[item];
				if (!IsProtectedOreId(num8))
				{
					continue;
				}
				BlockPos position2 = BlockPosFromChunkLocalIndex(chunkKey, item);
				if (IsPlayerPlacedOre(position2) || (discoveryIndicesSnapshot != null && discoveryIndicesSnapshot.Contains(item)))
				{
					continue;
				}
				if (!palette.TryGetHost(num8, out var hostId) || hostId == num8)
				{
					chunkDataLayer.SetUnsafe(item, 0);
					num5++;
					if (config.OreShieldLeakAuditEnabled)
					{
						num6++;
						num7++;
					}
					num3++;
					num4++;
				}
				else
				{
					if (array[item] != hostId)
					{
						chunkDataLayer.SetUnsafe(item, hostId);
						num3++;
					}
					if (config.OreShieldLeakAuditEnabled)
					{
						num6++;
					}
					num4++;
				}
			}
			lock (sync)
			{
				failClosedCells += num5;
				leakAuditChecks += num6;
				leakAuditViolations += num7;
			}
			if (num3 == 0)
			{
				return;
			}
			byte[] array3 = ChunkDataLayer.Compress(chunkDataLayer, array2);
			packet.SetBlocks(array3);
			lock (sync)
			{
				if (!flag && RevisionNoLock(chunkKey) == num2)
				{
					AddCacheNoLock(chunkKey, num2, num, array3, passThrough: false, 0, 0);
				}
				chunksMasked++;
				virtualizedBlocks += num3;
				hiddenRealOreBlocks += num4;
			}
		}
		finally
		{
			try
			{
				dataPool.FreeArrays(chunkDataLayer);
			}
			catch
			{
			}
			ArrayPool<int>.Shared.Return(array);
			ArrayPool<int>.Shared.Return(array2);
		}
		ServerMain.FrameProfiler.Mark("newfies-oreshield-v3-chunks");
	}

	public bool IsProtectedOreIdForSafety(int blockId)
	{
		return IsProtectedOreId(blockId);
	}

	public bool FailClosedSingleIfProtected(int blockId, BlockPos position)
	{
		if (position == null || !IsProtectedOreId(blockId) || IsPlayerPlacedOre(position))
		{
			return false;
		}
		return true;
	}

	public bool FailClosedBulkProtectedUpdates(List<BlockPos> positions)
	{
		if (positions == null || positions.Count == 0)
		{
			return false;
		}
		for (int num = positions.Count - 1; num >= 0; num--)
		{
			BlockPos blockPos = positions[num];
			if (!(blockPos == null))
			{
				int num2 = ReadWorldBlock(blockPos);
				if (num2 >= 0 && IsMaskableProtectedOre(num2, blockPos))
				{
					positions.RemoveAt(num);
				}
			}
		}
		return positions.Count > 0;
	}

	public bool RequiresSingleVirtualization(int blockId, int x, int y, int z)
	{
		return RequiresSingleVirtualization(blockId, new BlockPos(x, y, z));
	}

	public bool RequiresSingleVirtualization(int blockId, BlockPos changed)
	{
		if (!config.OreShieldEnabled || changed == null)
		{
			return false;
		}
		if (IsMaskableProtectedOre(blockId, changed))
		{
			return true;
		}
		BlockPos blockPos = changed.Copy();
		BlockFacing[] aLLFACES = BlockFacing.ALLFACES;
		foreach (BlockFacing blockFacing in aLLFACES)
		{
			blockPos.Set(changed);
			blockFacing.IterateThruFacingOffsets(blockPos);
			int blockId2 = ReadWorldBlock(blockPos);
			if (IsMaskableProtectedOre(blockId2, blockPos))
			{
				return true;
			}
		}
		return false;
	}

	public bool RequiresBulkVirtualization(List<BlockPos> positions)
	{
		if (!config.OreShieldEnabled || positions == null || positions.Count == 0)
		{
			return false;
		}
		BlockPos blockPos = positions[0].Copy();
		foreach (BlockPos position in positions)
		{
			if (position == null)
			{
				continue;
			}
			int blockId = ReadWorldBlock(position);
			if (IsMaskableProtectedOre(blockId, position))
			{
				return true;
			}
			BlockFacing[] aLLFACES = BlockFacing.ALLFACES;
			foreach (BlockFacing blockFacing in aLLFACES)
			{
				blockPos.Set(position);
				blockFacing.IterateThruFacingOffsets(blockPos);
				int blockId2 = ReadWorldBlock(blockPos);
				if (IsMaskableProtectedOre(blockId2, blockPos))
				{
					return true;
				}
			}
		}
		return false;
	}

	public bool FilterBulkProtectedUpdates(List<BlockPos> positions, int packetId)
	{
		if (!config.OreShieldEnabled || positions == null || positions.Count == 0)
		{
			return true;
		}
		List<BlockPos> list = new List<BlockPos>();
		for (int num = positions.Count - 1; num >= 0; num--)
		{
			BlockPos blockPos = positions[num];
			if (!(blockPos == null))
			{
				int blockId = ReadWorldBlock(blockPos);
				if (IsMaskableProtectedOre(blockId, blockPos))
				{
					list.Add(blockPos.Copy());
					positions.RemoveAt(num);
				}
			}
		}
		if (list.Count == 0)
		{
			return true;
		}
		foreach (BlockPos item in list)
		{
			InvalidateAround(item);
		}
		WorldBlockKey[] array = list.Select((BlockPos pos) => new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension)).ToArray();
		foreach (IServerPlayer item2 in OnlineServerPlayers())
		{
			SendPlayerViewCorrections(item2, array);
			lock (sync)
			{
				rewrittenUpdatePackets += array.Length;
			}
		}
		return positions.Count > 0;
	}

	private bool IsProtectedOreId(int blockId)
	{
		return blockId >= 0 && blockId < palette.ProtectedOre.Length && palette.ProtectedOre[blockId];
	}

	private bool IsMaskableProtectedOre(int blockId, BlockPos position)
	{
		return IsProtectedOreId(blockId) && !IsPlayerPlacedOre(position);
	}

	private void RememberProtectedOreBreak(BlockPos position)
	{
		if (position == null)
		{
			return;
		}
		long now = Environment.TickCount64;
		WorldBlockKey key = new WorldBlockKey(position.X, position.Y, position.Z, position.dimension);
		lock (sync)
		{
			recentProtectedOreBreaks[key] = now + 10000;
			if (recentProtectedOreBreaks.Count > 512)
			{
				WorldBlockKey[] array = (from pair in recentProtectedOreBreaks
					where pair.Value < now
					select pair.Key).Take(256).ToArray();
				foreach (WorldBlockKey key2 in array)
				{
					recentProtectedOreBreaks.Remove(key2);
				}
			}
		}
	}

	private bool ConsumeRecentProtectedOreBreak(BlockPos position)
	{
		if (position == null)
		{
			return false;
		}
		long tickCount = Environment.TickCount64;
		WorldBlockKey key = new WorldBlockKey(position.X, position.Y, position.Z, position.dimension);
		lock (sync)
		{
			if (!recentProtectedOreBreaks.TryGetValue(key, out var value))
			{
				return false;
			}
			recentProtectedOreBreaks.Remove(key);
			return value >= tickCount;
		}
	}

	private bool IsPlayerPlacedOre(BlockPos position)
	{
		if (position == null)
		{
			return false;
		}
		WorldBlockKey item = new WorldBlockKey(position.X, position.Y, position.Z, position.dimension);
		lock (sync)
		{
			return playerPlacedOreExemptions.Contains(item);
		}
	}

	public bool IsPlayerPlacedOreExempt(BlockPos position)
	{
		return IsPlayerPlacedOre(position);
	}

	public bool BeginPlayerOrePlacement(BlockPos position, int blockId)
	{
		if (!config.OreShieldEnabled || position == null || !IsProtectedOreId(blockId))
		{
			return false;
		}
		bool result = MarkPlayerPlacedOreExemption(position, persist: false);
		InvalidateAround(position);
		return result;
	}

	public void CompletePlayerOrePlacement(BlockPos position, bool addedExemption, bool succeeded)
	{
		if (position == null || !config.OreShieldEnabled)
		{
			return;
		}
		int blockId = ReadWorldBlock(position);
		if (succeeded && IsProtectedOreId(blockId))
		{
			MarkPlayerPlacedOreExemption(position, persist: true);
			InvalidateAround(position);
			return;
		}
		if (addedExemption)
		{
			RemovePlayerPlacedOreExemption(position, persist: false);
		}
		InvalidateAround(position);
	}

	public int MaskSingle(IServerPlayer player, int blockId, int x, int y, int z)
	{
		return MaskSingle(player, blockId, new BlockPos(x, y, z));
	}

	public int MaskSingle(IServerPlayer player, int blockId, BlockPos changed)
	{
		if (!config.OreShieldEnabled || blockId < 0 || changed == null)
		{
			return blockId;
		}
		InvalidateAround(changed);
		List<(BlockPos, int)> list = new List<(BlockPos, int)>(6);
		BlockPos blockPos = changed.Copy();
		BlockFacing[] aLLFACES = BlockFacing.ALLFACES;
		foreach (BlockFacing blockFacing in aLLFACES)
		{
			blockPos.Set(changed);
			blockFacing.IterateThruFacingOffsets(blockPos);
			int num = ReadWorldBlock(blockPos);
			if (num >= 0 && num < palette.ProtectedOre.Length && palette.ProtectedOre[num])
			{
				list.Add((blockPos.Copy(), ClientViewForPlayer(player, num, blockPos)));
			}
		}
		int num2 = ClientViewForPlayer(player, blockId, changed);
		if (config.OreShieldLeakAuditEnabled && IsMaskableProtectedOre(blockId, changed))
		{
			WorldBlockKey key = new WorldBlockKey(changed.X, changed.Y, changed.Z, changed.dimension);
			lock (sync)
			{
				leakAuditChecks++;
				if (!IsDiscovered(player.PlayerUID, key) && num2 == blockId)
				{
					leakAuditViolations++;
				}
			}
		}
		if (list.Count > 0)
		{
			SendPlayerViewCorrections(player, list.Select(((BlockPos Pos, int BlockId) c) => new WorldBlockKey(c.Pos.X, c.Pos.Y, c.Pos.Z, c.Pos.dimension)), reassertSmallSet: true);
		}
		return num2;
	}

	public void OnColumnLoaded(ChunkColumnLoadRequest request, int dimension)
	{
		if (!config.OreShieldEnabled || server.ShuttingDown)
		{
			return;
		}
		int num = dimension * 1024;
		lock (sync)
		{
			for (int i = 0; i < server.WorldMap.ChunkMapSizeY; i++)
			{
				int y = i + num;
				QueueBoundaryRefreshNoLock(new ChunkKey(request.ChunkX - 1, y, request.ChunkZ), BlockFacing.EAST);
				QueueBoundaryRefreshNoLock(new ChunkKey(request.ChunkX + 1, y, request.ChunkZ), BlockFacing.WEST);
				QueueBoundaryRefreshNoLock(new ChunkKey(request.ChunkX, y, request.ChunkZ - 1), BlockFacing.SOUTH);
				QueueBoundaryRefreshNoLock(new ChunkKey(request.ChunkX, y, request.ChunkZ + 1), BlockFacing.NORTH);
			}
		}
	}

	private void QueueBoundaryRefreshNoLock(ChunkKey chunk, BlockFacing face)
	{
		if (!oreIndexCache.TryGetValue(chunk, out OreIndexEntry value) || value.Indices.Count == 0)
		{
			return;
		}
		BoundaryRefreshKey item = new BoundaryRefreshKey(chunk, face.Index);
		if (!pendingBoundaryRefreshKeys.Contains(item))
		{
			if (pendingBoundaryRefreshes.Count >= 4096)
			{
				RemoveOreIndexNoLock(chunk);
				boundaryRefreshOverflowInvalidations++;
			}
			else
			{
				pendingBoundaryRefreshKeys.Add(item);
				pendingBoundaryRefreshes.Enqueue(item);
			}
		}
	}

	private void ProcessBoundaryRefreshes()
	{
		bool flag = false;
		BoundaryRefreshKey item;
		for (int i = 0; i < 4; flag |= RefreshBoundaryExposure(item.Chunk, BlockFacing.ALLFACES[item.Face]), i++)
		{
			lock (sync)
			{
				if (pendingBoundaryRefreshes.Count == 0)
				{
					break;
				}
				item = pendingBoundaryRefreshes.Dequeue();
				pendingBoundaryRefreshKeys.Remove(item);
				continue;
			}
		}
		if (!flag)
		{
			return;
		}
		lock (sync)
		{
			foreach (PlayerVisibilityState value in playerVisibilityStates.Values)
			{
				value.LastScanMs = 0L;
			}
		}
	}

	private bool RefreshBoundaryExposure(ChunkKey key, BlockFacing face)
	{
		Span<int> destination = stackalloc int[1024];
		int num = 0;
		OreIndexEntry value;
		lock (sync)
		{
			if (!oreIndexCache.TryGetValue(key, out value) || value.Indices.Count == 0)
			{
				return false;
			}
			bool fixedX = face == BlockFacing.WEST || face == BlockFacing.EAST;
			int coordinate = ((face != BlockFacing.WEST && face != BlockFacing.NORTH) ? chunkMask : 0);
			num = OreShieldBoundaryWindow.CopyHorizontalFace(value.Indices, fixedX, coordinate, destination);
		}
		BoundaryRefreshKey boundaryRefreshKey = new BoundaryRefreshKey(key, face.Index);
		int num2;
		lock (sync)
		{
			num2 = boundaryCursors.GetValueOrDefault(boundaryRefreshKey);
		}
		bool flag = false;
		int num3 = 0;
		while (num2 < num && num3 < 128 && !VisibilityTimeExpired)
		{
			int index = destination[num2];
			BlockPos position = BlockPosFromChunkLocalIndex(key, index);
			int blockId = ReadWorldBlock(position);
			bool flag2 = IsMaskableProtectedOre(blockId, position) && IsWorldExposed(position);
			lock (sync)
			{
				if (oreIndexCache.TryGetValue(key, out OreIndexEntry value2) && value2 == value)
				{
					flag |= (flag2 ? value2.ExposedIndices.Add(index) : value2.ExposedIndices.Remove(index));
				}
			}
			num2++;
			num3++;
		}
		lock (sync)
		{
			boundaryRefreshCellsEvaluated += num3;
			if (num2 < num)
			{
				boundaryCursors[boundaryRefreshKey] = num2;
				if (pendingBoundaryRefreshKeys.Add(boundaryRefreshKey))
				{
					pendingBoundaryRefreshes.Enqueue(boundaryRefreshKey);
				}
			}
			else
			{
				boundaryCursors.Remove(boundaryRefreshKey);
			}
		}
		return flag;
	}

	public void RevealAfterPlayerBreak(IServerPlayer player, BlockPos brokenPos, int oldBlockId)
	{
		if (config.OreShieldEnabled && player != null && !(brokenPos == null))
		{
			lock (sync)
			{
				miningRevealPulses++;
			}
			bool flag = IsProtectedOreId(oldBlockId);
			if (flag)
			{
				RemovePlayerPlacedOreExemption(brokenPos, persist: true);
			}
			InvalidateAround(brokenPos);
			if (flag)
			{
				RememberProtectedOreBreak(brokenPos);
				SyncAuthoritativeWorldCellToAllClients(brokenPos);
			}
			List<WorldBlockKey> list = AuthorizeBreakerAdjacentDiscoveries(player, brokenPos);
			QueueWorldChange(brokenPos, reassert: true);
			if (list.Count > 0)
			{
				SendPlayerViewCorrections(player, list, reassertSmallSet: true);
			}
			geometryChanged = true;
			if (config.OreShieldRevealWholeVeinOnMine & flag)
			{
				RevealConnectedVeinAfterMine(player, brokenPos, oldBlockId);
			}
		}
	}

	public void RevealAfterPlayerPlace(IServerPlayer player, BlockPos placedPos)
	{
		if (config.OreShieldEnabled && !(placedPos == null))
		{
			int blockId = ReadWorldBlock(placedPos);
			bool flag = IsProtectedOreId(blockId);
			bool flag2 = ConsumeRecentProtectedOreBreak(placedPos);
			if (flag)
			{
				MarkPlayerPlacedOreExemption(placedPos, persist: true);
			}
			else
			{
				RemovePlayerPlacedOreExemption(placedPos, persist: true);
			}
			InvalidateAround(placedPos);
			if (flag2)
			{
				SyncAuthoritativeWorldCellToAllClients(placedPos);
			}
			QueueWorldChange(placedPos, flag);
			if (flag && player != null)
			{
				WorldBlockKey worldBlockKey = new WorldBlockKey(placedPos.X, placedPos.Y, placedPos.Z, placedPos.dimension);
				SendPlayerViewCorrections(player, new WorldBlockKey[1] { worldBlockKey }, reassertSmallSet: true);
			}
			geometryChanged = true;
		}
	}

	public void SyncExposureAfterWorldChange(BlockPos changedPos)
	{
		if (config.OreShieldEnabled && !(changedPos == null))
		{
			InvalidateAround(changedPos);
			QueueWorldChange(changedPos, reassert: false);
		}
	}

	private List<WorldBlockKey> AuthorizeBreakerAdjacentDiscoveries(IServerPlayer player, BlockPos brokenPos)
	{
		List<WorldBlockKey> list = new List<WorldBlockKey>(6);
		if (player == null || brokenPos == null)
		{
			return list;
		}
		string text = player.PlayerUID ?? string.Empty;
		if (text.Length == 0)
		{
			return list;
		}
		BlockPos blockPos = brokenPos.Copy();
		BlockFacing[] aLLFACES = BlockFacing.ALLFACES;
		foreach (BlockFacing blockFacing in aLLFACES)
		{
			blockPos.Set(brokenPos);
			blockFacing.IterateThruFacingOffsets(blockPos);
			int blockId = ReadWorldBlock(blockPos);
			if (IsMaskableProtectedOre(blockId, blockPos))
			{
				WorldBlockKey worldBlockKey = new WorldBlockKey(blockPos.X, blockPos.Y, blockPos.Z, blockPos.dimension);
				MarkDiscoveredWithoutLosCounter(text, worldBlockKey);
				list.Add(worldBlockKey);
			}
		}
		if (list.Count > 0)
		{
			lock (sync)
			{
				miningRevealCorrections += list.Count;
			}
		}
		return list;
	}

	private List<WorldBlockKey> CollectAffectedProtectedOre(BlockPos changedPos)
	{
		List<WorldBlockKey> result = new List<WorldBlockKey>(7);
		HashSet<WorldBlockKey> seen = new HashSet<WorldBlockKey>();
		Consider(changedPos);
		BlockPos blockPos = changedPos.Copy();
		BlockFacing[] aLLFACES = BlockFacing.ALLFACES;
		foreach (BlockFacing blockFacing in aLLFACES)
		{
			blockPos.Set(changedPos);
			blockFacing.IterateThruFacingOffsets(blockPos);
			Consider(blockPos);
		}
		lock (sync)
		{
			exposureSyncEvents++;
			exposureBlocksEvaluated += result.Count;
		}
		return result;
		void Consider(BlockPos pos)
		{
			int blockId = ReadWorldBlock(pos);
			if (IsMaskableProtectedOre(blockId, pos))
			{
				WorldBlockKey item = new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension);
				if (seen.Add(item))
				{
					result.Add(item);
				}
			}
		}
	}

	private void SyncExposureToAllClients(IEnumerable<WorldBlockKey> keys, bool reassertSmallSet)
	{
		WorldBlockKey[] array = keys?.Distinct().ToArray() ?? Array.Empty<WorldBlockKey>();
		if (array.Length == 0)
		{
			return;
		}
		long num;
		lock (sync)
		{
			num = revealCorrectionPackets;
		}
		foreach (IServerPlayer item in OnlineServerPlayers())
		{
			SendPlayerViewCorrections(item, array, reassertSmallSet);
		}
		lock (sync)
		{
			long num2 = Math.Max(0L, revealCorrectionPackets - num);
			exposureCorrectionsSent += num2;
		}
	}

	private void SyncAuthoritativeWorldCellToAllClients(BlockPos position)
	{
		if (disposed || position == null)
		{
			return;
		}
		lock (sync)
		{
			WorldBlockKey item = new WorldBlockKey(position.X, position.Y, position.Z, position.dimension);
			if (pendingAuthoritativeCells.Count >= 4096 && !pendingAuthoritativeCells.Contains(item))
			{
				worldChangeOverflows++;
				return;
			}
			pendingAuthoritativeCells.Add(item);
			if (authoritativeFlushQueued)
			{
				return;
			}
			authoritativeFlushQueued = true;
		}
		api.Event.RegisterCallback((float _) =>
		{
			FlushAuthoritativeCells();
		}, 25);
	}

	private void FlushAuthoritativeCells()
	{
		List<BlockPos> positions;
		lock (sync)
		{
			authoritativeFlushQueued = false;
			if (disposed || pendingAuthoritativeCells.Count == 0)
			{
				return;
			}
			WorldBlockKey[] array = pendingAuthoritativeCells.Take(256).ToArray();
			WorldBlockKey[] array2 = array;
			foreach (WorldBlockKey item in array2)
			{
				pendingAuthoritativeCells.Remove(item);
			}
			positions = array.Select((WorldBlockKey key) => new BlockPos(key.X, key.Y, key.Z, key.Dimension)).ToList();
			authoritativeCorrectionFlushes++;
		}
		foreach (IServerPlayer item2 in OnlineServerPlayers())
		{
			TrySendClientSync(item2, positions, 0, stabilize: false);
		}
		lock (sync)
		{
			if (pendingAuthoritativeCells.Count == 0 || disposed || authoritativeFlushQueued)
			{
				return;
			}
			authoritativeFlushQueued = true;
		}
		api.Event.RegisterCallback((float _) =>
		{
			FlushAuthoritativeCells();
		}, 10);
	}

	private void RevealConnectedVeinAfterMine(IServerPlayer player, BlockPos mined, int minedId)
	{
		string playerUID = player.PlayerUID;
		if (!pendingVeins.TryGetValue(playerUID, out VeinJob value))
		{
			if (pendingVeins.Count >= 64)
			{
				worldChangeOverflows++;
				return;
			}
			value = (pendingVeins[playerUID] = new VeinJob());
		}
		WorldBlockKey worldBlockKey = new WorldBlockKey(mined.X, mined.Y, mined.Z, mined.dimension);
		if (value.SeedKeys.Count < 64 && value.SeedKeys.Add(worldBlockKey))
		{
			value.Seeds.Enqueue((worldBlockKey, minedId));
		}
	}

	private void ProcessVeins()
	{
		if (!config.OreShieldRevealWholeVeinOnMine)
		{
			pendingVeins.Clear();
		}
		else
		{
			if (pendingVeins.Count == 0 || VisibilityTimeExpired)
			{
				return;
			}
			KeyValuePair<string, VeinJob> keyValuePair = pendingVeins.MinBy((KeyValuePair<string, VeinJob> p) => p.Value.LastServed);
			VeinJob value = keyValuePair.Value;
			value.LastServed = ++veinServiceSequence;
			if (!(api.World.PlayerByUid(keyValuePair.Key) is IServerPlayer player))
			{
				pendingVeins.Remove(keyValuePair.Key);
				return;
			}
			List<WorldBlockKey> list = new List<WorldBlockKey>();
			int num = Math.Clamp(config.OreShieldMaxVeinRevealBlocks, 64, 8192);
			for (int num2 = 0; num2 < 2; num2++)
			{
				if (value.Seeds.Count <= 0)
				{
					break;
				}
				if (value.Visited.Count >= num)
				{
					break;
				}
				if (VisibilityTimeExpired)
				{
					break;
				}
				(WorldBlockKey, int) tuple = value.Seeds.Dequeue();
				EnqueueTouchingProtectedOre(new BlockPos(tuple.Item1.X, tuple.Item1.Y, tuple.Item1.Z, tuple.Item1.Dimension), tuple.Item2, value.Visited, value.Queue);
			}
			for (int num3 = 0; num3 < 64; num3++)
			{
				if (value.Queue.Count <= 0)
				{
					break;
				}
				if (value.Revealed >= num)
				{
					break;
				}
				if (VisibilityTimeExpired)
				{
					break;
				}
				WorldBlockKey worldBlockKey = value.Queue.Dequeue();
				BlockPos blockPos = new BlockPos(worldBlockKey.X, worldBlockKey.Y, worldBlockKey.Z, worldBlockKey.Dimension);
				int num4 = ReadWorldBlock(blockPos);
				if (IsMaskableProtectedOre(num4, blockPos))
				{
					value.Revealed++;
					if (MarkDiscoveredWithoutLosCounter(keyValuePair.Key, worldBlockKey))
					{
						list.Add(worldBlockKey);
					}
					if (value.Visited.Count < num)
					{
						EnqueueTouchingProtectedOre(blockPos, num4, value.Visited, value.Queue);
					}
				}
			}
			if (list.Count > 0)
			{
				lock (sync)
				{
					veinBlocksRevealed += list.Count;
					miningRevealCorrections += list.Count;
				}
				SendPlayerViewCorrections(player, list);
			}
			pendingVeins.Remove(keyValuePair.Key);
			if ((value.Queue.Count <= 0 && (value.Seeds.Count <= 0 || value.Visited.Count >= num)) || value.Revealed >= num)
			{
				lock (sync)
				{
					veinDiscoveries++;
					if (value.Revealed >= num || value.Visited.Count >= num)
					{
						veinBudgetLimitedJobs++;
					}
					return;
				}
			}
			pendingVeins[keyValuePair.Key] = value;
		}
	}

	public void ResyncKnownDiscoveries(IServerPlayer player)
	{
		if (disposed || player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}
		lock (sync)
		{
			if (!pendingDiscoveryResyncs.ContainsKey(player.PlayerUID) && playerDiscoveries.TryGetValue(player.PlayerUID, out Dictionary<ChunkKey, OreShieldDiscoveryCells> value) && value.Count != 0)
			{
				pendingDiscoveryResyncs[player.PlayerUID] = new ResyncJob
				{
					Chunks = new Queue<ChunkKey>(value.Keys)
				};
			}
		}
	}

	private void ProcessDiscoveryResyncs()
	{
		KeyValuePair<string, ResyncJob> keyValuePair;
		lock (sync)
		{
			if (pendingDiscoveryResyncs.Count == 0 || VisibilityTimeExpired)
			{
				return;
			}
			keyValuePair = pendingDiscoveryResyncs.First();
		}
		ResyncJob value = keyValuePair.Value;
		if (!(api.World.PlayerByUid(keyValuePair.Key) is IServerPlayer player))
		{
			lock (sync)
			{
				pendingDiscoveryResyncs.Remove(keyValuePair.Key);
				value.Cells?.Dispose();
				return;
			}
		}
		List<WorldBlockKey> list = new List<WorldBlockKey>(64);
		while (list.Count < 64 && !VisibilityTimeExpired)
		{
			if (value.Cells == null)
			{
				lock (sync)
				{
					if (value.Chunks.Count == 0)
					{
						break;
					}
					value.Chunk = value.Chunks.Dequeue();
					if (playerDiscoveries.TryGetValue(keyValuePair.Key, out Dictionary<ChunkKey, OreShieldDiscoveryCells> value2) && value2.TryGetValue(value.Chunk, out var value3))
					{
						value.Cells = value3.Clone().GetEnumerator();
					}
					goto IL_0188;
				}
			}
			goto IL_019a;
			IL_019a:
			if (!value.Cells.MoveNext())
			{
				value.Cells.Dispose();
				value.Cells = null;
			}
			else
			{
				BlockPos blockPos = BlockPosFromChunkLocalIndex(value.Chunk, value.Cells.Current);
				list.Add(new WorldBlockKey(blockPos.X, blockPos.Y, blockPos.Z, blockPos.dimension));
			}
			continue;
			IL_0188:
			if (value.Cells == null)
			{
				continue;
			}
			goto IL_019a;
		}
		if (list.Count > 0)
		{
			SendPlayerViewCorrections(player, list);
		}
		lock (sync)
		{
			pendingDiscoveryResyncs.Remove(keyValuePair.Key);
			if (value.Cells != null || value.Chunks.Count > 0)
			{
				pendingDiscoveryResyncs[keyValuePair.Key] = value;
			}
		}
	}

	public void RemovePlayerConnectionState(string? playerUid)
	{
		if (string.IsNullOrWhiteSpace(playerUid))
		{
			return;
		}
		lock (sync)
		{
			playerVisibilityStates.Remove(playerUid);
			pendingVeins.Remove(playerUid);
			if (pendingDiscoveryResyncs.Remove(playerUid, out ResyncJob value))
			{
				value.Cells?.Dispose();
			}
			clientSyncLastResyncRequestMs.Remove(playerUid);
			string failurePrefix = playerUid + "|";
			string[] array = clientSyncLastFailureResyncMs.Keys.Where((string k) => k.StartsWith(failurePrefix, StringComparison.Ordinal)).ToArray();
			foreach (string key in array)
			{
				clientSyncLastFailureResyncMs.Remove(key);
			}
			ClientSyncPendingKey[] array2 = pendingClientSync.Keys.Where((ClientSyncPendingKey k) => string.Equals(k.PlayerUid, playerUid, StringComparison.Ordinal)).ToArray();
			foreach (ClientSyncPendingKey key2 in array2)
			{
				RemovePendingClientSyncNoLock(key2);
			}
			RemoveQueuedClientSyncNoLock(playerUid);
		}
	}

	public void RemovePlayerState(string? playerUid)
	{
		if (string.IsNullOrWhiteSpace(playerUid))
		{
			return;
		}
		lock (sync)
		{
			if (playerDiscoveries.Remove(playerUid, out Dictionary<ChunkKey, OreShieldDiscoveryCells> value))
			{
				foreach (var (chunk, oreShieldDiscoveryCells2) in value)
				{
					discoveryCells -= oreShieldDiscoveryCells2.Count;
					UntrackDiscoveryOwnerNoLock(playerUid, chunk);
				}
			}
			playerVisibilityStates.Remove(playerUid);
			pendingVeins.Remove(playerUid);
			if (pendingDiscoveryResyncs.Remove(playerUid, out ResyncJob value2))
			{
				value2.Cells?.Dispose();
			}
			clientSyncLastResyncRequestMs.Remove(playerUid);
			string failurePrefix = playerUid + "|";
			string[] array = clientSyncLastFailureResyncMs.Keys.Where((string k) => k.StartsWith(failurePrefix, StringComparison.Ordinal)).ToArray();
			foreach (string key in array)
			{
				clientSyncLastFailureResyncMs.Remove(key);
			}
			ClientSyncPendingKey[] array2 = pendingClientSync.Keys.Where((ClientSyncPendingKey k) => string.Equals(k.PlayerUid, playerUid, StringComparison.Ordinal)).ToArray();
			foreach (ClientSyncPendingKey key2 in array2)
			{
				RemovePendingClientSyncNoLock(key2);
			}
			RemoveQueuedClientSyncNoLock(playerUid);
		}
	}

	private void TrackPendingClientSyncNoLock(PendingClientSync pending)
	{
		pendingClientSync[new ClientSyncPendingKey(pending.PlayerUid, pending.Sequence)] = pending;
		clientSyncPendingByPlayer[pending.PlayerUid] = clientSyncPendingByPlayer.GetValueOrDefault(pending.PlayerUid) + 1;
		ClientSyncRecipientChunk key = new ClientSyncRecipientChunk(pending.PlayerUid, pending.Chunk);
		clientSyncPendingByChunk[key] = clientSyncPendingByChunk.GetValueOrDefault(key) + 1;
	}

	private void RemovePendingClientSyncNoLock(ClientSyncPendingKey key)
	{
		if (pendingClientSync.Remove(key, out PendingClientSync value))
		{
			int num = clientSyncPendingByPlayer.GetValueOrDefault(value.PlayerUid) - 1;
			if (num <= 0)
			{
				clientSyncPendingByPlayer.Remove(value.PlayerUid);
			}
			else
			{
				clientSyncPendingByPlayer[value.PlayerUid] = num;
			}
			ClientSyncRecipientChunk key2 = new ClientSyncRecipientChunk(value.PlayerUid, value.Chunk);
			num = clientSyncPendingByChunk.GetValueOrDefault(key2) - 1;
			if (num <= 0)
			{
				clientSyncPendingByChunk.Remove(key2);
			}
			else
			{
				clientSyncPendingByChunk[key2] = num;
			}
		}
	}

	private void RemoveQueuedClientSyncNoLock(string playerUid)
	{
		ClientSyncOutboxKey[] array = clientSyncOutbox.Keys.Where((ClientSyncOutboxKey k) => k.PlayerUid == playerUid).ToArray();
		foreach (ClientSyncOutboxKey key in array)
		{
			clientSyncQueuedCells -= clientSyncOutbox[key].Positions.Count;
			clientSyncOutbox.Remove(key);
		}
	}

	private void EnqueueTouchingProtectedOre(BlockPos center, int familyBlockId, HashSet<WorldBlockKey> visited, Queue<WorldBlockKey> queue)
	{
		BlockPos blockPos = center.Copy();
		for (int i = -1; i <= 1; i++)
		{
			for (int j = -1; j <= 1; j++)
			{
				for (int k = -1; k <= 1; k++)
				{
					if (i == 0 && j == 0 && k == 0)
					{
						continue;
					}
					blockPos.Set(center.X + i, center.Y + j, center.Z + k);
					WorldBlockKey item = new WorldBlockKey(blockPos.X, blockPos.Y, blockPos.Z, blockPos.dimension);
					if (visited.Count >= Math.Clamp(config.OreShieldMaxVeinRevealBlocks, 64, 8192))
					{
						break;
					}
					if (!visited.Contains(item))
					{
						int num = ReadWorldBlock(blockPos);
						if (IsMaskableProtectedOre(num, blockPos) && palette.SameVeinFamily(familyBlockId, num))
						{
							visited.Add(item);
							queue.Enqueue(item);
						}
					}
				}
			}
		}
	}

	private ChunkKey ChunkFor(WorldBlockKey key)
	{
		int num = key.Y + key.Dimension * 32768;
		return new ChunkKey(key.X >> 5, num >> 5, key.Z >> 5);
	}

	private int LocalIndex(WorldBlockKey key)
	{
		int num = key.Y + key.Dimension * 32768;
		return Index(key.X & chunkMask, num & chunkMask, key.Z & chunkMask);
	}

	private OreShieldDiscoveryCells? GetDiscoveryIndicesSnapshot(string? playerUid, ChunkKey chunk)
	{
		if (string.IsNullOrWhiteSpace(playerUid))
		{
			return null;
		}
		lock (sync)
		{
			if (!playerDiscoveries.TryGetValue(playerUid, out Dictionary<ChunkKey, OreShieldDiscoveryCells> value) || !value.TryGetValue(chunk, out var value2) || value2.Count == 0)
			{
				return null;
			}
			return value2.Clone();
		}
	}

	private bool IsDiscovered(string? playerUid, WorldBlockKey key)
	{
		if (string.IsNullOrWhiteSpace(playerUid))
		{
			return false;
		}
		ChunkKey key2 = ChunkFor(key);
		int index = LocalIndex(key);
		lock (sync)
		{
			Dictionary<ChunkKey, OreShieldDiscoveryCells> value;
			OreShieldDiscoveryCells value2;
			return playerDiscoveries.TryGetValue(playerUid, out value) && value.TryGetValue(key2, out value2) && value2.Contains(index);
		}
	}

	private bool HasAnyDiscoveryInChunk(string? playerUid, ChunkKey chunk)
	{
		if (string.IsNullOrWhiteSpace(playerUid))
		{
			return false;
		}
		lock (sync)
		{
			Dictionary<ChunkKey, OreShieldDiscoveryCells> value;
			OreShieldDiscoveryCells value2;
			return playerDiscoveries.TryGetValue(playerUid, out value) && value.TryGetValue(chunk, out value2) && value2.Count > 0;
		}
	}

	private void TrackDiscoveryOwnerNoLock(string uid, ChunkKey chunk)
	{
		if (!discoveryOwners.TryGetValue(chunk, out HashSet<string> value))
		{
			value = (discoveryOwners[chunk] = new HashSet<string>(StringComparer.Ordinal));
		}
		if (value.Add(uid))
		{
			discoveryChunkRecords++;
		}
	}

	private void UntrackDiscoveryOwnerNoLock(string uid, ChunkKey chunk)
	{
		if (discoveryOwners.TryGetValue(chunk, out HashSet<string> value) && value.Remove(uid))
		{
			discoveryChunkRecords--;
			if (value.Count == 0)
			{
				discoveryOwners.Remove(chunk);
			}
		}
	}

	private bool MarkDiscovered(IServerPlayer player, BlockPos pos)
	{
		string text = player?.PlayerUID ?? string.Empty;
		if (text.Length == 0)
		{
			return false;
		}
		WorldBlockKey key = new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension);
		ChunkKey chunkKey = ChunkFor(key);
		int index = LocalIndex(key);
		lock (sync)
		{
			if (!playerDiscoveries.TryGetValue(text, out Dictionary<ChunkKey, OreShieldDiscoveryCells> value))
			{
				value = (playerDiscoveries[text] = new Dictionary<ChunkKey, OreShieldDiscoveryCells>());
			}
			if (!value.TryGetValue(chunkKey, out var value2))
			{
				value2 = (value[chunkKey] = new OreShieldDiscoveryCells());
				TrackDiscoveryOwnerNoLock(text, chunkKey);
			}
			if (!value2.Add(index))
			{
				return false;
			}
			discoveryCells++;
			losDiscoveries++;
			return true;
		}
	}

	private bool MarkDiscoveredWithoutLosCounter(string uid, WorldBlockKey key)
	{
		if (string.IsNullOrWhiteSpace(uid))
		{
			return false;
		}
		ChunkKey chunkKey = ChunkFor(key);
		int index = LocalIndex(key);
		lock (sync)
		{
			if (!playerDiscoveries.TryGetValue(uid, out Dictionary<ChunkKey, OreShieldDiscoveryCells> value))
			{
				value = (playerDiscoveries[uid] = new Dictionary<ChunkKey, OreShieldDiscoveryCells>());
			}
			if (!value.TryGetValue(chunkKey, out var value2))
			{
				value2 = (value[chunkKey] = new OreShieldDiscoveryCells());
				TrackDiscoveryOwnerNoLock(uid, chunkKey);
			}
			bool flag = value2.Add(index);
			if (flag)
			{
				discoveryCells++;
			}
			return flag;
		}
	}

	private void QueueWorldChange(BlockPos pos, bool reassert)
	{
		WorldBlockKey key = new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension);
		lock (sync)
		{
			geometryChanged = true;
			if (pendingWorldChanges.TryGetValue(key, out var value))
			{
				pendingWorldChanges[key] = value | reassert;
				worldChangeCoalesces++;
			}
			else if (pendingWorldChanges.Count >= 4096)
			{
				worldChangeOverflows++;
				exposureRefreshRequested = true;
			}
			else
			{
				pendingWorldChanges[key] = reassert;
			}
		}
	}

	private void ProcessWorldChanges()
	{
		HashSet<WorldBlockKey> hashSet = new HashSet<WorldBlockKey>();
		bool flag = false;
		int num = 0;
		while (num < 16 && !VisibilityTimeExpired)
		{
			KeyValuePair<WorldBlockKey, bool> keyValuePair;
			lock (sync)
			{
				if (pendingWorldChanges.Count == 0)
				{
					break;
				}
				keyValuePair = pendingWorldChanges.First();
				pendingWorldChanges.Remove(keyValuePair.Key);
				goto IL_006f;
			}
			IL_006f:
			BlockPos changedPos = new BlockPos(keyValuePair.Key.X, keyValuePair.Key.Y, keyValuePair.Key.Z, keyValuePair.Key.Dimension);
			RefreshExposureIndexAround(changedPos);
			foreach (WorldBlockKey item in CollectAffectedProtectedOre(changedPos))
			{
				hashSet.Add(item);
			}
			flag |= keyValuePair.Value;
			num++;
		}
		if (hashSet.Count > 0)
		{
			SyncExposureToAllClients(hashSet, flag);
		}
		lock (sync)
		{
			if (!geometryChanged)
			{
				return;
			}
			foreach (PlayerVisibilityState value in playerVisibilityStates.Values)
			{
				value.LastScanMs = 0L;
			}
			geometryChanged = false;
		}
	}

	private void OnVisibilityTick(float dt)
	{
		if (disposed || !config.OreShieldEnabled)
		{
			return;
		}
		visibilityStarted = Stopwatch.GetTimestamp();
		try
		{
			RunVisibilityPass();
		}
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
		lock (sync)
		{
			losPasses++;
		}
		ProcessVeins();
		FlushPlacedOreCheckpoints(2);
		ProcessWorldChanges();
		ProcessExposureBuilds();
		ProcessDiscoveryResyncs();
		if (!VisibilityTimeExpired)
		{
			ProcessBoundaryRefreshes();
		}
		IPlayer[] allOnlinePlayers = api.World.AllOnlinePlayers;
		if (allOnlinePlayers.Length == 0)
		{
			nextVisibilityPlayer = 0;
			return;
		}
		int num = Math.Clamp(config.OreShieldMaxLosChecksPerPass, 16, 512);
		OreShieldWorkBudget oreShieldWorkBudget = new OreShieldWorkBudget(Math.Min(num * 4, 2048));
		OreShieldWorkBudget oreShieldWorkBudget2 = new OreShieldWorkBudget(2);
		int maxChecks = Math.Min(num, Math.Max(8, oreShieldWorkBudget.Remaining / Math.Min(allOnlinePlayers.Length, 8)));
		int num2 = 0;
		int num3 = 0;
		int num4 = nextVisibilityPlayer % allOnlinePlayers.Length;
		while (num3 < allOnlinePlayers.Length && num2 < 8 && oreShieldWorkBudget.Remaining > 0 && !VisibilityTimeExpired)
		{
			int num5 = (num4 + num3++) % allOnlinePlayers.Length;
			nextVisibilityPlayer = (num5 + 1) % allOnlinePlayers.Length;
			if (allOnlinePlayers[num5] is IServerPlayer { Entity: not null } serverPlayer && (serverPlayer.ConnectionState == EnumClientState.Admitted || serverPlayer.ConnectionState == EnumClientState.Connected))
			{
				EntityPos entityPos = EntityRuntimeCompat.GetServerPos(serverPlayer.Entity) ?? EntityRuntimeCompat.GetPos(serverPlayer.Entity);
				if (entityPos != null && ShouldRunVisibilityScan(serverPlayer, entityPos))
				{
					num2++;
					DiscoverVisibleOreForPlayer(serverPlayer, entityPos, maxChecks, oreShieldWorkBudget, oreShieldWorkBudget2);
				}
			}
		}
		lock (sync)
		{
			visibilityIndexBuilds += oreShieldWorkBudget2.Used;
			if (num3 < allOnlinePlayers.Length)
			{
				visibilityBudgetDeferrals++;
			}
		}
	}

	private bool ShouldRunVisibilityScan(IServerPlayer player, EntityPos entityPos)
	{
		string text = player.PlayerUID ?? string.Empty;
		if (text.Length == 0)
		{
			return false;
		}
		long tickCount = Environment.TickCount64;
		float num = 0.08726646f;
		int num2 = Math.Clamp(config.OreShieldIdleRescanMs, 500, 15000);
		lock (sync)
		{
			if (!playerVisibilityStates.TryGetValue(text, out PlayerVisibilityState value))
			{
				value = (playerVisibilityStates[text] = new PlayerVisibilityState());
			}
			double num3 = entityPos.X - value.X;
			double num4 = entityPos.InternalY - value.InternalY;
			double num5 = entityPos.Z - value.Z;
			bool flag = !value.Initialized || value.Dimension != entityPos.Dimension || num3 * num3 + num4 * num4 + num5 * num5 >= 0.0625;
			bool flag2 = !value.Initialized || AngleDelta(entityPos.Yaw, value.Yaw) >= num || AngleDelta(entityPos.Pitch, value.Pitch) >= num;
			bool flag3 = !value.Initialized || tickCount - value.LastScanMs >= num2;
			if (value.PendingCandidates.Count == 0 && !value.Cursor.HasPendingSweep && !flag && !flag2 && !flag3)
			{
				losMovementSkips++;
				return false;
			}
			value.X = entityPos.X;
			value.InternalY = entityPos.InternalY;
			value.Z = entityPos.Z;
			value.Yaw = entityPos.Yaw;
			value.Pitch = entityPos.Pitch;
			value.Dimension = entityPos.Dimension;
			value.LastScanMs = tickCount;
			value.Initialized = true;
			return true;
		}
	}

	private static float AngleDelta(float a, float b)
	{
		float num = (float)Math.PI * 2f;
		float num2 = (float)Math.PI;
		float num3 = Math.Abs(a - b) % num;
		return (num3 > num2) ? (num - num3) : num3;
	}

	private void DiscoverVisibleOreForPlayer(IServerPlayer player, EntityPos entityPos, int maxChecks, OreShieldWorkBudget rayBudget, OreShieldWorkBudget indexBudget)
	{
		string text = player.PlayerUID ?? string.Empty;
		if (text.Length == 0)
		{
			return;
		}
		PlayerVisibilityState value;
		lock (sync)
		{
			losPlayersChecked++;
			if (!playerVisibilityStates.TryGetValue(text, out value))
			{
				return;
			}
		}
		Vec3d localEyePos = player.Entity.LocalEyePos;
		Vec3d vec3d = new Vec3d(entityPos.X + localEyePos.X, entityPos.InternalY + localEyePos.Y, entityPos.Z + localEyePos.Z);
		int num = Math.Clamp(config.OreShieldDiscoveryRangeBlocks, 16, 192);
		maxChecks = Math.Min(maxChecks, rayBudget.Remaining);
		double num2 = (double)num * (double)num;
		int scanRadius = (num + chunkSize - 1) / chunkSize;
		int num3 = (int)Math.Floor(entityPos.X / (double)chunkSize);
		int num4 = (int)Math.Floor(entityPos.Z / (double)chunkSize);
		int num5 = (int)Math.Floor(entityPos.InternalY) >> 5;
		int num6 = entityPos.Dimension * 1024;
		OreShieldScanCursor cursor = value.Cursor;
		cursor.Configure(num3, num5, num4, scanRadius, entityPos.Dimension);
		cursor.BeginSweep();
		List<(double, WorldBlockKey)> list = new List<(double, WorldBlockKey)>(maxChecks);
		Span<int> span = stackalloc int[128];
		Span<bool> span2 = stackalloc bool[128];
		int num7 = 0;
		int num8 = 0;
		while (value.PendingCandidates.Count == 0 && !VisibilityTimeExpired && cursor.HasPendingSweep && num7 < 64 && num8 < 1024 && list.Count < maxChecks)
		{
			OreShieldScanCursor.Offset current = cursor.Current;
			int x = num3 + current.X;
			int num9 = num5 + current.Y;
			int z = num4 + current.Z;
			num7++;
			if (num9 < num6 || num9 >= num6 + server.WorldMap.ChunkMapSizeY)
			{
				cursor.AdvanceChunk();
				continue;
			}
			ChunkKey chunkKey = new ChunkKey(x, num9, z);
			if (!EnsureOreIndexForLoadedChunk(chunkKey, indexBudget))
			{
				break;
			}
			int length = Math.Min(span.Length, 1024 - num8);
			int num10;
			int nextIndex;
			lock (sync)
			{
				if (!oreIndexCache.TryGetValue(chunkKey, out OreIndexEntry value2))
				{
					cursor.AdvanceChunk();
					continue;
				}
				TouchOreIndexNoLock(chunkKey);
				num10 = value2.ExposedIndices.CopyWindow(cursor.LocalIndex, span.Slice(0, length), out nextIndex);
				OreShieldDiscoveryCells value3 = null;
				if (playerDiscoveries.TryGetValue(text, out Dictionary<ChunkKey, OreShieldDiscoveryCells> value4))
				{
					value4.TryGetValue(chunkKey, out value3);
				}
				for (int i = 0; i < num10; i++)
				{
					span2[i] = value3?.Contains(span[i]) ?? false;
				}
			}
			int j;
			for (j = 0; j < num10; j++)
			{
				if (list.Count >= maxChecks)
				{
					break;
				}
				int num11 = span[j];
				cursor.LocalIndex = num11 + 1;
				num8++;
				if (!span2[j])
				{
					BlockPos blockPos = BlockPosFromChunkLocalIndex(chunkKey, num11);
					double num12 = (double)blockPos.X + 0.5 - vec3d.X;
					double num13 = (double)blockPos.InternalY + 0.5 - vec3d.Y;
					double num14 = (double)blockPos.Z + 0.5 - vec3d.Z;
					double num15 = num12 * num12 + num13 * num13 + num14 * num14;
					if (!(num15 > num2) && IsWithinViewCone(entityPos, vec3d, blockPos))
					{
						list.Add((num15, new WorldBlockKey(blockPos.X, blockPos.Y, blockPos.Z, blockPos.dimension)));
					}
				}
			}
			if (j == num10)
			{
				cursor.LocalIndex = nextIndex;
				if (nextIndex >= chunkVolume)
				{
					cursor.AdvanceChunk();
				}
			}
		}
		list.Sort(((double D2, WorldBlockKey Key) a, (double D2, WorldBlockKey Key) b) => a.D2.CompareTo(b.D2));
		foreach (var item in list)
		{
			value.PendingCandidates.Enqueue(item.Item2);
		}
		List<WorldBlockKey> list2 = new List<WorldBlockKey>();
		int num16 = 0;
		while (value.PendingCandidates.Count > 0 && num16 < maxChecks && !VisibilityTimeExpired)
		{
			WorldBlockKey worldBlockKey = value.PendingCandidates.Dequeue();
			if (IsDiscovered(text, worldBlockKey) || worldBlockKey.Dimension != entityPos.Dimension)
			{
				continue;
			}
			BlockPos blockPos2 = new BlockPos(worldBlockKey.X, worldBlockKey.Y, worldBlockKey.Z, worldBlockKey.Dimension);
			double num17 = (double)blockPos2.X + 0.5 - vec3d.X;
			double num18 = (double)blockPos2.InternalY + 0.5 - vec3d.Y;
			double num19 = (double)blockPos2.Z + 0.5 - vec3d.Z;
			if (num17 * num17 + num18 * num18 + num19 * num19 > num2 || !IsWithinViewCone(entityPos, vec3d, blockPos2))
			{
				continue;
			}
			int blockId = ReadWorldBlock(blockPos2);
			if (IsMaskableProtectedOre(blockId, blockPos2) && IsWorldExposed(blockPos2))
			{
				if (!rayBudget.TryTake())
				{
					value.PendingCandidates.Enqueue(worldBlockKey);
					break;
				}
				num16++;
				if (HasServerLineOfSight(player, vec3d, blockPos2) && MarkDiscovered(player, blockPos2))
				{
					list2.Add(worldBlockKey);
				}
			}
		}
		lock (sync)
		{
			visibilityChunksVisited += num7;
			visibilityCandidatesEvaluated += num8;
			losCandidatesConsidered += num16;
			losRayChecks += num16;
			if (cursor.HasPendingSweep || value.PendingCandidates.Count > 0)
			{
				visibilityBudgetDeferrals++;
			}
		}
		if (list2.Count > 0)
		{
			SendPlayerViewCorrections(player, list2);
		}
	}

	private bool IsWithinViewCone(EntityPos entityPos, Vec3d eye, BlockPos target)
	{
		int num = Math.Clamp(config.OreShieldViewConeDegrees, 90, 180);
		double num2 = (double)target.X + 0.5 - eye.X;
		double num3 = (double)target.InternalY + 0.5 - eye.Y;
		double num4 = (double)target.Z + 0.5 - eye.Z;
		double num5 = num2 * num2 + num3 * num3 + num4 * num4;
		if (num5 <= 0.0001)
		{
			return true;
		}
		double inv = 1.0 / Math.Sqrt(num5);
		double num6 = Math.Cos((double)num * 0.5 * Math.PI / 180.0);
		Vec3f viewVector = EntityPos.GetViewVector(entityPos.Pitch, entityPos.Yaw);
		if (Dot(viewVector, num2, num3, num4, inv) >= num6)
		{
			return true;
		}
		Vec3f viewVector2 = EntityPos.GetViewVector(entityPos.HeadPitch, entityPos.HeadYaw);
		if (Dot(viewVector2, num2, num3, num4, inv) >= num6)
		{
			return true;
		}
		Vec3f viewVector3 = EntityPos.GetViewVector(entityPos.Pitch + entityPos.HeadPitch, entityPos.Yaw + entityPos.HeadYaw);
		return Dot(viewVector3, num2, num3, num4, inv) >= num6;
		static double Dot(Vec3f view, double x, double y, double z, double num7)
		{
			return ((double)view.X * x + (double)view.Y * y + (double)view.Z * z) * num7;
		}
	}

	private bool EnsureOreIndexForLoadedChunk(ChunkKey key, OreShieldWorkBudget budget)
	{
		lock (sync)
		{
			if (oreIndexCache.ContainsKey(key))
			{
				return true;
			}
		}
		ServerChunk loadedChunk = server.GetLoadedChunk(ChunkIndex3D(key));
		if (loadedChunk == null || loadedChunk.Disposed)
		{
			return true;
		}
		if (!budget.TryTake())
		{
			return false;
		}
		long num;
		lock (sync)
		{
			num = RevisionNoLock(key);
		}
		try
		{
			loadedChunk.Unpack();
		}
		catch
		{
			return true;
		}
		IChunkBlocks data = loadedChunk.Data;
		if (data == null)
		{
			return true;
		}
		HashSet<int> hashSet = new HashSet<int>();
		for (int i = 0; i < chunkVolume; i++)
		{
			int blockIdUnsafe = data.GetBlockIdUnsafe(i);
			if (IsProtectedOreId(blockIdUnsafe))
			{
				int num2 = i / (chunkSize * chunkSize);
				int num3 = i - num2 * chunkSize * chunkSize;
				int num4 = num3 / chunkSize;
				int num5 = num3 - num4 * chunkSize;
				BlockPos position = BlockPosFromChunkLocalIndex(key, i);
				if (!IsPlayerPlacedOre(position))
				{
					hashSet.Add(i);
				}
			}
		}
		OreShieldExposedIndex exposedIndices = new OreShieldExposedIndex();
		lock (sync)
		{
			if (!oreIndexCache.ContainsKey(key) && RevisionNoLock(key) == num)
			{
				SetOreIndexNoLock(key, 0uL, hashSet, exposedIndices);
			}
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
				foreach (KeyValuePair<ChunkKey, OreIndexEntry> item in oreIndexCache)
				{
					if (item.Value.PendingExposure != null)
					{
						pendingExposureBuilds.Enqueue(item.Key);
					}
				}
			}
			if (exposureRefreshRequested && pendingExposureRefreshChunks == null)
			{
				pendingExposureRefreshChunks = new Queue<ChunkKey>(oreIndexCache.Keys);
				exposureRefreshRequested = false;
			}
			Queue<ChunkKey>? queue = pendingExposureRefreshChunks;
			if (queue != null && queue.Count > 0)
			{
				ChunkKey chunkKey = pendingExposureRefreshChunks.Dequeue();
				if (oreIndexCache.TryGetValue(chunkKey, out OreIndexEntry value))
				{
					value.PendingExposure = value.Indices.ToArray();
					value.ExposureCursor = 0;
					pendingExposureBuilds.Enqueue(chunkKey);
				}
			}
			Queue<ChunkKey>? queue2 = pendingExposureRefreshChunks;
			if (queue2 != null && queue2.Count == 0)
			{
				pendingExposureRefreshChunks = null;
			}
		}
		int num = 0;
		while (num < 256 && Stopwatch.GetElapsedTime(visibilityStarted).TotalMilliseconds < 2.0)
		{
			ChunkKey chunkKey2;
			OreIndexEntry value2;
			int index;
			lock (sync)
			{
				if (pendingExposureBuilds.Count == 0)
				{
					break;
				}
				chunkKey2 = pendingExposureBuilds.Peek();
				if (!oreIndexCache.TryGetValue(chunkKey2, out value2) || value2.PendingExposure == null)
				{
					pendingExposureBuilds.Dequeue();
					continue;
				}
				if (value2.ExposureCursor >= value2.PendingExposure.Length)
				{
					value2.PendingExposure = null;
					pendingExposureBuilds.Dequeue();
					continue;
				}
				index = value2.PendingExposure[value2.ExposureCursor++];
			}
			BlockPos position = BlockPosFromChunkLocalIndex(chunkKey2, index);
			bool flag = IsMaskableProtectedOre(ReadWorldBlock(position), position) && IsWorldExposed(position);
			lock (sync)
			{
				if (oreIndexCache.TryGetValue(chunkKey2, out OreIndexEntry value3) && value3 == value2)
				{
					if (flag)
					{
						value3.ExposedIndices.Add(index);
					}
					else
					{
						value3.ExposedIndices.Remove(index);
					}
					exposureBuildCells++;
					if (flag)
					{
						geometryChanged = true;
					}
					if (value3.ExposureCursor % 64 == 0 && pendingExposureBuilds.Count > 1)
					{
						pendingExposureBuilds.Dequeue();
						pendingExposureBuilds.Enqueue(chunkKey2);
					}
				}
			}
			num++;
		}
	}

	private void RefreshExposureIndexAround(BlockPos changedPos)
	{
		if (changedPos == null)
		{
			return;
		}
		List<BlockPos> list = new List<BlockPos>(7) { changedPos.Copy() };
		BlockFacing[] aLLFACES = BlockFacing.ALLFACES;
		foreach (BlockFacing blockFacing in aLLFACES)
		{
			BlockPos blockPos = changedPos.Copy();
			blockFacing.IterateThruFacingOffsets(blockPos);
			list.Add(blockPos);
		}
		foreach (BlockPos item in list)
		{
			ChunkKey key = new ChunkKey(item.X >> 5, item.InternalY >> 5, item.Z >> 5);
			int num = Index(item.X & chunkMask, item.InternalY & chunkMask, item.Z & chunkMask);
			int blockId = ReadWorldBlock(item);
			bool flag = IsMaskableProtectedOre(blockId, item);
			bool flag2 = flag && IsWorldExposed(item);
			lock (sync)
			{
				if (oreIndexCache.TryGetValue(key, out OreIndexEntry value))
				{
					if (flag)
					{
						value.Indices.Add(num);
					}
					else
					{
						value.Indices.Remove(num);
					}
					if (flag2)
					{
						value.ExposedIndices.Add(num);
					}
					else
					{
						value.ExposedIndices.Remove(num);
					}
				}
			}
		}
	}

	private bool HasServerLineOfSight(IServerPlayer player, Vec3d eye, BlockPos target)
	{
		EntityPos pos = EntityRuntimeCompat.GetPos(player.Entity);
		if (pos == null || pos.Dimension != target.dimension)
		{
			return false;
		}
		if (RayHitsTarget(new Vec3d((double)target.X + 0.5, (double)target.InternalY + 0.5, (double)target.Z + 0.5)))
		{
			return true;
		}
		BlockPos blockPos = target.Copy();
		BlockFacing[] aLLFACES = BlockFacing.ALLFACES;
		foreach (BlockFacing blockFacing in aLLFACES)
		{
			blockPos.Set(target.X, target.Y, target.Z).SetDimension(target.dimension);
			blockFacing.IterateThruFacingOffsets(blockPos);
			int num = ReadWorldBlock(blockPos);
			if (num >= 0 && !palette.IsOpaque(num))
			{
				Vec3f normalf = blockFacing.Normalf;
				Vec3d to = new Vec3d((double)target.X + 0.5 + (double)normalf.X * 0.499, (double)target.InternalY + 0.5 + (double)normalf.Y * 0.499, (double)target.Z + 0.5 + (double)normalf.Z * 0.499);
				if (RayHitsTarget(to))
				{
					return true;
				}
			}
		}
		return false;
		bool RayHitsTarget(Vec3d toPos)
		{
			BlockSelection blockSelection = null;
			EntitySelection entitySelection = null;
			api.World.RayTraceForSelection(eye, toPos, ref blockSelection, ref entitySelection, null, (Entity _) => false);
			BlockPos blockPos2 = blockSelection?.Position;
			return blockPos2 != null && blockPos2.X == target.X && blockPos2.Y == target.Y && blockPos2.Z == target.Z && blockPos2.dimension == target.dimension;
		}
	}

	private int ClientViewForPlayer(IServerPlayer player, int actualId, BlockPos position)
	{
		if (!IsProtectedOreId(actualId))
		{
			return actualId;
		}
		if (IsPlayerPlacedOre(position))
		{
			return actualId;
		}
		if (!palette.TryGetHost(actualId, out var hostId))
		{
			return actualId;
		}
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return hostId;
		}
		return IsDiscovered(key: new WorldBlockKey(position.X, position.Y, position.Z, position.dimension), playerUid: player.PlayerUID) ? actualId : hostId;
	}

	private bool IsWorldExposed(BlockPos position)
	{
		BlockPos blockPos = position.Copy();
		BlockFacing[] aLLFACES = BlockFacing.ALLFACES;
		foreach (BlockFacing blockFacing in aLLFACES)
		{
			blockPos.Set(position);
			blockFacing.IterateThruFacingOffsets(blockPos);
			int num = ReadWorldBlock(blockPos);
			if (num >= 0 && !palette.IsOpaque(num))
			{
				return true;
			}
		}
		return false;
	}

	private void SendPlayerViewCorrections(IServerPlayer player, IEnumerable<WorldBlockKey> keys, bool reassertSmallSet = false)
	{
		if (disposed || player == null || keys == null)
		{
			return;
		}
		WorldBlockKey[] array = keys.Distinct().ToArray();
		if (array.Length == 0)
		{
			return;
		}
		string playerUid = player.PlayerUID ?? string.Empty;
		if (playerUid.Length == 0)
		{
			return;
		}
		int num = 0;
		int num2 = 0;
		while (num < array.Length)
		{
			int num3 = Math.Min(64, array.Length - num);
			WorldBlockKey[] batch = new WorldBlockKey[num3];
			Array.Copy(array, num, batch, 0, num3);
			int num4 = num2 * 40;
			if (num4 == 0)
			{
				SendPlayerViewCorrectionsBatch(playerUid, batch);
			}
			else
			{
				api.Event.RegisterCallback((float _) =>
				{
					SendPlayerViewCorrectionsBatch(playerUid, batch);
				}, num4);
			}
			num += num3;
			num2++;
		}
	}

	private void SendPlayerViewCorrectionsBatch(string playerUid, WorldBlockKey[] keys)
	{
		if (disposed || string.IsNullOrWhiteSpace(playerUid) || keys == null || keys.Length == 0)
		{
			return;
		}
		lock (sync)
		{
			revealDispatchAttempts += keys.Length;
		}
		IServerPlayer serverPlayer = FindOnlinePlayerByUid(playerUid);
		if (serverPlayer == null)
		{
			lock (sync)
			{
				revealRecipientResolveFailures += keys.Length;
				return;
			}
		}
		List<BlockPos> list = new List<BlockPos>(keys.Length);
		long num = 0L;
		long num2 = 0L;
		long num3 = 0L;
		for (int i = 0; i < keys.Length; i++)
		{
			WorldBlockKey worldBlockKey = keys[i];
			BlockPos blockPos = new BlockPos(worldBlockKey.X, worldBlockKey.Y, worldBlockKey.Z, worldBlockKey.Dimension);
			int num4 = ReadWorldBlock(blockPos);
			if (!IsProtectedOreId(num4))
			{
				num3++;
				continue;
			}
			int num5 = ClientViewForPlayer(serverPlayer, num4, blockPos);
			list.Add(blockPos);
			if (num5 == num4)
			{
				num++;
			}
			else
			{
				num2++;
			}
		}
		if (num3 != 0)
		{
			lock (sync)
			{
				revealStaleKeySkips += num3;
			}
		}
		if (list.Count == 0 || !TrySendClientSync(serverPlayer, list))
		{
			return;
		}
		lock (sync)
		{
			revealCorrectionPackets += list.Count;
			revealRealBlockPackets += num;
			reconcealBlockPackets += num2;
		}
	}

	internal bool TryGetOnlineServerPlayerForClient(ConnectedClient client, out IServerPlayer player)
	{
		player = null;
		if (client == null || (client.State != EnumClientState.Admitted && client.State != EnumClientState.Connected))
		{
			return false;
		}
		string text = client.Entityplayer?.PlayerUID ?? string.Empty;
		if (text.Length > 0)
		{
			IServerPlayer serverPlayer = FindOnlinePlayerByUid(text);
			if (serverPlayer != null)
			{
				player = serverPlayer;
				return true;
			}
		}
		IPlayer[] allOnlinePlayers = api.World.AllOnlinePlayers;
		foreach (IPlayer player2 in allOnlinePlayers)
		{
			if (player2 is IServerPlayer serverPlayer2 && serverPlayer2.ClientId == client.Id)
			{
				player = serverPlayer2;
				return true;
			}
		}
		return false;
	}

	private IEnumerable<IServerPlayer> OnlineServerPlayers()
	{
		IPlayer[] allOnlinePlayers = api.World.AllOnlinePlayers;
		foreach (IPlayer raw in allOnlinePlayers)
		{
			if (raw is IServerPlayer player && (player.ConnectionState == EnumClientState.Admitted || player.ConnectionState == EnumClientState.Connected))
			{
				yield return player;
			}
		}
	}

	private IServerPlayer? FindOnlinePlayerByUid(string playerUid)
	{
		if (string.IsNullOrWhiteSpace(playerUid))
		{
			return null;
		}
		IPlayer[] allOnlinePlayers = api.World.AllOnlinePlayers;
		foreach (IPlayer player in allOnlinePlayers)
		{
			if (player is IServerPlayer serverPlayer && string.Equals(serverPlayer.PlayerUID, playerUid, StringComparison.OrdinalIgnoreCase))
			{
				return serverPlayer;
			}
		}
		if (api.World.PlayerByUid(playerUid) is IServerPlayer serverPlayer2 && (serverPlayer2.ConnectionState == EnumClientState.Admitted || serverPlayer2.ConnectionState == EnumClientState.Connected))
		{
			return serverPlayer2;
		}
		return null;
	}

	private bool TrySendClientSync(IServerPlayer player, List<BlockPos> positions, int retryCount = 0, bool stabilize = true)
	{
		if (disposed || oreShieldSyncChannel == null || player == null || positions.Count == 0)
		{
			return false;
		}
		if (!supportsClientSync(player))
		{
			lock (sync)
			{
				revealClientSyncUnsupportedSkips += positions.Count;
			}
			return false;
		}
		string text = player.PlayerUID ?? string.Empty;
		if (text.Length == 0)
		{
			return false;
		}
		bool flag = true;
		lock (sync)
		{
			foreach (BlockPos position in positions)
			{
				ChunkKey chunk = new ChunkKey(position.X >> 5, position.InternalY >> 5, position.Z >> 5);
				if (!CanReceiveClientSync(player, chunk, stabilize))
				{
					clientSyncChunkNotSentSkips++;
					continue;
				}
				ClientSyncOutboxKey clientSyncOutboxKey = new ClientSyncOutboxKey(text, chunk, stabilize);
				int key = Index(position.X & chunkMask, position.InternalY & chunkMask, position.Z & chunkMask);
				ClientSyncOutboxKey key2 = new ClientSyncOutboxKey(text, chunk, !stabilize);
				if (retryCount > 0 && this.clientSyncOutbox.TryGetValue(key2, out ClientSyncOutbox value) && value.Positions.ContainsKey(key))
				{
					continue;
				}
				if (!this.clientSyncOutbox.ContainsKey(clientSyncOutboxKey) && this.clientSyncOutbox.Count >= 1024)
				{
					clientSyncQueueOverflows++;
					flag = false;
					continue;
				}
				if (this.clientSyncOutbox.TryGetValue(key2, out ClientSyncOutbox value2) && value2.Positions.Remove(key))
				{
					clientSyncQueuedCells--;
					clientSyncQueueCoalesces++;
				}
				if (!this.clientSyncOutbox.TryGetValue(clientSyncOutboxKey, out ClientSyncOutbox value3))
				{
					if (this.clientSyncOutbox.Count >= 1024)
					{
						clientSyncQueueOverflows++;
						flag = false;
						continue;
					}
					value3 = (this.clientSyncOutbox[clientSyncOutboxKey] = new ClientSyncOutbox
					{
						RetryCount = retryCount
					});
					(stabilize ? clientSyncRevealQueue : clientSyncWorldQueue).Enqueue(clientSyncOutboxKey);
				}
				if (value3.Positions.ContainsKey(key))
				{
					clientSyncQueueCoalesces++;
				}
				else
				{
					if (clientSyncQueuedCells >= 65536)
					{
						clientSyncQueueOverflows++;
						flag = false;
						continue;
					}
					clientSyncQueuedCells++;
				}
				value3.RetryCount = Math.Min(value3.RetryCount, retryCount);
				value3.Positions[key] = position.Copy();
			}
		}
		if (!flag & stabilize)
		{
			ResyncKnownDiscoveries(player);
		}
		return flag;
	}

	private bool CanReceiveClientSync(IServerPlayer player, ChunkKey chunk, bool stabilize)
	{
		if (server.Clients != null && server.Clients.TryGetValue(player.ClientId, out var value) && value.ChunkSent != null && value.ChunkSent.Contains(ChunkIndex3D(chunk)))
		{
			return true;
		}
		lock (sync)
		{
			Dictionary<ChunkKey, OreShieldDiscoveryCells> value2;
			return clientSyncPendingByChunk.ContainsKey(new ClientSyncRecipientChunk(player.PlayerUID, chunk)) || (stabilize && playerDiscoveries.TryGetValue(player.PlayerUID, out value2) && value2.ContainsKey(chunk));
		}
	}

	private void PumpClientSync()
	{
		if (disposed || !config.OreShieldEnabled)
		{
			return;
		}
		long tickCount = Environment.TickCount64;
		lock (sync)
		{
			long num = Math.Max(0L, tickCount - clientSyncLastPumpMs);
			clientSyncClockMs += Math.Min(num, 500L);
			if (num > 2000)
			{
				clientSyncStallDeferrals++;
			}
			clientSyncLastPumpMs = tickCount;
		}
		long timestamp = Stopwatch.GetTimestamp();
		ClientSyncPendingKey element;
		for (int i = 0; i < 16 && Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds < 1.0; HandleClientSyncTimeout(element.PlayerUid, element.Sequence), i++)
		{
			lock (sync)
			{
				if (!clientSyncDeadlines.TryPeek(out element, out var priority) || priority > clientSyncClockMs)
				{
					break;
				}
				clientSyncDeadlines.Dequeue();
				continue;
			}
		}
		int num2;
		lock (sync)
		{
			num2 = clientSyncWorldQueue.Count + clientSyncRevealQueue.Count;
		}
		int num3 = 0;
		for (int j = 0; num3 < 16 && j < num2 && Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds < 3.0; j++)
		{
			ClientSyncOutboxKey clientSyncOutboxKey;
			ClientSyncOutbox value;
			List<BlockPos> list;
			lock (sync)
			{
				Queue<ClientSyncOutboxKey> queue = ((clientSyncWorldQueue.Count > 0 && (j % 4 != 3 || clientSyncRevealQueue.Count == 0)) ? clientSyncWorldQueue : clientSyncRevealQueue);
				if (queue.Count == 0)
				{
					break;
				}
				clientSyncOutboxKey = queue.Dequeue();
				if (!clientSyncOutbox.TryGetValue(clientSyncOutboxKey, out value))
				{
					continue;
				}
				if (value.Positions.Count == 0)
				{
					clientSyncOutbox.Remove(clientSyncOutboxKey);
					continue;
				}
				if (pendingClientSync.Count >= 1024 || clientSyncPendingByPlayer.GetValueOrDefault(clientSyncOutboxKey.PlayerUid) >= 32)
				{
					queue.Enqueue(clientSyncOutboxKey);
					continue;
				}
				list = value.Positions.Values.Take(128).ToList();
				foreach (BlockPos item in list)
				{
					value.Positions.Remove(Index(item.X & chunkMask, item.InternalY & chunkMask, item.Z & chunkMask));
				}
				clientSyncQueuedCells -= list.Count;
				if (value.Positions.Count == 0)
				{
					clientSyncOutbox.Remove(clientSyncOutboxKey);
				}
				else
				{
					queue.Enqueue(clientSyncOutboxKey);
				}
				goto IL_0360;
			}
			IL_0360:
			IServerPlayer serverPlayer = FindOnlinePlayerByUid(clientSyncOutboxKey.PlayerUid);
			if (serverPlayer == null || !supportsClientSync(serverPlayer))
			{
				continue;
			}
			if (!CanReceiveClientSync(serverPlayer, clientSyncOutboxKey.Chunk, clientSyncOutboxKey.Stabilize))
			{
				lock (sync)
				{
					clientSyncChunkNotSentSkips += list.Count;
				}
				continue;
			}
			if (clientSyncOutboxKey.Stabilize)
			{
				list.RemoveAll((BlockPos pos) => !IsProtectedOreId(ReadWorldBlock(pos)));
			}
			if (list.Count > 0)
			{
				if (!SendClientSyncNow(serverPlayer, list, value.RetryCount, clientSyncOutboxKey.Stabilize) && value.RetryCount < 1)
				{
					TrySendClientSync(serverPlayer, list, 1, clientSyncOutboxKey.Stabilize);
				}
				num3++;
			}
		}
		lock (sync)
		{
			if (clientSyncTimeoutSummaryCount != 0L && tickCount - clientSyncLastTimeoutSummaryMs >= 30000)
			{
				api.Logger.Debug("[ServerEssentials/AntiCheat/OreShield] {0} sync batch timeout(s), {1} block(s) since the last summary. Recovery is queued and bounded; pending={2}, queued cells={3}. These are delivery timeouts, not cheating findings.", clientSyncTimeoutSummaryCount, clientSyncTimeoutSummaryEntries, pendingClientSync.Count, clientSyncQueuedCells);
				clientSyncTimeoutSummaryCount = (clientSyncTimeoutSummaryEntries = 0L);
				clientSyncLastTimeoutSummaryMs = tickCount;
			}
		}
	}

	private bool SendClientSyncNow(IServerPlayer player, List<BlockPos> positions, int retryCount = 0, bool stabilize = true)
	{
		if (oreShieldSyncChannel == null || player == null || positions.Count == 0)
		{
			return false;
		}
		if (!supportsClientSync(player))
		{
			lock (sync)
			{
				revealClientSyncUnsupportedSkips += positions.Count;
			}
			return false;
		}
		try
		{
			foreach (IGrouping<ChunkKey, BlockPos> item in from p in positions
				group p by new ChunkKey(p.X >> 5, p.InternalY >> 5, p.Z >> 5))
			{
				BlockPos[] array = item.ToArray();
				for (int num = 0; num < array.Length; num += 128)
				{
					BlockPos[] array2 = array.Skip(num).Take(128).ToArray();
					if (array2.Length == 0)
					{
						continue;
					}
					int[] array3 = new int[array2.Length];
					int[] array4 = new int[array2.Length];
					for (int num2 = 0; num2 < array2.Length; num2++)
					{
						BlockPos blockPos = array2[num2];
						array3[num2] = Index(blockPos.X & chunkMask, blockPos.InternalY & chunkMask, blockPos.Z & chunkMask);
						int id = server.BlockAccessor.GetBlock(blockPos, 1).Id;
						array4[num2] = ClientViewForPlayer(player, id, blockPos);
					}
					long sequence = Interlocked.Increment(ref clientSyncSequence);
					long revision;
					lock (sync)
					{
						revision = RevisionNoLock(item.Key);
					}
					OreShieldSyncBatchPacket message = new OreShieldSyncBatchPacket
					{
						Sequence = sequence,
						ChunkX = item.Key.X,
						ChunkY = item.Key.Y,
						ChunkZ = item.Key.Z,
						Revision = revision,
						LocalIndices = array3,
						BlockIds = array4,
						ApplyMode = ((!stabilize) ? 1 : 0)
					};
					PendingClientSync pendingClientSync = new PendingClientSync
					{
						PlayerUid = (player.PlayerUID ?? string.Empty),
						Sequence = sequence,
						Chunk = item.Key,
						Revision = revision,
						Positions = array2,
						RetryCount = retryCount,
						Stabilize = stabilize
					};
					ClientSyncPendingKey clientSyncPendingKey = new ClientSyncPendingKey(pendingClientSync.PlayerUid, sequence);
					lock (sync)
					{
						TrackPendingClientSyncNoLock(pendingClientSync);
						clientSyncDeadlines.Enqueue(clientSyncPendingKey, clientSyncClockMs + 9000);
					}
					try
					{
						oreShieldSyncChannel.SendPacket(message, player);
						lock (sync)
						{
							clientSyncBatchesSent++;
							clientSyncEntriesSent += array2.Length;
						}
					}
					catch
					{
						lock (sync)
						{
							RemovePendingClientSyncNoLock(clientSyncPendingKey);
						}
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
				long tickCount = Environment.TickCount64;
				if (tickCount - clientSyncLastSendWarningMs >= 30000)
				{
					clientSyncLastSendWarningMs = tickCount;
					api.Logger.Warning("[ServerEssentials/AntiCheat/OreShield] Sync transport failures: {0} total. Recovery is bounded. Latest: {1}: {2}", clientSyncSendFailures, ex.GetType().Name, ex.Message);
				}
			}
			return false;
		}
	}

	private void HandleClientSyncTimeout(string playerUid, long sequence)
	{
		PendingClientSync value;
		lock (sync)
		{
			ClientSyncPendingKey key = new ClientSyncPendingKey(playerUid, sequence);
			if (!pendingClientSync.TryGetValue(key, out value))
			{
				return;
			}
			RemovePendingClientSyncNoLock(key);
			clientSyncTimeouts++;
			clientSyncTimeoutSummaryCount++;
			clientSyncTimeoutSummaryEntries += value.Positions.Length;
		}
		if (value.RetryCount >= 1)
		{
			return;
		}
		IServerPlayer serverPlayer = FindOnlinePlayerByUid(playerUid);
		if (serverPlayer != null && supportsClientSync(serverPlayer))
		{
			lock (sync)
			{
				clientSyncRetries++;
			}
			TrySendClientSync(serverPlayer, value.Positions.ToList(), value.RetryCount + 1, value.Stabilize);
		}
	}

	public void HandleClientSyncAck(IServerPlayer player, OreShieldSyncAckPacket packet)
	{
		if (disposed || player == null || packet == null || packet.ProtocolVersion != 3)
		{
			return;
		}
		string text = player.PlayerUID ?? string.Empty;
		if (text.Length == 0)
		{
			return;
		}
		ClientSyncPendingKey key = new ClientSyncPendingKey(text, packet.Sequence);
		PendingClientSync pendingClientSync = null;
		lock (sync)
		{
			clientSyncAcks++;
			if (packet.Deferred > 0)
			{
				clientSyncDeferredAcks++;
			}
			if (!this.pendingClientSync.TryGetValue(key, out PendingClientSync value) || packet.ChunkX != value.Chunk.X || packet.ChunkY != value.Chunk.Y || packet.ChunkZ != value.Chunk.Z || packet.Revision != value.Revision || packet.Deferred != 0 || packet.Applied + packet.Failed + packet.Skipped < value.ExpectedEntries)
			{
				return;
			}
			RemovePendingClientSyncNoLock(key);
			clientSyncAppliedEntries += Math.Max(0, packet.Applied);
			clientSyncFailedEntries += Math.Max(0, packet.Failed);
			clientSyncSkippedEntries += Math.Max(0, packet.Skipped);
			if (packet.Failed > 0)
			{
				clientSyncApplyFailureBatches++;
				pendingClientSync = value;
			}
		}
		if (pendingClientSync == null)
		{
			return;
		}
		api.Logger.Debug("[ServerEssentials/AntiCheat/OreShield] Client reported {0} failed apply(s) in sync sequence {1} for {2} (chunk {3},{4},{5}).", packet.Failed, packet.Sequence, text, pendingClientSync.Chunk.X, pendingClientSync.Chunk.Y, pendingClientSync.Chunk.Z);
		long tickCount = Environment.TickCount64;
		long num = 0L;
		bool flag = false;
		lock (sync)
		{
			clientSyncFailureWarningsSuppressed += Math.Max(1, packet.Failed);
			if (tickCount - lastClientSyncFailureWarningMs >= 30000)
			{
				num = clientSyncFailureWarningsSuppressed;
				clientSyncFailureWarningsSuppressed = 0L;
				lastClientSyncFailureWarningMs = tickCount;
				flag = true;
			}
		}
		if (flag)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat/OreShield] {0} client apply failure(s) observed in the last interval. Recovery is rate-limited; see Ore Shield telemetry for totals.", num);
		}
		if (pendingClientSync.RetryCount >= 1 || !supportsClientSync(player))
		{
			return;
		}
		string key2 = text + "|" + pendingClientSync.Chunk.X + "|" + pendingClientSync.Chunk.Y + "|" + pendingClientSync.Chunk.Z;
		lock (sync)
		{
			if (clientSyncLastFailureResyncMs.TryGetValue(key2, out var value2) && tickCount - value2 < 5000)
			{
				return;
			}
			clientSyncLastFailureResyncMs[key2] = tickCount;
		}
		List<BlockPos> list = pendingClientSync.Positions.Select((BlockPos pos) => pos.Copy()).ToList();
		if (list.Count != 0)
		{
			lock (sync)
			{
				clientSyncFailureResyncs++;
				clientSyncRetries++;
			}
			TrySendClientSync(player, list, pendingClientSync.RetryCount + 1, pendingClientSync.Stabilize);
		}
	}

	public void HandleClientSyncRequest(IServerPlayer player, OreShieldChunkSyncRequestPacket packet)
	{
		if (disposed || player == null || packet == null || packet.ProtocolVersion != 3 || !supportsClientSync(player))
		{
			return;
		}
		ChunkKey chunkKey = new ChunkKey(packet.ChunkX, packet.ChunkY, packet.ChunkZ);
		string key = player.PlayerUID ?? string.Empty;
		long tickCount = Environment.TickCount64;
		lock (sync)
		{
			if (clientSyncLastResyncRequestMs.TryGetValue(key, out var value) && tickCount - value < 250)
			{
				return;
			}
			clientSyncLastResyncRequestMs[key] = tickCount;
			clientSyncResyncRequests++;
		}
		lock (sync)
		{
			if (playerDiscoveries.TryGetValue(key, out Dictionary<ChunkKey, OreShieldDiscoveryCells> value2) && value2.ContainsKey(chunkKey))
			{
				if (!pendingDiscoveryResyncs.TryGetValue(key, out ResyncJob value3))
				{
					pendingDiscoveryResyncs[key] = new ResyncJob
					{
						Chunks = new Queue<ChunkKey>(new ChunkKey[1] { chunkKey })
					};
				}
				else if ((value3.Cells == null || value3.Chunk != chunkKey) && value3.Chunks.Count < 128 && !value3.Chunks.Contains(chunkKey))
				{
					value3.Chunks.Enqueue(chunkKey);
				}
			}
		}
	}

	private BlockPos BlockPosFromChunkLocalIndex(ChunkKey chunk, int index)
	{
		int num = index / (chunkSize * chunkSize);
		int num2 = index - num * chunkSize * chunkSize;
		int num3 = num2 / chunkSize;
		int num4 = num2 - num3 * chunkSize;
		DecodeChunkY(chunk.Y, out var localY, out var dimension);
		return new BlockPos(chunk.X * chunkSize + num4, localY * chunkSize + num, chunk.Z * chunkSize + num3, dimension);
	}

	private int ReadWorldBlock(BlockPos position)
	{
		IWorldChunk chunk = server.BlockAccessor.GetChunk(position.X >> 5, position.InternalY >> 5, position.Z >> 5);
		if (chunk == null || chunk.Disposed)
		{
			return -1;
		}
		return chunk.UnpackAndReadBlock(Index(position.X & chunkMask, position.InternalY & chunkMask, position.Z & chunkMask), 1);
	}

	private void LoadNeighbors(int chunkX, int internalChunkY, int chunkZ, IWorldChunk?[] neighbors, int dependencyMask)
	{
		DecodeChunkY(internalChunkY, out var localY, out var _);
		if ((dependencyMask & (1 << BlockFacing.NORTH.Index)) != 0)
		{
			neighbors[BlockFacing.NORTH.Index] = GetChunkAndBind(chunkX, internalChunkY, chunkZ - 1);
		}
		if ((dependencyMask & (1 << BlockFacing.EAST.Index)) != 0)
		{
			neighbors[BlockFacing.EAST.Index] = GetChunkAndBind(chunkX + 1, internalChunkY, chunkZ);
		}
		if ((dependencyMask & (1 << BlockFacing.SOUTH.Index)) != 0)
		{
			neighbors[BlockFacing.SOUTH.Index] = GetChunkAndBind(chunkX, internalChunkY, chunkZ + 1);
		}
		if ((dependencyMask & (1 << BlockFacing.WEST.Index)) != 0)
		{
			neighbors[BlockFacing.WEST.Index] = GetChunkAndBind(chunkX - 1, internalChunkY, chunkZ);
		}
		if ((dependencyMask & (1 << BlockFacing.UP.Index)) != 0 && localY + 1 < server.WorldMap.ChunkMapSizeY)
		{
			neighbors[BlockFacing.UP.Index] = GetChunkAndBind(chunkX, internalChunkY + 1, chunkZ);
		}
		if ((dependencyMask & (1 << BlockFacing.DOWN.Index)) != 0 && localY > 0)
		{
			neighbors[BlockFacing.DOWN.Index] = GetChunkAndBind(chunkX, internalChunkY - 1, chunkZ);
		}
	}

	private IWorldChunk? GetChunkAndBind(int chunkX, int internalChunkY, int chunkZ)
	{
		return server.BlockAccessor.GetChunk(chunkX, internalChunkY, chunkZ);
	}

	private int NeighborAvailabilityMask(IWorldChunk?[] neighbors, int dependencyMask)
	{
		int num = 0;
		BlockFacing[] aLLFACES = BlockFacing.ALLFACES;
		foreach (BlockFacing blockFacing in aLLFACES)
		{
			int num2 = 1 << blockFacing.Index;
			if ((dependencyMask & num2) != 0)
			{
				IWorldChunk worldChunk = neighbors[blockFacing.Index];
				if (worldChunk != null && !worldChunk.Disposed)
				{
					num |= num2;
				}
			}
		}
		return num;
	}

	private int CurrentNeighborAvailabilityMask(ChunkKey key, int dependencyMask)
	{
		DecodeChunkY(key.Y, out var localY, out var _);
		int mask = 0;
		Check(BlockFacing.NORTH, key.X, key.Y, key.Z - 1);
		Check(BlockFacing.EAST, key.X + 1, key.Y, key.Z);
		Check(BlockFacing.SOUTH, key.X, key.Y, key.Z + 1);
		Check(BlockFacing.WEST, key.X - 1, key.Y, key.Z);
		if (localY + 1 < server.WorldMap.ChunkMapSizeY)
		{
			Check(BlockFacing.UP, key.X, key.Y + 1, key.Z);
		}
		if (localY > 0)
		{
			Check(BlockFacing.DOWN, key.X, key.Y - 1, key.Z);
		}
		return mask;
		void Check(BlockFacing face, int x, int y, int z)
		{
			int num = 1 << face.Index;
			if ((dependencyMask & num) != 0)
			{
				IWorldChunk chunk = server.BlockAccessor.GetChunk(x, y, z);
				if (chunk != null && !chunk.Disposed)
				{
					mask |= num;
				}
			}
		}
	}

	private int Index(int x, int y, int z)
	{
		return (y * chunkSize + z) * chunkSize + x;
	}

	private long ChunkIndex3D(ChunkKey key)
	{
		DecodeChunkY(key.Y, out var localY, out var dimension);
		return server.WorldMap.ChunkIndex3D(key.X, localY, key.Z, dimension);
	}

	private static void DecodeChunkY(int internalChunkY, out int localY, out int dimension)
	{
		int num = 1024;
		dimension = internalChunkY / num;
		localY = internalChunkY % num;
		if (localY < 0)
		{
			localY += num;
			dimension--;
		}
	}

	private static string PlacedOreChunkSaveKey(ChunkKey chunk)
	{
		return FormattableString.Invariant(FormattableStringFactory.Create("{0}:{1}:{2}:{3}", "newfies:oreshield-playerplaced-v2", chunk.X, chunk.Y, chunk.Z));
	}

	private void UpdatePlacedOreChunkNoLock(WorldBlockKey key, bool added)
	{
		ChunkKey chunkKey = ChunkFor(key);
		if (!placedOreChunks.TryGetValue(chunkKey, out OreShieldDiscoveryCells value))
		{
			value = (placedOreChunks[chunkKey] = new OreShieldDiscoveryCells());
		}
		if (added)
		{
			value.Add(LocalIndex(key));
		}
		else
		{
			value.Remove(LocalIndex(key));
		}
		if (value.Count == 0)
		{
			placedOreChunks.Remove(chunkKey);
		}
		dirtyPlacedOreChunks.Add(chunkKey);
	}

	private void LoadPlayerPlacedOreExemptions()
	{
		try
		{
			byte[] data = api.WorldManager.SaveGame.GetData("newfies:oreshield-playerplaced-v2");
			if (data != null && data.Length >= 8)
			{
				using (BinaryReader binaryReader = new BinaryReader(new MemoryStream(data, writable: false)))
				{
					int num = binaryReader.ReadInt32();
					int num2 = binaryReader.ReadInt32();
					if (num != 2 || num2 < 0 || data.Length != 8 + (long)num2 * 12L)
					{
						throw new InvalidDataException("Invalid placed-ore manifest.");
					}
					for (int i = 0; i < num2; i++)
					{
						ChunkKey chunkKey = new ChunkKey(binaryReader.ReadInt32(), binaryReader.ReadInt32(), binaryReader.ReadInt32());
						byte[] array = api.WorldManager.SaveGame.GetData(PlacedOreChunkSaveKey(chunkKey)) ?? throw new InvalidDataException("Missing placed-ore shard.");
						using BinaryReader binaryReader2 = new BinaryReader(new MemoryStream(array, writable: false));
						if (array.Length < 8 || binaryReader2.ReadInt32() != 2)
						{
							throw new InvalidDataException("Invalid placed-ore shard.");
						}
						int num3 = binaryReader2.ReadInt32();
						if (num3 < 0 || num3 > chunkVolume || array.Length != 8 + (long)num3 * 2L)
						{
							throw new InvalidDataException("Invalid placed-ore shard length.");
						}
						for (int j = 0; j < num3; j++)
						{
							int num4 = binaryReader2.ReadUInt16();
							if (num4 >= chunkVolume)
							{
								throw new InvalidDataException("Invalid placed-ore cell.");
							}
							BlockPos blockPos = BlockPosFromChunkLocalIndex(chunkKey, num4);
							WorldBlockKey worldBlockKey = new WorldBlockKey(blockPos.X, blockPos.Y, blockPos.Z, blockPos.dimension);
							if (playerPlacedOreExemptions.Add(worldBlockKey))
							{
								UpdatePlacedOreChunkNoLock(worldBlockKey, added: true);
							}
						}
						persistedPlacedOreChunks.Add(chunkKey);
					}
					dirtyPlacedOreChunks.Clear();
					return;
				}
			}
			migratingPlacedOre = true;
			byte[] data2 = api.WorldManager.SaveGame.GetData("newfies:oreshield-playerplaced-v1");
			if (data2 == null || data2.Length == 0)
			{
				return;
			}
			if (data2.Length < 8)
			{
				throw new InvalidDataException("Invalid legacy placed-ore header.");
			}
			using BinaryReader binaryReader3 = new BinaryReader(new MemoryStream(data2, writable: false));
			if (binaryReader3.ReadInt32() != 1)
			{
				throw new InvalidDataException("Invalid legacy placed-ore version.");
			}
			int num5 = binaryReader3.ReadInt32();
			if (num5 < 0 || data2.Length != 8 + (long)num5 * 16L)
			{
				throw new InvalidDataException("Invalid legacy placed-ore length.");
			}
			for (int k = 0; k < num5; k++)
			{
				WorldBlockKey worldBlockKey2 = new WorldBlockKey(binaryReader3.ReadInt32(), binaryReader3.ReadInt32(), binaryReader3.ReadInt32(), binaryReader3.ReadInt32());
				if (playerPlacedOreExemptions.Add(worldBlockKey2))
				{
					UpdatePlacedOreChunkNoLock(worldBlockKey2, added: true);
				}
			}
		}
		catch (Exception ex)
		{
			placedOrePersistenceFailed = true;
			api.Logger.Warning("[OreShield] Placed-ore save could not be read; preserve it before repair: {0}", ex.Message);
		}
	}

	private void FlushPlacedOreOnWorldSave()
	{
		FlushPlacedOreCheckpoints(int.MaxValue);
	}

	private void FlushPlacedOreCheckpoints(int maxChunks)
	{
		if (placedOrePersistenceFailed)
		{
			return;
		}
		try
		{
			for (int num = 0; num < maxChunks; num++)
			{
				ChunkKey chunkKey;
				OreShieldDiscoveryCells oreShieldDiscoveryCells;
				lock (sync)
				{
					if (dirtyPlacedOreChunks.Count == 0)
					{
						break;
					}
					chunkKey = dirtyPlacedOreChunks.First();
					oreShieldDiscoveryCells = (placedOreChunks.TryGetValue(chunkKey, out OreShieldDiscoveryCells value) ? value.Clone() : null);
					goto IL_0080;
				}
				IL_0080:
				using MemoryStream memoryStream = new MemoryStream(8 + (oreShieldDiscoveryCells?.Count ?? 0) * 2);
				using BinaryWriter binaryWriter = new BinaryWriter(memoryStream);
				binaryWriter.Write(2);
				binaryWriter.Write(oreShieldDiscoveryCells?.Count ?? 0);
				if (oreShieldDiscoveryCells != null)
				{
					foreach (int item in oreShieldDiscoveryCells)
					{
						binaryWriter.Write((ushort)item);
					}
				}
				binaryWriter.Flush();
				byte[] array = memoryStream.ToArray();
				api.WorldManager.SaveGame.StoreData(PlacedOreChunkSaveKey(chunkKey), array);
				lock (sync)
				{
					dirtyPlacedOreChunks.Remove(chunkKey);
					if (oreShieldDiscoveryCells != null && oreShieldDiscoveryCells.Count > 0)
					{
						persistedPlacedOreChunks.Add(chunkKey);
					}
					else
					{
						persistedPlacedOreChunks.Remove(chunkKey);
					}
					placedManifestDirty = true;
					placedOreCheckpoints++;
					placedOreCheckpointBytes += array.Length;
				}
			}
			lock (sync)
			{
				if ((migratingPlacedOre && dirtyPlacedOreChunks.Count > 0) || !placedManifestDirty)
				{
					return;
				}
				using MemoryStream memoryStream2 = new MemoryStream(8 + persistedPlacedOreChunks.Count * 12);
				using BinaryWriter binaryWriter2 = new BinaryWriter(memoryStream2);
				binaryWriter2.Write(2);
				binaryWriter2.Write(persistedPlacedOreChunks.Count);
				foreach (ChunkKey persistedPlacedOreChunk in persistedPlacedOreChunks)
				{
					binaryWriter2.Write(persistedPlacedOreChunk.X);
					binaryWriter2.Write(persistedPlacedOreChunk.Y);
					binaryWriter2.Write(persistedPlacedOreChunk.Z);
				}
				binaryWriter2.Flush();
				api.WorldManager.SaveGame.StoreData("newfies:oreshield-playerplaced-v2", memoryStream2.ToArray());
				placedManifestDirty = false;
				migratingPlacedOre = false;
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[OreShield] Player-placed ore checkpoint retained for retry: {0}", ex.Message);
		}
	}

	private bool MarkPlayerPlacedOreExemption(BlockPos pos, bool persist)
	{
		if (pos == null)
		{
			return false;
		}
		WorldBlockKey worldBlockKey = new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension);
		ChunkKey key = ChunkFor(worldBlockKey);
		bool flag;
		lock (sync)
		{
			flag = playerPlacedOreExemptions.Add(worldBlockKey);
			if (flag)
			{
				UpdatePlacedOreChunkNoLock(worldBlockKey, added: true);
				BumpRevisionNoLock(key);
				InvalidateNoLock(key);
				RemoveOreIndexNoLock(key);
			}
		}
		return flag;
	}

	private bool RemovePlayerPlacedOreExemption(BlockPos pos, bool persist)
	{
		if (pos == null)
		{
			return false;
		}
		WorldBlockKey worldBlockKey = new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension);
		ChunkKey key = ChunkFor(worldBlockKey);
		bool flag;
		lock (sync)
		{
			flag = playerPlacedOreExemptions.Remove(worldBlockKey);
			if (flag)
			{
				UpdatePlacedOreChunkNoLock(worldBlockKey, added: false);
				BumpRevisionNoLock(key);
				InvalidateNoLock(key);
				RemoveOreIndexNoLock(key);
			}
		}
		return flag;
	}

	private void InvalidateAround(BlockPos pos)
	{
		int x = pos.X;
		int internalY = pos.InternalY;
		int z = pos.Z;
		ChunkKey key = new ChunkKey(x >> 5, internalY >> 5, z >> 5);
		bool flag = IsProtectedOreId(ReadWorldBlock(pos));
		WorldBlockKey worldBlockKey = new WorldBlockKey(pos.X, pos.Y, pos.Z, pos.dimension);
		bool flag2 = false;
		lock (sync)
		{
			if (!flag)
			{
				flag2 = playerPlacedOreExemptions.Remove(worldBlockKey);
			}
			if (flag2)
			{
				UpdatePlacedOreChunkNoLock(worldBlockKey, added: false);
			}
		}
		lock (sync)
		{
			BumpRevisionNoLock(key);
			InvalidateNoLock(key);
			if (!flag)
			{
				ChunkKey chunkKey = ChunkFor(worldBlockKey);
				int index = LocalIndex(worldBlockKey);
				if (discoveryOwners.TryGetValue(chunkKey, out HashSet<string> value))
				{
					string[] array = value.ToArray();
					foreach (string text in array)
					{
						discoveryInvalidationOwnersVisited++;
						Dictionary<ChunkKey, OreShieldDiscoveryCells> dictionary = playerDiscoveries[text];
						OreShieldDiscoveryCells oreShieldDiscoveryCells = dictionary[chunkKey];
						if (oreShieldDiscoveryCells.Remove(index))
						{
							discoveryCells--;
						}
						if (oreShieldDiscoveryCells.Count == 0)
						{
							dictionary.Remove(chunkKey);
							UntrackDiscoveryOwnerNoLock(text, chunkKey);
							if (dictionary.Count == 0)
							{
								playerDiscoveries.Remove(text);
							}
						}
					}
				}
			}
		}
		QueueWorldChange(pos, reassert: false);
	}

	private void BumpRevisionNoLock(ChunkKey key)
	{
		if (!revisions.ContainsKey(key) && revisions.Count >= 16384)
		{
			revisionEpoch = ++nextRevision;
			revisions.Clear();
			revisionCompactions++;
		}
		revisions[key] = ++nextRevision;
		revisionInvalidations++;
	}

	private long RevisionNoLock(ChunkKey key)
	{
		long value;
		return revisions.TryGetValue(key, out value) ? value : revisionEpoch;
	}

	private long RevisionSignatureNoLock(ChunkKey key, int neighborDependencyMask)
	{
		long hash = 1469598103934665603L;
		Mix(RevisionNoLock(key));
		DecodeChunkY(key.Y, out var localY, out var _);
		if ((neighborDependencyMask & (1 << BlockFacing.WEST.Index)) != 0)
		{
			Mix(RevisionNoLock(new ChunkKey(key.X - 1, key.Y, key.Z)));
		}
		if ((neighborDependencyMask & (1 << BlockFacing.EAST.Index)) != 0)
		{
			Mix(RevisionNoLock(new ChunkKey(key.X + 1, key.Y, key.Z)));
		}
		if ((neighborDependencyMask & (1 << BlockFacing.NORTH.Index)) != 0)
		{
			Mix(RevisionNoLock(new ChunkKey(key.X, key.Y, key.Z - 1)));
		}
		if ((neighborDependencyMask & (1 << BlockFacing.SOUTH.Index)) != 0)
		{
			Mix(RevisionNoLock(new ChunkKey(key.X, key.Y, key.Z + 1)));
		}
		if ((neighborDependencyMask & (1 << BlockFacing.DOWN.Index)) != 0 && localY > 0)
		{
			Mix(RevisionNoLock(new ChunkKey(key.X, key.Y - 1, key.Z)));
		}
		if ((neighborDependencyMask & (1 << BlockFacing.UP.Index)) != 0 && localY + 1 < server.WorldMap.ChunkMapSizeY)
		{
			Mix(RevisionNoLock(new ChunkKey(key.X, key.Y + 1, key.Z)));
		}
		return hash;
		void Mix(long value)
		{
			hash ^= value;
			hash *= 1099511628211L;
		}
	}

	private int ComputeNeighborDependencyMask(int[] candidateIndexes)
	{
		int num = 0;
		foreach (int num2 in candidateIndexes)
		{
			if ((uint)num2 < (uint)chunkVolume)
			{
				int num3 = num2 & chunkMask;
				int num4 = (num2 >> 5) & chunkMask;
				int num5 = num2 >> 10;
				if (num3 == 0)
				{
					num |= 1 << BlockFacing.WEST.Index;
				}
				if (num3 == chunkMask)
				{
					num |= 1 << BlockFacing.EAST.Index;
				}
				if (num4 == 0)
				{
					num |= 1 << BlockFacing.NORTH.Index;
				}
				if (num4 == chunkMask)
				{
					num |= 1 << BlockFacing.SOUTH.Index;
				}
				if (num5 == 0)
				{
					num |= 1 << BlockFacing.DOWN.Index;
				}
				if (num5 == chunkMask)
				{
					num |= 1 << BlockFacing.UP.Index;
				}
				if (num == 63)
				{
					break;
				}
			}
		}
		return num;
	}

	private void InvalidateNeighborDependencyNoLock(ChunkKey key, BlockFacing requiredFace)
	{
		if (cache.TryGetValue(key, out LinkedListNode<CacheEntry> value) && (value.Value.NeighborDependencyMask & (1 << requiredFace.Index)) != 0)
		{
			RemoveNoLock(value);
			revisionInvalidations++;
		}
	}

	private bool InvalidateNoLock(ChunkKey key)
	{
		if (!cache.TryGetValue(key, out LinkedListNode<CacheEntry> value))
		{
			return false;
		}
		RemoveNoLock(value);
		return true;
	}

	private void AddCacheNoLock(ChunkKey key, long signature, ulong sourceFingerprint, byte[]? clientBlocks, bool passThrough, int neighborDependencyMask, int neighborAvailabilityMask)
	{
		if (config.OreShieldChunkCacheEnabled)
		{
			if (cache.TryGetValue(key, out LinkedListNode<CacheEntry> value))
			{
				RemoveNoLock(value);
			}
			CacheEntry cacheEntry = new CacheEntry
			{
				Key = key,
				RevisionSignature = signature,
				SourceFingerprint = sourceFingerprint,
				ClientBlocks = clientBlocks,
				PassThrough = passThrough,
				NeighborDependencyMask = neighborDependencyMask,
				NeighborAvailabilityMask = neighborAvailabilityMask
			};
			LinkedListNode<CacheEntry> value2 = lru.AddLast(cacheEntry);
			cache[key] = value2;
			cacheBytes += cacheEntry.OwnedBytes;
			TrimCacheNoLock();
		}
	}

	private void TouchNoLock(LinkedListNode<CacheEntry> node)
	{
		lru.Remove(node);
		lru.AddLast(node);
	}

	private void TrimCacheNoLock()
	{
		long num = (long)Math.Clamp(config.OreShieldChunkCacheMiB, 8, 1024) * 1024L * 1024;
		int num2 = Math.Clamp(config.OreShieldMaxCachedChunks, 256, 16384);
		while (lru.First != null && (cacheBytes > num || cache.Count > num2))
		{
			RemoveNoLock(lru.First);
		}
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
			foreach (KeyValuePair<ChunkKey, OreIndexEntry> item in oreIndexCache)
			{
				if (item.Value.PendingExposure != null)
				{
					pendingExposureBuilds.Enqueue(item.Key);
				}
			}
		}
		pendingExposureBuilds.Enqueue(key);
		TouchOreIndexNoLock(key);
		TrimOreIndexCacheNoLock();
	}

	private void TouchOreIndexNoLock(ChunkKey key)
	{
		if (oreIndexLruNodes.TryGetValue(key, out LinkedListNode<ChunkKey> value))
		{
			oreIndexLru.Remove(value);
			oreIndexLru.AddLast(value);
		}
		else
		{
			LinkedListNode<ChunkKey> value2 = oreIndexLru.AddLast(key);
			oreIndexLruNodes[key] = value2;
		}
	}

	private void RemoveOreIndexNoLock(ChunkKey key)
	{
		oreIndexCache.Remove(key);
		BlockFacing[] aLLFACES = BlockFacing.ALLFACES;
		foreach (BlockFacing blockFacing in aLLFACES)
		{
			boundaryCursors.Remove(new BoundaryRefreshKey(key, blockFacing.Index));
		}
		if (oreIndexLruNodes.TryGetValue(key, out LinkedListNode<ChunkKey> value))
		{
			oreIndexLruNodes.Remove(key);
			oreIndexLru.Remove(value);
		}
	}

	private void TrimOreIndexCacheNoLock()
	{
		int num = Math.Clamp(config.OreShieldMaxCachedChunks, 256, 16384);
		while (oreIndexCache.Count > num && oreIndexLru.First != null)
		{
			RemoveOreIndexNoLock(oreIndexLru.First.Value);
		}
	}

	private static ulong FingerprintBlocks(byte[] bytes)
	{
		ulong num = 14695981039346656037uL;
		for (int i = 0; i < bytes.Length; i++)
		{
			num ^= bytes[i];
			num *= 1099511628211L;
		}
		num ^= (ulong)bytes.Length;
		return num * 1099511628211L;
	}

	private void RemoveNoLock(LinkedListNode<CacheEntry> node)
	{
		cache.Remove(node.Value.Key);
		lru.Remove(node);
		cacheBytes -= node.Value.OwnedBytes;
		if (cacheBytes < 0)
		{
			cacheBytes = 0L;
		}
	}

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}
		disposed = true;
		FlushPlacedOreOnWorldSave();
		try
		{
			api.Event.GameWorldSave -= FlushPlacedOreOnWorldSave;
		}
		catch
		{
		}
		try
		{
			api.Event.UnregisterGameTickListener(visibilityTickListenerId);
		}
		catch
		{
		}
		try
		{
			api.Event.UnregisterGameTickListener(clientSyncTickListenerId);
		}
		catch
		{
		}
		lock (sync)
		{
			cache.Clear();
			lru.Clear();
			revisions.Clear();
			pendingAuthoritativeCells.Clear();
			pendingExposureRefreshChunks = null;
			pendingWorldChanges.Clear();
			pendingExposureBuilds.Clear();
			boundaryCursors.Clear();
			pendingVeins.Clear();
			foreach (ResyncJob value in pendingDiscoveryResyncs.Values)
			{
				value.Cells?.Dispose();
			}
			pendingDiscoveryResyncs.Clear();
			pendingBoundaryRefreshes.Clear();
			pendingBoundaryRefreshKeys.Clear();
			oreIndexCache.Clear();
			oreIndexLru.Clear();
			oreIndexLruNodes.Clear();
			playerDiscoveries.Clear();
			discoveryOwners.Clear();
			discoveryChunkRecords = 0L;
			playerPlacedOreExemptions.Clear();
			placedOreChunks.Clear();
			dirtyPlacedOreChunks.Clear();
			persistedPlacedOreChunks.Clear();
			playerVisibilityStates.Clear();
			pendingClientSync.Clear();
			clientSyncPendingByPlayer.Clear();
			clientSyncPendingByChunk.Clear();
			clientSyncDeadlines.Clear();
			clientSyncOutbox.Clear();
			clientSyncRevealQueue.Clear();
			clientSyncWorldQueue.Clear();
			clientSyncQueuedCells = 0;
			clientSyncLastResyncRequestMs.Clear();
			clientSyncLastFailureResyncMs.Clear();
			recentProtectedOreBreaks.Clear();
			cacheBytes = 0L;
			discoveryCells = 0L;
		}
	}
}
