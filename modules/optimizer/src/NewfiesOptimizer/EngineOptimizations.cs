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
    public const string HarmonyId = "newfies.serveroptimizer.engine.0.8.0";
    private readonly ICoreServerAPI api;
    private readonly Harmony harmony = new(HarmonyId);
    private readonly List<Entry> entries = new();
    private readonly object gate = new();
    private long listener = -1;
    private bool disposed;
    private readonly int minimumNearbyClients;
    private readonly int assetCacheBytes;
    private readonly Func<int> clientCount;
    private sealed class Entry(string name, bool requested, Func<MethodInfo> target, Func<MethodInfo[]> dependencies, MethodInfo? prefix, Action<bool> enable)
    {
        public string Name = name, State = "pending", Reason = "Not installed";
        public bool Requested = requested;
        public Func<MethodInfo> Target = target;
        public Func<MethodInfo[]> Dependencies = dependencies;
        public MethodInfo? Prefix = prefix;
        public Action<bool> Enable = enable;
        public bool Installed;
        public long FailureBaseline;
        public MethodInfo? Postfix;
        public MethodInfo? Finalizer;
        public MethodInfo? Transpiler;
        private MethodInfo? resolvedTarget;
        private MethodInfo[]? resolvedDependencies;
        public MethodInfo TargetMethod => resolvedTarget ??= Target();
        public MethodInfo[] DependencyMethods => resolvedDependencies ??= Dependencies();
    }
    public EngineOptimizations(ICoreServerAPI api, OptimizerConfig config, Func<int>? clientCount = null)
    {
        this.api = api;
        minimumNearbyClients = config.EngineOptimizations.MinimumNearbyClients;
        assetCacheBytes = config.EngineOptimizations.AssetCacheMaxMiB * 1048576;
        AssetCompressionOptimizations.Configure(false);
        PhysicsPacketOptimizations.Configure(false);
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
                bool contract = entry.Name == "physicsPacketListReuse" ? PhysicsPacketOptimizations.ValidateContract(out why) : entry.Name is "pathQueueLimitFix" or "pathMainThreadBudget" ? PathQueueOptimizations.ValidateContract(out why) : entry.Name is "nearestEntitySearch" or "interactableEntitySearch" ? EntitySearchOptimizations.ValidateContract(out why) : entry.Name == "aiTaskStartDispatch" ? AiTaskStartOptimizations.ValidateContract(out why) : entry.Name == "worldgenRequestReuse" ? WorldgenRequestOptimizations.ValidateContract(out why) :
                    entry.Name == "saveSnapshotRelease" ? SaveSnapshotOptimizations.ValidateContract(out why) : entry.Name == "aiFilterDispatch" ? AiFilterOptimizations.ValidateContract(out why) :
                    entry.Name == "repulseDelegateReuse" ? RepulseDelegateOptimizations.ValidateContract(out why) : entry.Name == "assetCompressionCache" ? AssetCompressionOptimizations.ValidateContract(out why) :
                    entry.Name == "bulkStreamReads" ? StreamCopyOptimizations.ValidateContract(out why) : PlayerQueryOptimizations.ValidateContract(out why);
                if (!contract) { entry.State = "unsupported"; entry.Reason = why; continue; }
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
    private static string? Conflict(Entry entry)
    {
        foreach (var method in entry.DependencyMethods)
        {
            var foreign = Harmony.GetPatchInfo(method)?.Owners.Where(x => x != HarmonyId).ToArray();
            if (foreign is { Length: > 0 }) return "Foreign Harmony owner on " + method.Name + ": " + string.Join(", ", foreign);
        }
        return null;
    }
    private void RefreshAssets()
    {
        var world = api.World as ServerMain;
        var packet = world == null ? null : AccessTools.Field(typeof(ServerMain), "serverAssetsPacket")?.GetValue(world);
        var buffer = packet == null ? null : AccessTools.Field(packet.GetType(), "buffer")?.GetValue(packet) as byte[];
        AssetCompressionOptimizations.Track(buffer, assetCacheBytes);
    }
    private void Check()
    {
        lock (gate)
        {
            if (disposed) return;
            foreach (var entry in entries.Where(x => x.Installed || x.State == "standby").ToArray())
            {
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
                    if (entry.Installed && Harmony.GetPatchInfo(entry.TargetMethod)?.Owners.Contains(HarmonyId) != true)
                    { Disable(entry, "failed", "Own runtime patch was removed; stock behavior retained. Reapply settings to retry."); continue; }
                    if(entry.Name == "assetCompressionCache") RefreshAssets();
                    string? conflict = Conflict(entry);
                    if (conflict != null) { Disable(entry, "conflict", conflict); continue; }
                    if (Failures(entry.Name) > entry.FailureBaseline)
                        Disable(entry, "failed", "A new failure was observed during this activation. Stock behavior retained; reapply settings to retry.");
                }
                catch (Exception error) { Disable(entry, "failed", "Compatibility monitor failed: " + error.GetType().Name); }
            }
        }
    }
    private static long Failures(string name)
    {
        var query = PlayerQueryOptimizations.GetCounters();
        return name switch { "pathMainThreadBudget" => PathQueueOptimizations.MainFailures, "physicsPacketListReuse" or "pathQueueLimitFix" or "nearestEntitySearch" or "interactableEntitySearch" or "aiTaskStartDispatch" or "worldgenRequestReuse" or "saveSnapshotRelease" or "aiFilterDispatch" or "repulseDelegateReuse" => 0, "onlinePlayerSnapshots" => query.SnapshotFailures,
            "nearbyPlayerQueries" => query.AroundFailures,
            "assetCompressionCache" => AssetCompressionOptimizations.Counters.Failures,
            _ => StreamCopyOptimizations.Snapshot.Failures };
    }
    private void Disable(Entry entry, string state, string reason)
    {
        entry.Enable(false);
        try { harmony.Unpatch(entry.TargetMethod, HarmonyPatchType.All, HarmonyId); }
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
        return new Dictionary<string,long> { ["physicsPacketPasses"] = physics.Calls, ["physicsPacketBuffersCreated"] = physics.Created, ["physicsPacketOversizedBuffersReleased"] = physics.Oversized, ["pathAssistanceFailures"] = PathQueueOptimizations.MainFailures, ["pathWorkerLimitStops"] = paths.WorkerLimitStops, ["pathMainSearches"] = paths.MainSearches, ["pathDeferredTicks"] = paths.DeferredTicks, ["nearestEntitySearchCalls"] = entitySearch.NearestCalls, ["interactableEntitySearchCalls"] = entitySearch.InteractableCalls, ["entitySearchContextsCreated"] = entitySearch.ContextsCreated, ["saveSnapshotReferencesReleased"] = saves.ReferencesReleased, ["saveSnapshotCompletions"] = saves.Completions, ["onlineSnapshotCalls"] = q.SnapshotCalls,
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
