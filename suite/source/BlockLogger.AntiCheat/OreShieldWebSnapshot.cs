using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class OreShieldWebSnapshot
{
	public double VisibilityLastMilliseconds { get; set; }

	public double VisibilityMaxMilliseconds { get; set; }

	public int PendingWorldChanges { get; set; }

	public int PendingAuthoritativeCells { get; set; }

	public long AuthoritativeCorrectionFlushes { get; set; }

	public int PlacedOreDirtyChunks { get; set; }

	public long PlacedOreCheckpoints { get; set; }

	public long PlacedOreCheckpointBytes { get; set; }

	public long WorldChangeCoalesces { get; set; }

	public long WorldChangeOverflows { get; set; }

	public long ExposureBuildCells { get; set; }

	public bool Enabled { get; set; }

	public bool Active { get; set; }

	public string Status { get; set; } = "";

	public int ProtectedOreVariants { get; set; }

	public int HostVariants { get; set; }

	public int SkippedOreCandidates { get; set; }

	public bool ProtectModdedOres { get; set; }

	public bool RevealOnUnknownNeighbor { get; set; }

	public bool StrictOreMatching { get; set; }

	public int RevealRadiusBlocks { get; set; } = 0;

	public bool RevealWholeVeinOnMine { get; set; } = false;

	public int MaxVeinRevealBlocks { get; set; } = 2048;

	public int DiscoveryRangeBlocks { get; set; } = 96;

	public int VisibilityScanIntervalMs { get; set; } = 250;

	public int MaxLosChecksPerPass { get; set; } = 96;

	public int ViewConeDegrees { get; set; } = 140;

	public int IdleRescanMs { get; set; } = 3000;

	public bool LeakAuditEnabled { get; set; } = true;

	public bool ChunkCacheEnabled { get; set; }

	public int ChunkCacheMiB { get; set; } = 96;

	public int MaxCachedChunks { get; set; } = 4096;

	public List<string> AdditionalOrePatterns { get; set; } = new List<string>();

	public List<string> ExcludedBlockPatterns { get; set; } = new List<string>();

	public int CacheEntries { get; set; }

	public long CacheBytes { get; set; }

	public long CacheHits { get; set; }

	public long CacheMisses { get; set; }

	public long ChunkMaskHookCalls { get; set; }

	public long ChunkMaskUniquePackets { get; set; }

	public long ChunkMaskEligiblePackets { get; set; }

	public long FailClosedChunkPackets { get; set; }

	public long FailClosedCells { get; set; }

	public long ChunksInspected { get; set; }

	public long ChunksMasked { get; set; }

	public long PassThroughChunks { get; set; }

	public long VirtualizedBlocks { get; set; }

	public long HiddenRealOreBlocks { get; set; }

	public long DecoyBlocks { get; set; }

	public long RewrittenUpdatePackets { get; set; }

	public long UnsupportedCompressionPackets { get; set; }

	public long OreIndexCacheHits { get; set; }

	public long OreIndexCacheMisses { get; set; }

	public int OreIndexCacheEntries { get; set; }

	public long RevisionInvalidations { get; set; }

	public long MiningRevealPulses { get; set; }

	public long MiningRevealCorrections { get; set; }

	public long ExposureSyncEvents { get; set; }

	public long ExposureBlocksEvaluated { get; set; }

	public long ExposureCorrectionsSent { get; set; }

	public int PendingVeinJobs { get; set; }

	public long OldestVeinJobAgeMs { get; set; }

	public long VeinLimitReachedJobs { get; set; }

	public long VeinDiscoveries { get; set; }

	public long VeinBlocksRevealed { get; set; }

	public long RevealRadiusPasses { get; set; }

	public long RevealPlayersChecked { get; set; }

	public long RevealDesiredBlocks { get; set; }

	public long RevealCorrectionPackets { get; set; }

	public long RevealRealBlockPackets { get; set; }

	public long ReconcealBlockPackets { get; set; }

	public long LosPasses { get; set; }

	public long LosPlayersChecked { get; set; }

	public long LosCandidatesConsidered { get; set; }

	public long LosRayChecks { get; set; }

	public long LosDiscoveries { get; set; }

	public long LosMovementSkips { get; set; }

	public long VisibilityChunksVisited { get; set; }

	public long VisibilityCandidatesEvaluated { get; set; }

	public long VisibilityIndexBuilds { get; set; }

	public long VisibilityBudgetDeferrals { get; set; }

	public int VisibilityPlayersPerTickLimit { get; set; }

	public int VisibilityChunksPerPlayerPassLimit { get; set; }

	public int VisibilityCandidatesPerPlayerPassLimit { get; set; }

	public int VisibilityIndexBuildsPerTickLimit { get; set; }

	public int BoundaryRefreshesPending { get; set; }

	public long BoundaryRefreshCellsEvaluated { get; set; }

	public long BoundaryRefreshOverflowInvalidations { get; set; }

	public int BoundaryRefreshesPerTickLimit { get; set; }

	public long ExposedIndexCandidates { get; set; }

	public long LeakAuditChecks { get; set; }

	public long LeakAuditViolations { get; set; }

	public int ClientSyncReadyClients { get; set; }

	public long ClientSyncHelloPackets { get; set; }

	public long ClientSyncRejectedHellos { get; set; }

	public long ClientSyncBatchesSent { get; set; }

	public long ClientSyncEntriesSent { get; set; }

	public long ClientSyncAcks { get; set; }

	public long ClientSyncDeferredAcks { get; set; }

	public long ClientSyncAppliedEntries { get; set; }

	public long ClientSyncFailedEntries { get; set; }

	public long ClientSyncSkippedEntries { get; set; }

	public long ClientSyncApplyFailureBatches { get; set; }

	public long ClientSyncFailureResyncs { get; set; }

	public long ClientSyncFallbacks { get; set; }

	public long ClientSyncTimeouts { get; set; }

	public long ClientSyncRetries { get; set; }

	public long ClientSyncResyncRequests { get; set; }

	public int ClientSyncPendingBatches { get; set; }

	public int ClientSyncQueuedCells { get; set; }

	public long ClientSyncQueueCoalesces { get; set; }

	public long ClientSyncQueueOverflows { get; set; }

	public long ClientSyncStallDeferrals { get; set; }

	public int DiscoveryPlayers { get; set; }

	public long DiscoveryCells { get; set; }

	public long DiscoveryChunkRecords { get; set; }

	public int DiscoveryDistinctChunks { get; set; }

	public long DiscoveryInvalidationOwnersVisited { get; set; }

	public int RevisionEntries { get; set; }

	public long RevisionCompactions { get; set; }

	public int SyncProtocolVersion { get; set; }

	public string SyncCapability { get; set; } = "";

	public string SyncBuildMarker { get; set; } = "";

	public long RevealDispatchAttempts { get; set; }

	public long RevealRecipientResolveFailures { get; set; }

	public long RevealChunkNotSentSkips { get; set; }

	public long RevealStaleKeySkips { get; set; }

	public long RevealDirectPacketsSent { get; set; }

	public long RevealDirectPacketErrors { get; set; }

	public long RevealClientSyncUnsupportedSkips { get; set; }

	public long HookErrors { get; set; }
}
