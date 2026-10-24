namespace NewfiesOptimizer;

public sealed class OptimizerConfig
{
    public bool Enabled { get; set; } = true;
    public ProfilingOptions Profiling { get; set; } = new();
    public EngineOptimizationOptions EngineOptimizations { get; set; } = new();
    public ExperimentalOptions EntityOptimization { get; set; } = new();
    public ExperimentalOptions ChunkOptimization { get; set; } = new();
    public ExperimentalOptions BlockEntities { get; set; } = new();
    public ExperimentalOptions MemoryManagement { get; set; } = new();
    public ExperimentalOptions Networking { get; set; } = new();
    public ExperimentalOptions Scheduling { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
    public DashboardOptions Dashboard { get; set; } = new();
    public CompatibilityOptions Compatibility { get; set; } = new();
    public BenchmarkOptions Benchmarking { get; set; } = new();

    public void Normalize()
    {
        Profiling ??= new(); EngineOptimizations ??= new(); EntityOptimization ??= new(); ChunkOptimization ??= new(); BlockEntities ??= new();
        MemoryManagement ??= new(); Networking ??= new(); Scheduling ??= new(); Logging ??= new();
        Dashboard ??= new(); Compatibility ??= new(); Benchmarking ??= new();
        EngineOptimizations.BlockSimulationBudgetMs = Math.Clamp(EngineOptimizations.BlockSimulationBudgetMs, 1, 20);
        EngineOptimizations.EntityProtectionRadius = Math.Clamp(EngineOptimizations.EntityProtectionRadius, 48, 256);
        EngineOptimizations.AiDistanceIntervalMs = Math.Clamp(EngineOptimizations.AiDistanceIntervalMs, 100, 1000);
        EngineOptimizations.DistantWanderIntervalMs = Math.Clamp(EngineOptimizations.DistantWanderIntervalMs, 250, 10000);
        EngineOptimizations.RepulsionDistanceIntervalMs = Math.Clamp(EngineOptimizations.RepulsionDistanceIntervalMs, 50, 500);
        EngineOptimizations.EntityPolicyInclude = (EngineOptimizations.EntityPolicyInclude ?? ["game:*"]).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Take(64).ToArray();
        EngineOptimizations.EntityPolicyExclude = (EngineOptimizations.EntityPolicyExclude ?? []).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Take(64).ToArray();
        EngineOptimizations.PathMainThreadBudgetMs = Math.Clamp(EngineOptimizations.PathMainThreadBudgetMs, 1, 10);
        EngineOptimizations.PathMainThreadMaxSearches = Math.Clamp(EngineOptimizations.PathMainThreadMaxSearches, 1, 8);
        EngineOptimizations.PathMainThreadGraceMs = Math.Clamp(EngineOptimizations.PathMainThreadGraceMs, 0, 5000);
        EngineOptimizations.AssetCacheMaxMiB = Math.Clamp(EngineOptimizations.AssetCacheMaxMiB, 1, 32);
        EngineOptimizations.MinimumNearbyClients = Math.Clamp(EngineOptimizations.MinimumNearbyClients, 0, 4096);
        Profiling.SampleIntervalSeconds = Math.Clamp(Profiling.SampleIntervalSeconds, 1, 60);
        Profiling.MaxSamples = Math.Clamp(Profiling.MaxSamples, 60, 7200);
        Profiling.SystemSampleEvery = Math.Clamp(Profiling.SystemSampleEvery, 1, 1000);
        Profiling.MaxHotspots = Math.Clamp(Profiling.MaxHotspots, 8, 128);
        Profiling.AutoStopSeconds = Math.Clamp(Profiling.AutoStopSeconds, 0, 86400);
        Benchmarking.MaxEvidenceBytes = Math.Clamp(Benchmarking.MaxEvidenceBytes, 65536, 67108864);
        Benchmarking.PresenceIntervalSeconds = Math.Clamp(Benchmarking.PresenceIntervalSeconds, 5, 60);
        Benchmarking.RunId = (Benchmarking.RunId ?? "").Length <= 128 ? Benchmarking.RunId ?? "" : Benchmarking.RunId![..128];
        Logging.MaxExports = Math.Clamp(Logging.MaxExports, 1, 100);
    }
}

public sealed class ProfilingOptions
{
    public bool AutoStart { get; set; }
    public bool TickMetrics { get; set; } = true;
    public bool ProcessMetrics { get; set; } = true;
    public bool WorldCounts { get; set; } = true;
    public bool NetworkCounters { get; set; } = true;
    public bool MainThreadCpu { get; set; }
    public bool SystemTimings { get; set; }
    public bool PhaseTimings { get; set; }
    public bool CallbackTimings { get; set; }
    public int SystemSampleEvery { get; set; } = 10;
    public int SampleIntervalSeconds { get; set; } = 1;
    public int MaxSamples { get; set; } = 3600;
    public int MaxHotspots { get; set; } = 64;
    public int AutoStopSeconds { get; set; }
    public bool ExportOnStop { get; set; } = true;
}

// These sections declare intended research areas, not implemented optimizations.
// Setting Enabled cannot silently change gameplay: the runtime reports them unsupported.
public sealed class ExperimentalOptions
{
    public bool Enabled { get; set; }
}
public sealed class LoggingOptions { public int MaxExports { get; set; } = 12; }
public sealed class DashboardOptions { public bool Enabled { get; set; } }
public sealed class CompatibilityOptions
{
    public bool SafeMode { get; set; } = false;
    public bool DisableHookOnConflict { get; set; } = true;
    public bool RequireExactGameVersion { get; set; } = true;
}
public sealed class BenchmarkOptions
{
    public bool WriteSessionEvidence { get; set; }
    public int MaxEvidenceBytes { get; set; } = 16777216;
    public int PresenceIntervalSeconds { get; set; } = 10;
    public string RunId { get; set; } = "";
}

// Each API patch or opt-in scheduling policy is independently switchable. SafeMode disables every engine module.
public sealed class EngineOptimizationOptions
{
    public bool BlockSimulationBudget { get; set; } = false;
    public int BlockSimulationBudgetMs { get; set; } = 5;
    public bool BarrelMatchCountGuard { get; set; } = false;
    public bool IdleFirepitFastPath { get; set; } = false;
    public bool BarrelIngredientPairing { get; set; } = true;
    public bool WildBushCallbackFlatten { get; set; } = true;
    public bool ChunkUnloadStagingReuse { get; set; } = false;
    public bool NativeBuildLogGuard { get; set; } = false;
    public bool CollisionGenerationReuse { get; set; }
    public bool CachedCollisionGenerationReuse { get; set; }
    public bool AnimationCollisionFlagScan { get; set; } = true;
    public bool CollisionTraversalReuse { get; set; }
    public bool ContainerTransitionScan { get; set; }
    public bool ContainerTickTraversal { get; set; }
    public bool NearbyEntityListReuse { get; set; }
    public bool CuboidEntityListReuse { get; set; }
    public bool ShapelessStackMerge { get; set; }
    public bool ShapelessWildcardMatch { get; set; }
    public bool DelayedCallbackSnapshotReuse { get; set; }
    public bool SpawnOpportunityListReuse { get; set; }
    public bool SpawnColumnArrayReuse { get; set; }
    public bool BlockUpdateListReuse { get; set; }
    public bool ChunkLoadRequestListReuse { get; set; }
    public bool ChunkUnloadListReuse { get; set; }
    public bool BroadcastFilterReuse { get; set; }
    public bool EntityLifecycleListReuse { get; set; }
    public bool AiDistanceThrottle { get; set; }
    public bool DistantWanderThrottle { get; set; }
    public bool RepulsionDistanceThrottle { get; set; }
    public bool WaypointNodePooling { get; set; }
    public bool AttributeNoOpSkip { get; set; }
    public int EntityProtectionRadius { get; set; } = 64;
    public int AiDistanceIntervalMs { get; set; } = 250;
    public int DistantWanderIntervalMs { get; set; } = 1500;
    public int RepulsionDistanceIntervalMs { get; set; } = 100;
    public string[] EntityPolicyInclude { get; set; } = ["game:*"];
    public string[] EntityPolicyExclude { get; set; } = ["game:player", "game:trader-*"];
    public bool DespawnPacketProjection { get; set; } = true;
    public bool PhysicsPacketListReuse { get; set; } = true;
    public bool WorldgenRequestReuse { get; set; } = true;
    public bool SaveSnapshotRelease { get; set; } = true;
    public bool PathQueueLimitFix { get; set; } = true;
    public bool PathMainThreadBudget { get; set; }
    public int PathMainThreadBudgetMs { get; set; } = 2;
    public int PathMainThreadMaxSearches { get; set; } = 1;
    public int PathMainThreadGraceMs { get; set; } = 300;
    public bool NearestEntitySearch { get; set; } = true;
    public bool InteractableEntitySearch { get; set; } = true;
    public bool AiTaskStartDispatch { get; set; } = true;
    public bool AiFilterDispatch { get; set; } = true;
    public bool RepulseDelegateReuse { get; set; } = true;
    public bool OnlinePlayerSnapshots { get; set; } = true;
    public bool NearbyPlayerQueries { get; set; } = false;
    public int MinimumNearbyClients { get; set; } = 128;
    public bool BulkStreamReads { get; set; } = true;
    public bool AssetCompressionCache { get; set; } = true;
    public int AssetCacheMaxMiB { get; set; } = 16;
}
