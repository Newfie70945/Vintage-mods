using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace NewfiesOptimizer;

// Each module owns only its own prefix. No foreign patches or server files are modified.
public sealed class EngineOptimizations : IDisposable
{
    public const string HarmonyId = "newfies.serveroptimizer.engine.0.20.15";
    // RuntimeProfiler.HarmonyId refers to this. It lives here because module test projects compile this file without the profiler.
    internal const string ProfilerHarmonyId = "newfies.serveroptimizer.profiling.0.9.0";
    private readonly ICoreServerAPI api;
    private readonly Harmony harmony = new(HarmonyId);
    private readonly List<Entry> entries = new();
    private readonly object gate = new();
    private long listener = -1;
    private bool disposed;
    private bool checking;
    private readonly Dictionary<MethodBase,Patches?> patchSnapshots = new();
    private readonly int minimumNearbyClients;
    private readonly int assetCacheBytes;
    private readonly Func<int> clientCount;
    private sealed class Entry(string name, bool requested, Func<MethodInfo> target, Func<MethodBase[]> dependencies, MethodInfo? prefix, Action<bool> enable)
    {
        public string Name = name, State = "pending", Reason = "Not installed";
        public bool Requested = requested;
        public Func<MethodInfo> Target = target;
        public Func<MethodBase[]> Dependencies = dependencies;
        public MethodInfo? Prefix = prefix;
        public Action<bool> Enable = enable;
        public bool Installed;
        public long FailureBaseline;
        public MethodInfo? Postfix;
        public MethodInfo? Finalizer;
        public MethodInfo? Transpiler;
        private MethodInfo? resolvedTarget;
        private MethodBase[]? resolvedDependencies;
        public MethodInfo TargetMethod => resolvedTarget ??= Target();
        public MethodBase[] DependencyMethods => resolvedDependencies ??= Dependencies();
    }
    public EngineOptimizations(ICoreServerAPI api, OptimizerConfig config, Func<int>? clientCount = null)
    {
        this.api = api;
        minimumNearbyClients = config.EngineOptimizations.MinimumNearbyClients;
        assetCacheBytes = config.EngineOptimizations.AssetCacheMaxMiB * 1048576;
        AssetCompressionOptimizations.Configure(false);
        PhysicsPacketOptimizations.Configure(false);
        DespawnPacketOptimizations.Configure(false);
        PathQueueOptimizations.ConfigureLimit(false); PathQueueOptimizations.ConfigureBudget(false);
        EntitySearchOptimizations.ConfigureNearest(false); EntitySearchOptimizations.ConfigureInteractable(false);
        AiTaskStartOptimizations.Configure(false); AiFilterOptimizations.Configure(false); RepulseDelegateOptimizations.Configure(false);
        WorldgenRequestOptimizations.Configure(false); SaveSnapshotOptimizations.Configure(false);
        this.clientCount = clientCount ?? (() => api.World is ServerMain world ? world.Clients.Values.Count : 0);
        PlayerQueryOptimizations.MinimumNearbyClients = minimumNearbyClients;
        PlayerQueryOptimizations.SnapshotEnabled = false;
        PlayerQueryOptimizations.AroundEnabled = false;
        StreamCopyOptimizations.ConfigureRead(false);
        var options = config.EngineOptimizations;

        string[] blockNames = ["idleFirepitFastPath", "barrelIngredientPairing", "wildBushCallbackFlatten"];
        bool[] blockFlags = [options.IdleFirepitFastPath, options.BarrelIngredientPairing, options.WildBushCallbackFlatten];
        string[] blockPrefixes = [nameof(BlockTickOptimizations.FirepitPrefix), nameof(BlockTickOptimizations.BarrelPrefix), nameof(BlockTickOptimizations.BushPrefix)];
        string[] blockFinalizers = [nameof(BlockTickOptimizations.FirepitFinalizer), nameof(BlockTickOptimizations.BarrelFinalizer), nameof(BlockTickOptimizations.BushFinalizer)];
        for (int i = 0; i < 3; i++)
        {
            int kind = i; BlockTickOptimizations.Configure(kind, false);
            entries.Add(new(blockNames[kind], blockFlags[kind], () => BlockTickOptimizations.Target(kind), () => BlockTickOptimizations.Dependencies(kind), AccessTools.Method(typeof(BlockTickOptimizations), blockPrefixes[kind]), v => BlockTickOptimizations.Configure(kind, v))
            { Finalizer = AccessTools.Method(typeof(BlockTickOptimizations), blockFinalizers[kind]) });
        }
        CraftingCandidateOptimizations.Configure(false);
        entries.Add(new("craftingCandidateDedup",options.CraftingCandidateDedup,()=>CraftingCandidateOptimizations.Target,
            ()=>CraftingCandidateOptimizations.Dependencies,AccessTools.Method(typeof(CraftingCandidateOptimizations),nameof(CraftingCandidateOptimizations.Prefix)),CraftingCandidateOptimizations.Configure)
            {Transpiler=AccessTools.Method(typeof(CraftingCandidateOptimizations),nameof(CraftingCandidateOptimizations.Transpiler)),Finalizer=AccessTools.Method(typeof(CraftingCandidateOptimizations),nameof(CraftingCandidateOptimizations.Finalizer))});
        BlockSimulationBudget.Configure(false);BlockSimulationBudget.SetBudget(options.BlockSimulationBudgetMs);
        entries.Add(new("blockSimulationBudget",options.BlockSimulationBudget,()=>BlockSimulationBudget.Target,()=>BlockSimulationBudget.Dependencies,AccessTools.Method(typeof(BlockSimulationBudget),nameof(BlockSimulationBudget.Prefix)),BlockSimulationBudget.Configure){Transpiler=AccessTools.Method(typeof(BlockSimulationBudget),nameof(BlockSimulationBudget.Transpiler)),Finalizer=AccessTools.Method(typeof(BlockSimulationBudget),nameof(BlockSimulationBudget.Finalizer))});
        BarrelCountOptimizations.Configure(false);
        entries.Add(new("barrelMatchCountGuard",options.BarrelMatchCountGuard,()=>BarrelCountOptimizations.Target,()=>BarrelCountOptimizations.Dependencies,AccessTools.Method(typeof(BarrelCountOptimizations),nameof(BarrelCountOptimizations.Prefix)),BarrelCountOptimizations.Configure){Finalizer=AccessTools.Method(typeof(BarrelCountOptimizations),nameof(BarrelCountOptimizations.Finalizer))});
        BarrelRecipeMemoOptimizations.Configure(false);BarrelRecipeMemoOptimizations.SetRescanEvery(options.BarrelRecipeRescanEvery);
        entries.Add(new("barrelRecipeMemo",options.BarrelRecipeMemo,()=>BarrelRecipeMemoOptimizations.Target,()=>BarrelRecipeMemoOptimizations.Dependencies,AccessTools.Method(typeof(BarrelRecipeMemoOptimizations),nameof(BarrelRecipeMemoOptimizations.Prefix)),BarrelRecipeMemoOptimizations.Configure){Postfix=AccessTools.Method(typeof(BarrelRecipeMemoOptimizations),nameof(BarrelRecipeMemoOptimizations.Postfix)),Finalizer=AccessTools.Method(typeof(BarrelRecipeMemoOptimizations),nameof(BarrelRecipeMemoOptimizations.Finalizer))});
        BlockUpdateDiagnostics.Configure(false);BlockUpdateDiagnostics.SetThreshold(options.BlockUpdateDiagnosticsMs);
        entries.Add(new("blockUpdateDiagnostics",options.BlockUpdateDiagnostics,()=>BlockUpdateDiagnostics.Target,()=>BlockUpdateDiagnostics.Dependencies,AccessTools.Method(typeof(BlockUpdateDiagnostics),nameof(BlockUpdateDiagnostics.Prefix)),BlockUpdateDiagnostics.Configure){Finalizer=AccessTools.Method(typeof(BlockUpdateDiagnostics),nameof(BlockUpdateDiagnostics.Finalizer))});
        entries.Add(new("blockUpdateDiagnosticsSplit",options.BlockUpdateDiagnostics,()=>BlockUpdateDiagnostics.HandleTarget,()=>[BlockUpdateDiagnostics.HandleTarget],AccessTools.Method(typeof(BlockUpdateDiagnostics),nameof(BlockUpdateDiagnostics.HandlePrefix)),_=>{}){Finalizer=AccessTools.Method(typeof(BlockUpdateDiagnostics),nameof(BlockUpdateDiagnostics.HandleFinalizer))});
        ChunkUnloadStagingOptimizations.Configure(false);
        entries.Add(new("chunkUnloadStagingReuse", options.ChunkUnloadStagingReuse, () => ChunkUnloadStagingOptimizations.Target, () => ChunkUnloadStagingOptimizations.Dependencies, AccessTools.Method(typeof(ChunkUnloadStagingOptimizations), nameof(ChunkUnloadStagingOptimizations.Prefix)), ChunkUnloadStagingOptimizations.Configure)
        { Transpiler = AccessTools.Method(typeof(ChunkUnloadStagingOptimizations), nameof(ChunkUnloadStagingOptimizations.Transpiler)), Finalizer = AccessTools.Method(typeof(ChunkUnloadStagingOptimizations), nameof(ChunkUnloadStagingOptimizations.Finalizer)) });
        NativeBuildLogOptimizations.Configure(false);
        entries.Add(new("nativeBuildLogGuard",options.NativeBuildLogGuard,()=>NativeBuildLogOptimizations.Target,()=>NativeBuildLogOptimizations.Dependencies,AccessTools.Method(typeof(NativeBuildLogOptimizations),nameof(NativeBuildLogOptimizations.Prefix)),NativeBuildLogOptimizations.Configure){Transpiler=AccessTools.Method(typeof(NativeBuildLogOptimizations),nameof(NativeBuildLogOptimizations.Transpiler)),Finalizer=AccessTools.Method(typeof(NativeBuildLogOptimizations),nameof(NativeBuildLogOptimizations.Finalizer))});
        BroadcastOptimizations.ConfigureRaw(false);BroadcastOptimizations.ConfigurePacket(false);BroadcastOptimizations.ConfigureUdp(false);
        EntityLifecycleOptimizations.ConfigureTicks(false);EntityLifecycleOptimizations.ConfigureDespawns(false);
        entries.Add(new("broadcastRawFilter",options.BroadcastFilterReuse,()=>BroadcastOptimizations.RawTarget,()=>BroadcastOptimizations.Dependencies(0),AccessTools.Method(typeof(BroadcastOptimizations),nameof(BroadcastOptimizations.RawPrefix)),BroadcastOptimizations.ConfigureRaw));
        entries.Add(new("broadcastPacketFilter",options.BroadcastFilterReuse,()=>BroadcastOptimizations.PacketTarget,()=>BroadcastOptimizations.Dependencies(1),AccessTools.Method(typeof(BroadcastOptimizations),nameof(BroadcastOptimizations.PacketPrefix)),BroadcastOptimizations.ConfigurePacket));
        entries.Add(new("broadcastUdpFilter",options.BroadcastFilterReuse,()=>BroadcastOptimizations.UdpTarget,()=>BroadcastOptimizations.Dependencies(2),AccessTools.Method(typeof(BroadcastOptimizations),nameof(BroadcastOptimizations.UdpPrefix)),BroadcastOptimizations.ConfigureUdp));
        entries.Add(new("entityTickCleanupLists",options.EntityLifecycleListReuse,()=>EntityLifecycleOptimizations.TickTarget,()=>EntityLifecycleOptimizations.Dependencies(0),AccessTools.Method(typeof(EntityLifecycleOptimizations),nameof(EntityLifecycleOptimizations.TickPrefix)),EntityLifecycleOptimizations.ConfigureTicks)
            {Transpiler=AccessTools.Method(typeof(EntityLifecycleOptimizations),nameof(EntityLifecycleOptimizations.Transpiler)),Finalizer=AccessTools.Method(typeof(EntityLifecycleOptimizations),nameof(EntityLifecycleOptimizations.Finalizer))});
        entries.Add(new("entityDespawnLists",options.EntityLifecycleListReuse,()=>EntityLifecycleOptimizations.DespawnTarget,()=>EntityLifecycleOptimizations.Dependencies(1),AccessTools.Method(typeof(EntityLifecycleOptimizations),nameof(EntityLifecycleOptimizations.DespawnPrefix)),EntityLifecycleOptimizations.ConfigureDespawns)
            {Transpiler=AccessTools.Method(typeof(EntityLifecycleOptimizations),nameof(EntityLifecycleOptimizations.Transpiler)),Finalizer=AccessTools.Method(typeof(EntityLifecycleOptimizations),nameof(EntityLifecycleOptimizations.Finalizer))});

        CollisionGenerationOptimizations.Configure(0,false);CollisionGenerationOptimizations.Configure(1,false);AnimationScanOptimizations.Configure(false);
        entries.Add(new("collisionGenerationReuse",options.CollisionGenerationReuse,()=>CollisionGenerationOptimizations.Target(0),()=>CollisionGenerationOptimizations.Dependencies(0),AccessTools.Method(typeof(CollisionGenerationOptimizations),nameof(CollisionGenerationOptimizations.NormalPrefix)),v=>CollisionGenerationOptimizations.Configure(0,v)));
        entries.Add(new("cachedCollisionGenerationReuse",options.CachedCollisionGenerationReuse,()=>CollisionGenerationOptimizations.Target(1),()=>CollisionGenerationOptimizations.Dependencies(1),AccessTools.Method(typeof(CollisionGenerationOptimizations),nameof(CollisionGenerationOptimizations.CachedPrefix)),v=>CollisionGenerationOptimizations.Configure(1,v)));
        entries.Add(new("animationCollisionFlagScan",options.AnimationCollisionFlagScan,()=>AnimationScanOptimizations.Target,()=>AnimationScanOptimizations.Dependencies,AccessTools.Method(typeof(AnimationScanOptimizations),nameof(AnimationScanOptimizations.Prefix)),AnimationScanOptimizations.Configure){Transpiler=AccessTools.Method(typeof(AnimationScanOptimizations),nameof(AnimationScanOptimizations.Transpiler)),Finalizer=AccessTools.Method(typeof(AnimationScanOptimizations),nameof(AnimationScanOptimizations.Finalizer))});

        string[] traversalNames = ["collisionTraversalReuse", "containerTransitionScan", "containerTickTraversal"];
        bool[] traversalFlags = [options.CollisionTraversalReuse, options.ContainerTransitionScan, options.ContainerTickTraversal];
        string[] traversalPrefixes = [nameof(TraversalOptimizations.CollisionPrefix), nameof(TraversalOptimizations.ScanPrefix), nameof(TraversalOptimizations.TickPrefix)];
        string[] traversalFinalizers = [nameof(TraversalOptimizations.CollisionFinalizer), nameof(TraversalOptimizations.ScanFinalizer), nameof(TraversalOptimizations.TickFinalizer)];
        for (int i = 0; i < 3; i++)
        {
            int kind = i; TraversalOptimizations.Configure(kind, false);
            entries.Add(new(traversalNames[i], traversalFlags[i], () => TraversalOptimizations.Target(kind), () => TraversalOptimizations.Dependencies(kind),
                AccessTools.Method(typeof(TraversalOptimizations), traversalPrefixes[i]), value => TraversalOptimizations.Configure(kind, value))
                { Transpiler = AccessTools.Method(typeof(TraversalOptimizations), nameof(TraversalOptimizations.Transpiler)), Finalizer = AccessTools.Method(typeof(TraversalOptimizations), traversalFinalizers[i]) });
        }

        string[] queryNames = ["nearbyEntityListReuse", "cuboidEntityListReuse"];
        string[] recipeNames = ["shapelessStackMerge", "shapelessWildcardMatch"];
        bool[] queryFlags = [options.NearbyEntityListReuse, options.CuboidEntityListReuse];
        bool[] recipeFlags = [options.ShapelessStackMerge, options.ShapelessWildcardMatch];
        for (int i = 0; i < 2; i++)
        {
            int kind = i;
            EntityQueryScratchOptimizations.Configure(kind, false); RecipeOptimizations.Configure(kind, false);
            entries.Add(new(queryNames[i], queryFlags[i], () => EntityQueryScratchOptimizations.Target(kind), () => EntityQueryScratchOptimizations.Dependencies(kind),
                AccessTools.Method(typeof(EntityQueryScratchOptimizations), kind == 0 ? nameof(EntityQueryScratchOptimizations.AroundPrefix) : nameof(EntityQueryScratchOptimizations.CuboidPrefix)), value => EntityQueryScratchOptimizations.Configure(kind, value))
                { Transpiler = AccessTools.Method(typeof(EntityQueryScratchOptimizations), nameof(EntityQueryScratchOptimizations.Transpiler)), Finalizer = AccessTools.Method(typeof(EntityQueryScratchOptimizations), nameof(EntityQueryScratchOptimizations.Finalizer)) });
            entries.Add(new(recipeNames[i], recipeFlags[i], () => RecipeOptimizations.Target(kind), () => RecipeOptimizations.Dependencies(kind),
                AccessTools.Method(typeof(RecipeOptimizations), kind == 0 ? nameof(RecipeOptimizations.MergePrefix) : nameof(RecipeOptimizations.WildcardPrefix)), value => RecipeOptimizations.Configure(kind, value))
                { Finalizer = AccessTools.Method(typeof(RecipeOptimizations), kind == 0 ? nameof(RecipeOptimizations.MergeFinalizer) : nameof(RecipeOptimizations.WildcardFinalizer)) });
        }

        string[] workNames = ["callbackSnapshotReuse", "callbackDebugSnapshotReuse", "spawnOpportunityListReuse", "spawnColumnArrayReuse"];
        bool[] workSwitches = [options.DelayedCallbackSnapshotReuse, options.DelayedCallbackSnapshotReuse, options.SpawnOpportunityListReuse, options.SpawnColumnArrayReuse];
        string[] workPrefixes = [nameof(CallbackSpawnOptimizations.TickPrefix), nameof(CallbackSpawnOptimizations.DebugPrefix), nameof(CallbackSpawnOptimizations.SpawnPrefix), nameof(CallbackSpawnOptimizations.ColumnPrefix)];
        for (int i = 0; i < workNames.Length; i++)
        {
            int kind = i; CallbackSpawnOptimizations.Configure(kind, false);
            entries.Add(new(workNames[i], workSwitches[i], () => CallbackSpawnOptimizations.Target(kind), () => CallbackSpawnOptimizations.Dependencies(kind),
                AccessTools.Method(typeof(CallbackSpawnOptimizations), workPrefixes[i]), value => CallbackSpawnOptimizations.Configure(kind, value))
                { Transpiler = AccessTools.Method(typeof(CallbackSpawnOptimizations), nameof(CallbackSpawnOptimizations.Transpiler)), Finalizer = AccessTools.Method(typeof(CallbackSpawnOptimizations), nameof(CallbackSpawnOptimizations.Finalizer)) });
        }

        string[] scratchNames = ["blockUpdateListReuse", "chunkLoadRequestListReuse", "chunkUnloadCandidateListReuse", "chunkUnloadNotificationReuse"];
        bool[] scratchSwitches = [options.BlockUpdateListReuse, options.ChunkLoadRequestListReuse, options.ChunkUnloadListReuse, options.ChunkUnloadListReuse];
        string[] scratchPrefixes = [nameof(BlockChunkScratchOptimizations.BlockPrefix), nameof(BlockChunkScratchOptimizations.LoadPrefix), nameof(BlockChunkScratchOptimizations.CandidatesPrefix), nameof(BlockChunkScratchOptimizations.NotifyPrefix)];
        for (int i = 0; i < scratchNames.Length; i++)
        {
            int kind = i;
            BlockChunkScratchOptimizations.Configure(kind, false);
            entries.Add(new(scratchNames[i], scratchSwitches[i], () => BlockChunkScratchOptimizations.Target(kind), () => BlockChunkScratchOptimizations.Dependencies(kind),
                AccessTools.Method(typeof(BlockChunkScratchOptimizations), scratchPrefixes[i]), value => BlockChunkScratchOptimizations.Configure(kind, value))
                { Transpiler = AccessTools.Method(typeof(BlockChunkScratchOptimizations), nameof(BlockChunkScratchOptimizations.Transpiler)), Finalizer = AccessTools.Method(typeof(BlockChunkScratchOptimizations), nameof(BlockChunkScratchOptimizations.Finalizer)) });
        }

        PathNodePoolOptimizations.Configure(false);PathNodePoolOptimizations.ConfigureScope(false);
        entries.Add(new("waypointNodePoolScope",options.WaypointNodePooling,()=>PathNodePoolOptimizations.ScopeTarget,()=>PathNodePoolOptimizations.Dependencies,
            AccessTools.Method(typeof(PathNodePoolOptimizations),nameof(PathNodePoolOptimizations.Prefix)),PathNodePoolOptimizations.ConfigureScope)
            {Finalizer=AccessTools.Method(typeof(PathNodePoolOptimizations),nameof(PathNodePoolOptimizations.Finalizer))});
        entries.Add(new("waypointNodePooling",options.WaypointNodePooling,()=>PathNodePoolOptimizations.Target,()=>PathNodePoolOptimizations.Dependencies,null,PathNodePoolOptimizations.Configure)
            {Transpiler=AccessTools.Method(typeof(PathNodePoolOptimizations),nameof(PathNodePoolOptimizations.Transpiler))});

        EntityDistanceOptimizations.ConfigureAi(false);EntityDistanceOptimizations.ConfigureWander(false);EntityDistanceOptimizations.ConfigureRepulsion(false);EntityDistanceOptimizations.ConfigureSafety(false);AttributeSyncOptimizations.Configure(false);
        EntityDistanceOptimizations.Settings(options.EntityProtectionRadius,options.AiDistanceIntervalMs,options.DistantWanderIntervalMs,options.RepulsionDistanceIntervalMs,options.EntityPolicyInclude,options.EntityPolicyExclude);
        entries.Add(new("entityHurtSafety",options.AiDistanceThrottle||options.DistantWanderThrottle||options.RepulsionDistanceThrottle,()=>EntityDistanceOptimizations.HurtTarget,
            ()=>[EntityDistanceOptimizations.HurtTarget],AccessTools.Method(typeof(EntityDistanceOptimizations),nameof(EntityDistanceOptimizations.HurtPrefix)),EntityDistanceOptimizations.ConfigureSafety));
        entries.Add(new("aiDistanceThrottle",options.AiDistanceThrottle,()=>EntityDistanceOptimizations.AiTarget,
            ()=>EntityDistanceOptimizations.Dependencies(EntityDistanceOptimizations.AiTarget),AccessTools.Method(typeof(EntityDistanceOptimizations),nameof(EntityDistanceOptimizations.AiPrefix)),EntityDistanceOptimizations.ConfigureAi));
        entries.Add(new("distantWanderThrottle",options.DistantWanderThrottle,()=>EntityDistanceOptimizations.WanderTarget,
            ()=>EntityDistanceOptimizations.Dependencies(EntityDistanceOptimizations.WanderTarget),AccessTools.Method(typeof(EntityDistanceOptimizations),nameof(EntityDistanceOptimizations.WanderPrefix)),EntityDistanceOptimizations.ConfigureWander));
        entries.Add(new("repulsionDistanceThrottle",options.RepulsionDistanceThrottle,()=>EntityDistanceOptimizations.RepulsionTarget,
            ()=>EntityDistanceOptimizations.Dependencies(EntityDistanceOptimizations.RepulsionTarget),AccessTools.Method(typeof(EntityDistanceOptimizations),nameof(EntityDistanceOptimizations.RepulsionPrefix)),EntityDistanceOptimizations.ConfigureRepulsion));
        entries.Add(new("attributeNoOpSkip",options.AttributeNoOpSkip,()=>AttributeSyncOptimizations.Target,
            ()=>AttributeSyncOptimizations.Dependencies,AccessTools.Method(typeof(AttributeSyncOptimizations),nameof(AttributeSyncOptimizations.Prefix)),AttributeSyncOptimizations.Configure));

        entries.Add(new("despawnPacketProjection", options.DespawnPacketProjection, () => DespawnPacketOptimizations.Target,
            () => DespawnPacketOptimizations.Dependencies, DespawnPacketOptimizations.PrefixMethod, DespawnPacketOptimizations.Configure));
        entries.Add(new("physicsPacketListReuse", options.PhysicsPacketListReuse, () => PhysicsPacketOptimizations.Target,
            () => [PhysicsPacketOptimizations.Target], PhysicsPacketOptimizations.PrefixMethod, PhysicsPacketOptimizations.Configure)
            { Transpiler = PhysicsPacketOptimizations.TranspilerMethod, Finalizer = PhysicsPacketOptimizations.FinalizerMethod });
        entries.Add(new("onlinePlayerSnapshots", options.OnlinePlayerSnapshots, () => PlayerQueryOptimizations.SnapshotTarget,
            () => [PlayerQueryOptimizations.SnapshotTarget], PlayerQueryOptimizations.SnapshotPrefixMethod, value => PlayerQueryOptimizations.SnapshotEnabled = value));
        entries.Add(new("nearbyPlayerQueries", options.NearbyPlayerQueries, () => PlayerQueryOptimizations.AroundTarget,
            () => [PlayerQueryOptimizations.AroundTarget], PlayerQueryOptimizations.AroundPrefixMethod, value => PlayerQueryOptimizations.AroundEnabled = value));
        entries.Add(new("bulkStreamReads", options.BulkStreamReads, () => StreamCopyOptimizations.ReadTarget,
            () => StreamCopyOptimizations.ReadConflictTargets, StreamCopyOptimizations.ReadPrefixMethod, StreamCopyOptimizations.ConfigureRead));
        entries.Add(new("assetCompressionCache", options.AssetCompressionCache, () => AssetCompressionOptimizations.Target,
            () => AssetCompressionOptimizations.ConflictTargets, AssetCompressionOptimizations.PrefixMethod, AssetCompressionOptimizations.Configure)
            { Postfix = AssetCompressionOptimizations.PostfixMethod });
        PathQueueOptimizations.SetBudget(options.PathMainThreadBudgetMs, options.PathMainThreadMaxSearches, options.PathMainThreadGraceMs);
        entries.Add(new("pathQueueLimitFix", options.PathQueueLimitFix, () => PathQueueOptimizations.WorkerTarget,
            () => PathQueueOptimizations.WorkerDependencies, null, PathQueueOptimizations.ConfigureLimit) { Transpiler = PathQueueOptimizations.WorkerTranspilerMethod });
        entries.Add(new("pathMainThreadBudget", options.PathMainThreadBudget, () => PathQueueOptimizations.MainTarget,
            () => PathQueueOptimizations.MainDependencies, PathQueueOptimizations.MainPrefixMethod, PathQueueOptimizations.ConfigureBudget));
        entries.Add(new("nearestEntitySearch", options.NearestEntitySearch, () => EntitySearchOptimizations.NearestTarget,
            () => EntitySearchOptimizations.NearestDependencies, EntitySearchOptimizations.NearestPrefixMethod, EntitySearchOptimizations.ConfigureNearest));
        entries.Add(new("interactableEntitySearch", options.InteractableEntitySearch, () => EntitySearchOptimizations.InteractableTarget,
            () => EntitySearchOptimizations.InteractableDependencies, EntitySearchOptimizations.InteractablePrefixMethod, EntitySearchOptimizations.ConfigureInteractable));
        entries.Add(new("aiTaskStartDispatch",options.AiTaskStartDispatch,()=>AiTaskStartOptimizations.Target,
            ()=>[AiTaskStartOptimizations.Target],null,AiTaskStartOptimizations.Configure) { Transpiler=AiTaskStartOptimizations.TranspilerMethod });
        entries.Add(new("aiFilterDispatch",options.AiFilterDispatch,()=>AiFilterOptimizations.Target,
            ()=>[AiFilterOptimizations.Target],null,AiFilterOptimizations.Configure) { Transpiler=AiFilterOptimizations.TranspilerMethod });
        DespawnCleanupTimings.Configure(false);
        entries.Add(new("despawnCleanupTimings",options.DespawnCleanupTimings,()=>DespawnCleanupTimings.Target,()=>DespawnCleanupTimings.Dependencies,null,DespawnCleanupTimings.Configure)
            {Transpiler=AccessTools.Method(typeof(DespawnCleanupTimings),nameof(DespawnCleanupTimings.Transpiler)),Finalizer=AccessTools.Method(typeof(DespawnCleanupTimings),nameof(DespawnCleanupTimings.Finalizer))});
        ListenerRegistrationOptimizations.Configure(0,false); ListenerRegistrationOptimizations.Configure(1,false);
        for(int i=0;i<2;i++)
        {
            int kind=i;
            entries.Add(new(kind==0?"entityListenerRegistrationIndex":"blockListenerRegistrationIndex",options.ListenerRegistrationIndex,
                ()=>ListenerRegistrationOptimizations.Target(kind),()=>ListenerRegistrationOptimizations.Dependencies(kind),
                AccessTools.Method(typeof(ListenerRegistrationOptimizations),kind==0?nameof(ListenerRegistrationOptimizations.EntityPrefix):nameof(ListenerRegistrationOptimizations.BlockPrefix)),v=>ListenerRegistrationOptimizations.Configure(kind,v))
                {Transpiler=AccessTools.Method(typeof(ListenerRegistrationOptimizations),nameof(ListenerRegistrationOptimizations.Transpiler)),Finalizer=AccessTools.Method(typeof(ListenerRegistrationOptimizations),nameof(ListenerRegistrationOptimizations.Finalizer))});
        }
        for (int i=0;i<2;i++)
        {
            int kind=i; EntityEventDispatchOptimizations.Configure(kind,false);
            entries.Add(new(kind==0 ? "entityLoadedDispatch" : "entityDespawnDispatch",kind==0 ? options.EntityLoadedDispatchReuse : options.EntityDespawnDispatchReuse,
                ()=>EntityEventDispatchOptimizations.Target(kind),()=>EntityEventDispatchOptimizations.Dependencies(kind),
                AccessTools.Method(typeof(EntityEventDispatchOptimizations),kind==0 ? nameof(EntityEventDispatchOptimizations.LoadedPrefix) : nameof(EntityEventDispatchOptimizations.DespawnPrefix)),
                v=>EntityEventDispatchOptimizations.Configure(kind,v))
                {Finalizer=AccessTools.Method(typeof(EntityEventDispatchOptimizations),kind==0 ? nameof(EntityEventDispatchOptimizations.LoadedFinalizer) : nameof(EntityEventDispatchOptimizations.DespawnFinalizer))});
        }
        TemperatureAnimationOptimizations.Configure(false);
        entries.Add(new("temperatureAnimationChecks",options.TemperatureAnimationChecks,()=>TemperatureAnimationOptimizations.Target,
            ()=>TemperatureAnimationOptimizations.Dependencies,AccessTools.Method(typeof(TemperatureAnimationOptimizations),nameof(TemperatureAnimationOptimizations.Prefix)),TemperatureAnimationOptimizations.Configure)
            {Transpiler=AccessTools.Method(typeof(TemperatureAnimationOptimizations),nameof(TemperatureAnimationOptimizations.Transpiler)),Finalizer=AccessTools.Method(typeof(TemperatureAnimationOptimizations),nameof(TemperatureAnimationOptimizations.Finalizer))});
        EmotionSearchOptimizations.Configure(false);
        entries.Add(new("emotionSearchReuse",options.EmotionSearchReuse,()=>EmotionSearchOptimizations.Target,
            ()=>EmotionSearchOptimizations.Dependencies,AccessTools.Method(typeof(EmotionSearchOptimizations),nameof(EmotionSearchOptimizations.Prefix)),EmotionSearchOptimizations.Configure)
            {Finalizer=AccessTools.Method(typeof(EmotionSearchOptimizations),nameof(EmotionSearchOptimizations.Finalizer))});
        entries.Add(new("repulseDelegateReuse",options.RepulseDelegateReuse,()=>RepulseDelegateOptimizations.Target,
            ()=>RepulseDelegateOptimizations.Dependencies,null,RepulseDelegateOptimizations.Configure) { Transpiler=RepulseDelegateOptimizations.TranspilerMethod });
        entries.Add(new("worldgenRequestReuse",options.WorldgenRequestReuse,()=>WorldgenRequestOptimizations.Target,
            ()=>WorldgenRequestOptimizations.Dependencies,null,WorldgenRequestOptimizations.Configure) { Transpiler=WorldgenRequestOptimizations.TranspilerMethod });
        entries.Add(new("saveSnapshotRelease",options.SaveSnapshotRelease,()=>SaveSnapshotOptimizations.Target,
            ()=>SaveSnapshotOptimizations.Dependencies,null,SaveSnapshotOptimizations.Configure) { Transpiler=SaveSnapshotOptimizations.TranspilerMethod });
        string? blocked = !config.Enabled ? "Optimizer master switch is disabled." : config.Compatibility.SafeMode ?
            "Compatibility.SafeMode is enabled; stock methods retained." : EngineGuard();
        foreach (var entry in entries)
        {
            if (!entry.Requested) { entry.State = "disabled"; entry.Reason = "Independent module switch is off."; continue; }
            if (blocked != null) { entry.State = "blocked"; entry.Reason = blocked; continue; }
            try
            {
                string why;
                bool contract = entry.Name=="despawnCleanupTimings" ? DespawnCleanupTimings.ValidateContract(out why) : entry.Name is "entityListenerRegistrationIndex" or "blockListenerRegistrationIndex" ? ListenerRegistrationOptimizations.ValidateContract(entry.Name=="entityListenerRegistrationIndex"?0:1,out why) : entry.Name is "entityLoadedDispatch" or "entityDespawnDispatch" ? EntityEventDispatchOptimizations.ValidateContract(entry.Name=="entityLoadedDispatch"?0:1,out why) : entry.Name=="craftingCandidateDedup" ? CraftingCandidateOptimizations.ValidateContract(out why) : entry.Name=="blockSimulationBudget" ? BlockSimulationBudget.ValidateContract(out why) : entry.Name=="barrelMatchCountGuard" ? BarrelCountOptimizations.ValidateContract(out why) : entry.Name=="barrelRecipeMemo" ? BarrelRecipeMemoOptimizations.ValidateContract(out why) : entry.Name is "blockUpdateDiagnostics" or "blockUpdateDiagnosticsSplit" ? BlockUpdateDiagnostics.ValidateContract(out why) : entry.Name=="idleFirepitFastPath" ? BlockTickOptimizations.ValidateContract(0,out why) : entry.Name=="barrelIngredientPairing" ? BlockTickOptimizations.ValidateContract(1,out why) : entry.Name=="wildBushCallbackFlatten" ? BlockTickOptimizations.ValidateContract(2,out why) : entry.Name=="chunkUnloadStagingReuse" ? ChunkUnloadStagingOptimizations.ValidateContract(out why) : entry.Name=="nativeBuildLogGuard" ? NativeBuildLogOptimizations.ValidateContract(out why) : entry.Name is "collisionGenerationReuse" or "cachedCollisionGenerationReuse" ? CollisionGenerationOptimizations.ValidateContract(entry.Name=="collisionGenerationReuse"?0:1,out why) : entry.Name=="animationCollisionFlagScan" ? AnimationScanOptimizations.ValidateContract(out why) : TraversalKind(entry.Name) is int traversalKind ? TraversalOptimizations.ValidateContract(traversalKind, out why) : RecipeKind(entry.Name) is int recipeKind ? RecipeOptimizations.ValidateContract(recipeKind, out why) : QueryKind(entry.Name) is int queryKind ? EntityQueryScratchOptimizations.ValidateContract(queryKind, out why) : WorkKind(entry.Name) is int workKind ? CallbackSpawnOptimizations.ValidateContract(workKind, out why) : ScratchKind(entry.Name) is int scratchKind ? BlockChunkScratchOptimizations.ValidateContract(scratchKind, out why) : entry.Name.StartsWith("broadcast",StringComparison.Ordinal) ? BroadcastOptimizations.ValidateContract(out why) : entry.Name is "entityTickCleanupLists" or "entityDespawnLists" ? EntityLifecycleOptimizations.ValidateContract(out why) : entry.Name is "waypointNodePooling" or "waypointNodePoolScope" ? PathNodePoolOptimizations.ValidateContract(out why) : entry.Name is "entityHurtSafety" or "aiDistanceThrottle" or "distantWanderThrottle" or "repulsionDistanceThrottle" ? EntityDistanceOptimizations.ValidateContract(out why) : entry.Name == "attributeNoOpSkip" ? AttributeSyncOptimizations.ValidateContract(out why) : entry.Name == "despawnPacketProjection" ? DespawnPacketOptimizations.ValidateContract(out why) : entry.Name == "physicsPacketListReuse" ? PhysicsPacketOptimizations.ValidateContract(out why) : entry.Name is "pathQueueLimitFix" or "pathMainThreadBudget" ? PathQueueOptimizations.ValidateContract(out why) : entry.Name is "nearestEntitySearch" or "interactableEntitySearch" ? EntitySearchOptimizations.ValidateContract(out why) : entry.Name == "aiTaskStartDispatch" ? AiTaskStartOptimizations.ValidateContract(out why) : entry.Name == "worldgenRequestReuse" ? WorldgenRequestOptimizations.ValidateContract(out why) :
                    entry.Name == "temperatureAnimationChecks" ? TemperatureAnimationOptimizations.ValidateContract(out why) : entry.Name == "emotionSearchReuse" ? EmotionSearchOptimizations.ValidateContract(out why) : entry.Name == "saveSnapshotRelease" ? SaveSnapshotOptimizations.ValidateContract(out why) : entry.Name == "aiFilterDispatch" ? AiFilterOptimizations.ValidateContract(out why) :
                    entry.Name == "repulseDelegateReuse" ? RepulseDelegateOptimizations.ValidateContract(out why) : entry.Name == "assetCompressionCache" ? AssetCompressionOptimizations.ValidateContract(out why) :
                    entry.Name == "bulkStreamReads" ? StreamCopyOptimizations.ValidateContract(out why) : PlayerQueryOptimizations.ValidateContract(out why);
                if (!contract) { entry.State = "unsupported"; entry.Reason = why; continue; }
                if (UsesSurvivalMod(entry) && SurvivalGuard.Value is string survival) { entry.State = "blocked"; entry.Reason = survival; continue; }
                if (entry.Name == "nearbyPlayerQueries" && this.clientCount() < minimumNearbyClients)
                { entry.State = "standby"; entry.Reason = "Original method remains unpatched below " + minimumNearbyClients + " connected clients."; continue; }
                Activate(entry);
            }
            catch (Exception error) { Disable(entry, "failed", "Installation failed: " + error.GetType().Name); }
        }
        // Late Harmony changes and module trip counters are inspected off the hot path.
        try { if (entries.Any(x => x.Installed || x.State == "standby")) listener = api.Event.RegisterGameTickListener(_ => Check(), 1000); }
        catch (Exception error)
        {
            foreach (var entry in entries.Where(x => x.Installed || x.State == "standby").ToArray())
                Disable(entry, "failed", "Compatibility monitor could not start: " + error.GetType().Name);
        }
        foreach (var entry in entries) api.Logger.Notification("[NewfiesOptimizer] {0}: {1} — {2}", entry.Name, entry.State, entry.Reason);
    }
    private void Activate(Entry entry)
    {
        string? conflict = Conflict(entry);
        if (conflict != null) { entry.State = "conflict"; entry.Reason = conflict; return; }
        patchSnapshots.Remove(entry.TargetMethod);
        harmony.Patch(entry.TargetMethod, prefix: entry.Prefix == null ? null : new HarmonyMethod(entry.Prefix), transpiler: entry.Transpiler == null ? null : new HarmonyMethod(entry.Transpiler), postfix: entry.Postfix == null ? null : new HarmonyMethod(entry.Postfix), finalizer: entry.Finalizer == null ? null : new HarmonyMethod(entry.Finalizer));
        if(entry.Name == "assetCompressionCache") RefreshAssets();
        entry.FailureBaseline = Failures(entry.Name);
        entry.Installed = true; entry.Enable(true); entry.State = "active";
        entry.Reason = "Verified official 1.22.7; independently controlled runtime patch installed.";
    }
    internal static bool IsKnownRuntime(string name) =>
        name.Equals("Stratum", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("StratumServer", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("StratumServer.", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Tungsten", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Synergy", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Synergy.", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("VSSynergy", StringComparison.OrdinalIgnoreCase);
    private static string? EngineGuard()
    {
        if (EngineVersion.Actual != "1.22.7") return "Unsupported game version: " + EngineVersion.Actual;
        var found = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "")
            .FirstOrDefault(IsKnownRuntime);
        if (found != null) return "Alternative runtime/optimizer detected: " + found + ". Do not stack unvalidated runtime optimizers.";
        try
        {
            foreach (var pair in new[] {
                (typeof(FastMemoryStream).Assembly, "034283e7e9d98eae45ee63005576fd89badc3c995b531cc4c3fe46f3eb2d3296"),
                (typeof(ServerMain).Assembly, "e08f22b493b92feaf0aaeb79d22437ea0f7efc38aa7f72a04a47f98bc0e40df0"),
                (typeof(Vintagestory.GameContent.AiTaskManager).Assembly, "a28565c5c9181f8cc84b98a2b7457ab824b8ecb7763714448da1f7c245aecd6e") })
            {
                using var file = File.OpenRead(pair.Item1.Location);
                if (!Convert.ToHexString(SHA256.HashData(file)).Equals(pair.Item2, StringComparison.OrdinalIgnoreCase))
                    return "Engine assembly hash differs from the inspected official build: " + pair.Item1.GetName().Name;
            }
        }
        catch (Exception error) { return "Engine identity could not be verified: " + error.GetType().Name; }
        return null;
    }
    // Block entity, barrel and body-temperature modules patch VSSurvivalMod. Verify it only
    // for those modules, so a hotfixed survival assembly cannot disable the core modules.
    private const string SurvivalAssemblyName = "VSSurvivalMod";
    private static bool UsesSurvivalMod(Entry entry) =>
        entry.DependencyMethods.Append(entry.TargetMethod).Any(m => m?.DeclaringType?.Assembly.GetName().Name == SurvivalAssemblyName);
    private static readonly Lazy<string?> SurvivalGuard = new(() =>
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == SurvivalAssemblyName);
            using var file = File.OpenRead(assembly.Location);
            return Convert.ToHexString(SHA256.HashData(file)).Equals("d67b48a321403b2052b33c7d0caa99611f92350ffac73ea72672901dd87ccd7a", StringComparison.OrdinalIgnoreCase)
                ? null : "Engine assembly hash differs from the inspected official build: " + SurvivalAssemblyName;
        }
        catch (Exception error) { return "Engine identity could not be verified: " + error.GetType().Name; }
    });
    private static string? ForeignOwner(Patches? patches)
    {
        if (patches == null) return null;
        static string? Find(IList<Patch> list)
        {
            // Profiler timing observers only add a prefix/postfix pair; they never alter arguments or results.
            for (int i = 0; i < list.Count; i++) if (list[i].owner != HarmonyId && list[i].owner != ProfilerHarmonyId) return list[i].owner;
            return null;
        }
        return Find(patches.Prefixes) ?? Find(patches.Postfixes) ?? Find(patches.Transpilers) ?? Find(patches.Finalizers);
    }
    private static readonly Func<Patch,MethodInfo,bool> MatchesPatch = CreatePatchMatcher();
    private static Func<Patch,MethodInfo,bool> CreatePatchMatcher()
    {
        try
        {
            // GetPatchInfo reconstructs records. Resolving PatchMethod on each
            // record walks assemblies again; compare the stored identity instead.
            var token=AccessTools.FieldRefAccess<Patch,int>("methodToken");
            var module=AccessTools.FieldRefAccess<Patch,string>("moduleGUID");
            string ownModule=typeof(EngineOptimizations).Module.ModuleVersionId.ToString();
            return (patch,expected)=>token(patch)==expected.MetadataToken && module(patch)==ownModule;
        }
        catch { return (patch,expected)=>patch.PatchMethod==expected; }
    }
    private static bool HasOwnPatch(Entry entry, Patches? patches)
    {
        if (patches == null) return false;
        static bool Find(IList<Patch> list, MethodInfo? expected)
        {
            if(expected == null) return true;
            for(int i=0;i<list.Count;i++)
                if(list[i].owner==HarmonyId && MatchesPatch(list[i],expected)) return true;
            return false;
        }
        return Find(patches.Prefixes,entry.Prefix) && Find(patches.Postfixes,entry.Postfix) &&
            Find(patches.Transpilers,entry.Transpiler) && Find(patches.Finalizers,entry.Finalizer);
    }
    private string? Conflict(Entry entry, Patches? targetInfo = null, bool targetInspected = false)
    {
        foreach (var method in entry.DependencyMethods)
        {
            var patches = targetInspected && method == entry.TargetMethod ? targetInfo : Inspect(method);
            if (entry.Name == "barrelMatchCountGuard" && method == entry.TargetMethod &&
                BarrelPatchCompatibility.Allows(patches, HarmonyId)) continue;
            var owner = ForeignOwner(patches);
            if (owner != null) return "Foreign Harmony owner on " + method.Name + ": " + owner;
        }
        return null;
    }
    private Patches? Inspect(MethodBase method)
    {
        if (!checking) return Harmony.GetPatchInfo(method);
        if (patchSnapshots.TryGetValue(method, out var snapshot)) return snapshot;
        snapshot = Harmony.GetPatchInfo(method); patchSnapshots.Add(method, snapshot); return snapshot;
    }
    private FieldInfo? assetsPacketField, assetsBufferField;
    private void RefreshAssets()
    {
        var world = api.World as ServerMain;
        var packet = world == null ? null : (assetsPacketField ??= AccessTools.Field(typeof(ServerMain), "serverAssetsPacket"))?.GetValue(world);
        var buffer = packet == null ? null : (assetsBufferField ??= AccessTools.Field(packet.GetType(), "buffer"))?.GetValue(packet) as byte[];
        AssetCompressionOptimizations.Track(buffer, assetCacheBytes);
    }
    private void Check()
    {
        lock (gate)
        {
            if (disposed || checking) return;
            checking = true;
            try
            {
                foreach (var entry in entries)
                {
                    if (!entry.Installed && entry.State != "standby") continue;
                    try
                    {
                        if (entry.Name == "nearbyPlayerQueries")
                        {
                            int count = clientCount();
                            if (!entry.Installed)
                            {
                                if (count >= minimumNearbyClients) Activate(entry);
                                continue;
                            }
                            // Hysteresis avoids patch churn when clients fluctuate around activation.
                            if (count < Math.Max(0, minimumNearbyClients - 32))
                            { Disable(entry, "standby", "Population below active threshold; original method unpatched."); continue; }
                        }
                        var targetInfo = entry.Installed ? Inspect(entry.TargetMethod) : null;
                        if (entry.Installed && !HasOwnPatch(entry,targetInfo))
                        { Disable(entry, "failed", "Own runtime patch was removed; stock behavior retained. Reapply settings to retry."); continue; }
                        if(entry.Name == "assetCompressionCache") RefreshAssets();
                        string? conflict = Conflict(entry, targetInfo, entry.Installed);
                        if (conflict != null) { Disable(entry, "conflict", conflict); continue; }
                        if (Failures(entry.Name) > entry.FailureBaseline)
                            Disable(entry, "failed", entry.Name=="blockSimulationBudget" && BlockSimulationBudget.PressureFallback ? "Queued block work exceeded 8,192 entries. Budget released; stock processing retained without dropping callbacks. Increase the budget or leave this policy disabled before reapplying." : "A new failure was observed during this activation. Stock behavior retained; reapply settings to retry.");
                    }
                    catch (Exception error) { Disable(entry, "failed", "Compatibility monitor failed: " + error.GetType().Name); }
                }
            }
            finally { checking = false; patchSnapshots.Clear(); }
        }
    }
    private static int? TraversalKind(string name) => name switch { "collisionTraversalReuse" => 0, "containerTransitionScan" => 1, "containerTickTraversal" => 2, _ => null };
    private static int? RecipeKind(string name) => name switch { "shapelessStackMerge" => 0, "shapelessWildcardMatch" => 1, _ => null };
    private static int? QueryKind(string name) => name switch { "nearbyEntityListReuse" => 0, "cuboidEntityListReuse" => 1, _ => null };
    private static int? WorkKind(string name) => name switch { "callbackSnapshotReuse" => 0, "callbackDebugSnapshotReuse" => 1, "spawnOpportunityListReuse" => 2, "spawnColumnArrayReuse" => 3, _ => null };
    private static int? ScratchKind(string name) => name switch { "blockUpdateListReuse" => 0, "chunkLoadRequestListReuse" => 1, "chunkUnloadCandidateListReuse" => 2, "chunkUnloadNotificationReuse" => 3, _ => null };
    private static long Failures(string name)
    {
        if(name=="idleFirepitFastPath")return BlockTickOptimizations.Counters(0).Failures;
        if(name=="barrelIngredientPairing")return BlockTickOptimizations.Counters(1).Failures;
        if(name=="wildBushCallbackFlatten")return BlockTickOptimizations.Counters(2).Failures;
        if(name=="chunkUnloadStagingReuse")return ChunkUnloadStagingOptimizations.Counters.Failures;
        if(name=="nativeBuildLogGuard")return NativeBuildLogOptimizations.Counters.Failures;
        if(name is "collisionGenerationReuse" or "cachedCollisionGenerationReuse")return CollisionGenerationOptimizations.Counters(name=="collisionGenerationReuse"?0:1).Failures;
        if(name=="craftingCandidateDedup")return CraftingCandidateOptimizations.Counters.Failures;
        if(name=="blockSimulationBudget")return BlockSimulationBudget.Counters.Failures;
        if(name=="barrelMatchCountGuard")return BarrelCountOptimizations.Counters.Failures;
        if(name=="barrelRecipeMemo")return BarrelRecipeMemoOptimizations.Counters.Failures;
        if(name is "blockUpdateDiagnostics" or "blockUpdateDiagnosticsSplit")return 0;
        if(name=="animationCollisionFlagScan")return AnimationScanOptimizations.Counters.Failures;
        if (TraversalKind(name) is int traversalKind) return TraversalOptimizations.Counters(traversalKind).Failures;
        if (RecipeKind(name) is int recipeKind) return RecipeOptimizations.Counters(recipeKind).Failures;
        if (QueryKind(name) is int queryKind) return EntityQueryScratchOptimizations.Counters(queryKind).Failures;
        if (WorkKind(name) is int workKind) return CallbackSpawnOptimizations.Counters(workKind).Failures;
        if (ScratchKind(name) is int kind) return BlockChunkScratchOptimizations.Counters(kind).Failures;
        if(name=="despawnCleanupTimings")return DespawnCleanupTimings.Counters.Failures;
        if(name is "entityListenerRegistrationIndex" or "blockListenerRegistrationIndex")return ListenerRegistrationOptimizations.Failures(name=="entityListenerRegistrationIndex"?0:1);
        if(name is "entityLoadedDispatch" or "entityDespawnDispatch")return EntityEventDispatchOptimizations.Counters(name=="entityLoadedDispatch"?0:1).Failures;
        if(name=="temperatureAnimationChecks")return TemperatureAnimationOptimizations.Counters.Failures;
        if(name=="emotionSearchReuse")return EmotionSearchOptimizations.Counters.Failures;
        return name switch { "broadcastRawFilter" or "broadcastPacketFilter" or "broadcastUdpFilter"=>0, "entityTickCleanupLists" or "entityDespawnLists"=>EntityLifecycleOptimizations.Counters.Failures, "waypointNodePooling" or "waypointNodePoolScope" => PathNodePoolOptimizations.Failures, "entityHurtSafety" or "aiDistanceThrottle" or "distantWanderThrottle" or "repulsionDistanceThrottle" => EntityDistanceOptimizations.Counters.Failures, "attributeNoOpSkip" => 0, "pathMainThreadBudget" => PathQueueOptimizations.MainFailures, "despawnPacketProjection" or "physicsPacketListReuse" or "pathQueueLimitFix" or "nearestEntitySearch" or "interactableEntitySearch" or "aiTaskStartDispatch" or "worldgenRequestReuse" or "saveSnapshotRelease" or "aiFilterDispatch" or "repulseDelegateReuse" => 0, "onlinePlayerSnapshots" => PlayerQueryOptimizations.GetCounters().SnapshotFailures,
            "nearbyPlayerQueries" => PlayerQueryOptimizations.GetCounters().AroundFailures,
            "assetCompressionCache" => AssetCompressionOptimizations.Counters.Failures,
            _ => StreamCopyOptimizations.Snapshot.Failures };
    }
    private void Disable(Entry entry, string state, string reason)
    {
        entry.Enable(false);
        // Harmony's Unpatch rebuilds and detours the original even when it holds
        // no patches. Never touch the target of an entry that was not installed.
        if (entry.Installed) try
        {
            patchSnapshots.Remove(entry.TargetMethod);
            // Several independent modules can share one engine method. Remove only
            // this entry's patch methods, preserving other NOS and foreign patches.
            foreach(var method in new[]{entry.Prefix,entry.Postfix,entry.Transpiler,entry.Finalizer})
                if(method!=null) harmony.Unpatch(entry.TargetMethod,method);
        }
        catch (Exception error) { reason += "; own patch removal failed: " + error.GetType().Name + ". Prefix is disabled."; }
        entry.Installed = false; entry.State = state; entry.Reason = reason;
    }
    public ModuleStatus[] Modules() { lock (gate) return entries.Select(x => new ModuleStatus(x.Name, x.State, x.Reason)).ToArray(); }
    public IReadOnlyDictionary<string,long> Counters()
    {
        var q = PlayerQueryOptimizations.GetCounters(); var s = StreamCopyOptimizations.Snapshot;
        var assets = AssetCompressionOptimizations.Counters;
        var saves = SaveSnapshotOptimizations.Counters;
        var entitySearch = EntitySearchOptimizations.Counters;
        var paths = PathQueueOptimizations.Counters;
        var physics = PhysicsPacketOptimizations.Counters;
        var despawn = DespawnPacketOptimizations.Counters;
        var distance=EntityDistanceOptimizations.Counters; var nodes=PathNodePoolOptimizations.Counters;
        var broadcasts=BroadcastOptimizations.Counters;var lifecycle=EntityLifecycleOptimizations.Counters;
        var blockScratch = BlockChunkScratchOptimizations.Counters(0); var loadScratch = BlockChunkScratchOptimizations.Counters(1);
        var candidateScratch = BlockChunkScratchOptimizations.Counters(2); var notifyScratch = BlockChunkScratchOptimizations.Counters(3);
        var callback = CallbackSpawnOptimizations.Counters(0); var callbackDebug = CallbackSpawnOptimizations.Counters(1);
        var spawn = CallbackSpawnOptimizations.Counters(2); var column = CallbackSpawnOptimizations.Counters(3);
        var around = EntityQueryScratchOptimizations.Counters(0); var cuboid = EntityQueryScratchOptimizations.Counters(1);
        var merge = RecipeOptimizations.Counters(0); var wildcard = RecipeOptimizations.Counters(1);
        var collision = TraversalOptimizations.Counters(0); var scan = TraversalOptimizations.Counters(1); var tick = TraversalOptimizations.Counters(2);
        var generation=CollisionGenerationOptimizations.Counters(0);var cachedGeneration=CollisionGenerationOptimizations.Counters(1);var animation=AnimationScanOptimizations.Counters;
        return new Dictionary<string,long> { ["despawnCleanupTimedCalls"]=DespawnCleanupTimings.Counters.Calls,["despawnCleanupObservedFailures"]=DespawnCleanupTimings.Counters.Failures, ["listenerRegistrationCalls"]=ListenerRegistrationOptimizations.Counters.Calls,["listenerRegistrationIndexRebuilds"]=ListenerRegistrationOptimizations.Counters.Rebuilds,["listenerRegistrationLeadingSlotsSkipped"]=ListenerRegistrationOptimizations.Counters.Avoided,["listenerRegistrationFallbacks"]=ListenerRegistrationOptimizations.Counters.Fallbacks,["listenerRegistrationFailures"]=ListenerRegistrationOptimizations.Counters.Failures, ["entityLoadedDispatchCalls"]=EntityEventDispatchOptimizations.Counters(0).Calls,["entityDespawnDispatchCalls"]=EntityEventDispatchOptimizations.Counters(1).Calls,["entityEventDispatchFailures"]=EntityEventDispatchOptimizations.Counters(0).Failures+EntityEventDispatchOptimizations.Counters(1).Failures, ["temperatureAnimationChecks"]=TemperatureAnimationOptimizations.Counters.Calls,["temperatureAnimationFallbacks"]=TemperatureAnimationOptimizations.Counters.Fallbacks,["temperatureAnimationFailures"]=TemperatureAnimationOptimizations.Counters.Failures, ["emotionSearchCalls"]=EmotionSearchOptimizations.Counters.Calls,["emotionSearchCallbacksCreated"]=EmotionSearchOptimizations.Counters.Created,["emotionSearchFailures"]=EmotionSearchOptimizations.Counters.Failures, ["recipeVariantFastPaths"]=RecipeOptimizations.VariantFastPaths, ["craftingCandidateQueries"]=CraftingCandidateOptimizations.Counters.Queries,["craftingCandidateHashedQueries"]=CraftingCandidateOptimizations.Counters.Hashed,["craftingCandidateChecks"]=CraftingCandidateOptimizations.Counters.Checks,["craftingCandidateDuplicatesAvoided"]=CraftingCandidateOptimizations.Counters.Duplicates,["craftingCandidateCustomFallbacks"]=CraftingCandidateOptimizations.Counters.Fallbacks,["craftingCandidateContextsCreated"]=CraftingCandidateOptimizations.Counters.Created,["craftingCandidateOversizedReleased"]=CraftingCandidateOptimizations.Counters.Oversized,["craftingCandidateFailures"]=CraftingCandidateOptimizations.Counters.Failures, ["blockSimulationPasses"]=BlockSimulationBudget.Counters.Passes,["blockSimulationDeferredPasses"]=BlockSimulationBudget.Counters.Deferred,["blockSimulationFailures"]=BlockSimulationBudget.Counters.Failures,["blockSimulationQueueDepth"]=BlockSimulationBudget.Counters.Queue,["blockSimulationQueuePeak"]=BlockSimulationBudget.Counters.Peak, ["barrelMatchCountGuardCalls"]=BarrelCountOptimizations.Counters.Calls, ["barrelMatchCountGuardFallbacks"]=BarrelCountOptimizations.Counters.Fallbacks, ["barrelMatchCountGuardFailures"]=BarrelCountOptimizations.Counters.Failures, ["blockUpdatePassesObserved"]=BlockUpdateDiagnostics.Counters.Passes,["blockUpdateSlowPasses"]=BlockUpdateDiagnostics.Counters.Slow,["barrelRecipeMemoChecks"]=BarrelRecipeMemoOptimizations.Counters.Checks,["barrelRecipeMemoSkipped"]=BarrelRecipeMemoOptimizations.Counters.Skipped,["barrelRecipeMemoScans"]=BarrelRecipeMemoOptimizations.Counters.Scans,["barrelRecipeMemoFallbacks"]=BarrelRecipeMemoOptimizations.Counters.Fallbacks,["barrelRecipeMemoFailures"]=BarrelRecipeMemoOptimizations.Counters.Failures, ["idleFirepitFastPathCalls"] = BlockTickOptimizations.Counters(0).Calls, ["idleFirepitFastPathFallbacks"] = BlockTickOptimizations.Counters(0).Fallbacks, ["idleFirepitFastPathFailures"] = BlockTickOptimizations.Counters(0).Failures,
            ["barrelIngredientPairingCalls"] = BlockTickOptimizations.Counters(1).Calls, ["barrelIngredientPairingFallbacks"] = BlockTickOptimizations.Counters(1).Fallbacks, ["barrelIngredientPairingFailures"] = BlockTickOptimizations.Counters(1).Failures,
            ["wildBushCallbackFlattenCalls"] = BlockTickOptimizations.Counters(2).Calls, ["wildBushCallbackFlattenFallbacks"] = BlockTickOptimizations.Counters(2).Fallbacks, ["wildBushCallbackFlattenFailures"] = BlockTickOptimizations.Counters(2).Failures,
            ["chunkUnloadStagingCalls"] = ChunkUnloadStagingOptimizations.Counters.Calls, ["chunkUnloadStagingBuffersCreated"] = ChunkUnloadStagingOptimizations.Counters.Created, ["chunkUnloadStagingOversizedReleased"] = ChunkUnloadStagingOptimizations.Counters.Released, ["chunkUnloadStagingFailures"] = ChunkUnloadStagingOptimizations.Counters.Failures,
            ["nativeBuildLogMissingCodes"]=NativeBuildLogOptimizations.Counters.MissingCodes,["nativeBuildLogSystemPlayers"]=NativeBuildLogOptimizations.Counters.SystemPlayers,["nativeBuildLogFailures"]=NativeBuildLogOptimizations.Counters.Failures, ["collisionGenerationCalls"]=generation.Calls,["cachedCollisionGenerationCalls"]=cachedGeneration.Calls,["collisionGenerationCacheHits"]=generation.Hits+cachedGeneration.Hits,["collisionGenerationContextsCreated"]=generation.Created+cachedGeneration.Created,["collisionGenerationFallbacks"]=generation.Fallbacks+cachedGeneration.Fallbacks,["collisionGenerationFailures"]=generation.Failures+cachedGeneration.Failures,["animationCollisionScans"]=animation.Calls,["animationScanFallbacks"]=animation.Fallbacks,["animationScanFailures"]=animation.Failures, ["collisionTraversalCalls"] = collision.Calls, ["containerTransitionScans"] = scan.Calls, ["containerTickTraversals"] = tick.Calls,
            ["traversalIteratorsAvoided"] = collision.Calls + scan.Calls + tick.Calls,
            ["customInventoryTraversalFallbacks"] = scan.Fallbacks + tick.Fallbacks, ["collisionTraversalFailures"] = collision.Failures, ["containerScanFailures"] = scan.Failures, ["containerTickFailures"] = tick.Failures, ["nearbyEntityListPasses"] = around.Calls, ["cuboidEntityListPasses"] = cuboid.Calls,
            ["entityQueryBuffersCreated"] = around.Created + cuboid.Created, ["entityQueryOversizedReleased"] = around.Oversized + cuboid.Oversized,
            ["entityQueryFailures"] = around.Failures + cuboid.Failures, ["shapelessMergeCalls"] = merge.Calls, ["shapelessWildcardCalls"] = wildcard.Calls,
            ["shapelessMergeFailures"] = merge.Failures, ["shapelessWildcardFailures"] = wildcard.Failures, ["callbackSnapshotPasses"] = callback.Calls, ["callbackDebugSnapshotPasses"] = callbackDebug.Calls,
            ["callbackPositionsCopied"] = callback.Items + callbackDebug.Items, ["spawnOpportunityPasses"] = spawn.Calls,
            ["spawnColumnPasses"] = column.Calls, ["spawnColumnArraysCreated"] = column.ArraysCreated, ["spawnColumnArraysReused"] = column.ArraysReused,
            ["callbackSpawnBuffersCreated"] = callback.Created + callbackDebug.Created + spawn.Created + column.Created,
            ["callbackSpawnOversizedReleased"] = callback.Released + callbackDebug.Released + spawn.Released + column.Released,
            ["callbackSpawnFailures"] = callback.Failures + callbackDebug.Failures + spawn.Failures + column.Failures, ["blockUpdatePasses"] = blockScratch.Calls, ["chunkRequestPasses"] = loadScratch.Calls,
            ["chunkUnloadCandidatePasses"] = candidateScratch.Calls, ["chunkUnloadNotificationPasses"] = notifyScratch.Calls,
            ["chunkRequestCoordinatesReused"] = loadScratch.Coordinates, ["chunkUnloadCoordinatesReused"] = notifyScratch.Coordinates,
            ["blockChunkBuffersCreated"] = blockScratch.Created + loadScratch.Created + candidateScratch.Created + notifyScratch.Created,
            ["blockChunkOversizedReleased"] = blockScratch.Released + loadScratch.Released + candidateScratch.Released + notifyScratch.Released,
            ["blockChunkFailures"] = blockScratch.Failures + loadScratch.Failures + candidateScratch.Failures + notifyScratch.Failures, ["broadcastRawCalls"]=broadcasts.Raw,["broadcastPacketCalls"]=broadcasts.Packet,["broadcastUdpCalls"]=broadcasts.Udp,["entityCleanupPasses"]=lifecycle.Calls,["entityCleanupBuffersCreated"]=lifecycle.Created,["entityCleanupOversizedReleased"]=lifecycle.Released, ["waypointNodesReused"] = nodes.Reused, ["waypointNodesCreated"] = nodes.Created, ["waypointPoolOverflow"] = nodes.Overflow, ["aiChecksDeferred"] = distance.Ai, ["wanderChecksDeferred"] = distance.Wander, ["repulsionChecksDeferred"] = distance.Repulsion, ["entityPolicyFailures"] = distance.Failures, ["attributeNoOpSkips"] = AttributeSyncOptimizations.Skipped, ["despawnPacketCalls"] = despawn.Calls, ["despawnPacketEntities"] = despawn.Entities, ["physicsPacketPasses"] = physics.Calls, ["physicsPacketBuffersCreated"] = physics.Created, ["physicsPacketOversizedBuffersReleased"] = physics.Oversized, ["pathAssistanceFailures"] = PathQueueOptimizations.MainFailures, ["pathWorkerLimitStops"] = paths.WorkerLimitStops, ["pathMainSearches"] = paths.MainSearches, ["pathDeferredTicks"] = paths.DeferredTicks, ["nearestEntitySearchCalls"] = entitySearch.NearestCalls, ["interactableEntitySearchCalls"] = entitySearch.InteractableCalls, ["entitySearchContextsCreated"] = entitySearch.ContextsCreated, ["saveSnapshotReferencesReleased"] = saves.ReferencesReleased, ["saveSnapshotCompletions"] = saves.Completions, ["onlineSnapshotCalls"] = q.SnapshotCalls,
            ["nearbyQueryCalls"] = q.AroundCalls, ["snapshotFallbacks"] = q.SnapshotFallbacks,
            ["nearbyFallbacks"] = q.AroundFallbacks, ["snapshotFailures"] = q.SnapshotFailures,
            ["nearbyFailures"] = q.AroundFailures, ["assetCacheHits"] = assets.Hits, ["assetCacheMisses"] = assets.Misses,
            ["assetCacheBytes"] = assets.Bytes, ["assetCacheFailures"] = assets.Failures, ["bulkReadCalls"] = s.AcceleratedCalls,
            ["bulkReadBytes"] = s.CopiedBytes, ["bulkReadFallbacks"] = s.FallbackCalls, ["bulkReadFailures"] = s.Failures };
    }
    public string Status()
    {
        var queries = PlayerQueryOptimizations.GetCounters(); var stream = StreamCopyOptimizations.Snapshot;
        return string.Join("\n", Modules().Select(x => x.Name + ": " + x.Status + " — " + x.Reason)) +
            $"\nExecuted replacements: player snapshots={queries.SnapshotCalls}, nearby queries={queries.AroundCalls}, stream reads={stream.AcceleratedCalls} / {stream.CopiedBytes} bytes. Query fallbacks={queries.SnapshotFallbacks + queries.AroundFallbacks}; stream fallbacks={stream.FallbackCalls}.";
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            if (listener >= 0)
            {
                try { api.Event.UnregisterGameTickListener(listener); }
                catch (Exception error) { api.Logger.Warning("[NewfiesOptimizer] Monitor unregister failed ({0}); disposed callback is inert. Removing patches anyway.", error.GetType().Name); }
                listener = -1;
            }
            foreach (var entry in entries) Disable(entry, "stopped", "Own patch removed during disposal.");
            PlayerQueryOptimizations.ClearCurrentThreadScratch();
        }
    }
}
