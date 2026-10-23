using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace NewfiesOptimizer;

public sealed partial class OptimizerMod
{
    private OptimizerConfig? currentConfig;
    private bool bridgeActive;
    public const string ControlEvent = "newfiesse:optimizer-control-v1";
    private static readonly JsonSerializerOptions WebJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private object ControlSnapshot() => new {
        available = true, protocol = 1, version = "0.10.0", success = true,
        profiling = profiler?.IsRunning ?? false,
        captureAvailable = profiler?.Session != null,
        capabilities = new[] { "capture-clear", "entity-search-reuse", "physics-packet-list-reuse", "despawn-packet-projection", "entity-distance-policies", "waypoint-node-pooling", "attribute-noop-skip" },
        settings = currentConfig == null ? null : new {
            enabled = currentConfig.Enabled, safeMode = currentConfig.Compatibility.SafeMode,
            aiDistanceThrottle = currentConfig.EngineOptimizations.AiDistanceThrottle,
            distantWanderThrottle = currentConfig.EngineOptimizations.DistantWanderThrottle,
            repulsionDistanceThrottle = currentConfig.EngineOptimizations.RepulsionDistanceThrottle,
            attributeNoOpSkip = currentConfig.EngineOptimizations.AttributeNoOpSkip,
            waypointNodePooling = currentConfig.EngineOptimizations.WaypointNodePooling,
            entityProtectionRadius = currentConfig.EngineOptimizations.EntityProtectionRadius,
            aiDistanceIntervalMs = currentConfig.EngineOptimizations.AiDistanceIntervalMs,
            distantWanderIntervalMs = currentConfig.EngineOptimizations.DistantWanderIntervalMs,
            repulsionDistanceIntervalMs = currentConfig.EngineOptimizations.RepulsionDistanceIntervalMs,
            entityPolicyInclude = currentConfig.EngineOptimizations.EntityPolicyInclude,
            entityPolicyExclude = currentConfig.EngineOptimizations.EntityPolicyExclude,
            onlinePlayerSnapshots = currentConfig.EngineOptimizations.OnlinePlayerSnapshots,
            worldgenRequestReuse = currentConfig.EngineOptimizations.WorldgenRequestReuse,
            saveSnapshotRelease = currentConfig.EngineOptimizations.SaveSnapshotRelease,
            pathQueueLimitFix = currentConfig.EngineOptimizations.PathQueueLimitFix,
            pathMainThreadBudget = currentConfig.EngineOptimizations.PathMainThreadBudget,
            pathMainThreadBudgetMs = currentConfig.EngineOptimizations.PathMainThreadBudgetMs,
            pathMainThreadMaxSearches = currentConfig.EngineOptimizations.PathMainThreadMaxSearches,
            pathMainThreadGraceMs = currentConfig.EngineOptimizations.PathMainThreadGraceMs,
            nearestEntitySearch = currentConfig.EngineOptimizations.NearestEntitySearch,
            interactableEntitySearch = currentConfig.EngineOptimizations.InteractableEntitySearch,
            aiTaskStartDispatch = currentConfig.EngineOptimizations.AiTaskStartDispatch,
            aiFilterDispatch = currentConfig.EngineOptimizations.AiFilterDispatch,
            despawnPacketProjection = currentConfig.EngineOptimizations.DespawnPacketProjection,
            physicsPacketListReuse = currentConfig.EngineOptimizations.PhysicsPacketListReuse,
            repulseDelegateReuse = currentConfig.EngineOptimizations.RepulseDelegateReuse,
            nearbyPlayerQueries = currentConfig.EngineOptimizations.NearbyPlayerQueries,
            minimumNearbyClients = currentConfig.EngineOptimizations.MinimumNearbyClients,
            bulkStreamReads = currentConfig.EngineOptimizations.BulkStreamReads,
            assetCompressionCache = currentConfig.EngineOptimizations.AssetCompressionCache,
            assetCacheMaxMiB = currentConfig.EngineOptimizations.AssetCacheMaxMiB },
        modules = optimizations?.Modules() ?? [], counters = optimizations?.Counters(),
        status = Status()
    };
    private void OnSuiteControl(string name, ref EnumHandling handling, IAttribute data)
    {
        if(!bridgeActive || data is not ITreeAttribute tree) return;
        bool success=true; string message="Optimizer status refreshed.";
        try
        {
            switch(tree.GetString("operation","get"))
            {
                case "get": break;
                case "set": ApplySettings(tree.GetString("settings","{}")); message="Settings saved and engine modules reapplied. Check actual module states below."; break;
                case "profile-start": message=profiler?.Start() ?? "Profiler unavailable."; success=profiler?.IsRunning==true; break;
                case "profile-stop": message=profiler?.Stop() ?? "Profiler unavailable."; break;
                case "export": message=profiler?.QueueExport() ?? "Profiler unavailable."; break;
                case "capture-clear":
                    var cleared = profiler?.ClearCapture() ?? (Success: false, Message: "Profiler unavailable.");
                    success = cleared.Success; message = cleared.Message; break;
                default: throw new ArgumentException("Unsupported optimizer operation.");
            }
        }
        catch(Exception error) { success=false; message=error is ArgumentException or InvalidOperationException ? error.Message : "Optimizer operation failed ("+error.GetType().Name+"). Existing saved settings and module diagnostics should be checked."; }
        tree.SetString("snapshot",JsonSerializer.Serialize(ControlSnapshot(),WebJson));
        tree.SetBool("success",success); tree.SetString("message",message);
    }
    private static string[] Patterns(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Array||value.GetArrayLength()>64)throw new ArgumentException("Entity code patterns must be an array of at most 64 strings.");
        var list=new List<string>();
        foreach(var pattern in value.EnumerateArray())
        {
            if(pattern.ValueKind!=JsonValueKind.String)throw new ArgumentException("Entity patterns must be strings.");
            string text=pattern.GetString()!.Trim();
            if(text.Length==0||text.Length>160||text.Any(c=>char.IsControl(c)||char.IsWhiteSpace(c))||!text.Contains(':'))throw new ArgumentException("Use qualified entity code patterns such as game:hare-*.");
            list.Add(text);
        }
        return list.ToArray();
    }
    // Called synchronously on the game thread by the authenticated Suite bridge.
    private void ApplySettings(string json)
    {
        if(api == null || currentConfig == null) throw new InvalidOperationException("Optimizer configuration is unavailable; inspect the server log.");
        if(profiler?.IsRunning == true) throw new InvalidOperationException("Stop the optimizer capture before changing modules.");
        var candidate = JsonSerializer.Deserialize<OptimizerConfig>(JsonSerializer.Serialize(currentConfig))!;
        using var doc=JsonDocument.Parse(json);
        if(doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Settings must be an object.");
        foreach(var item in doc.RootElement.EnumerateObject())
        {
            bool B() => item.Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.Value.GetBoolean() : throw new ArgumentException(item.Name+" must be boolean.");
            int N(int min,int max) => item.Value.TryGetInt32(out int value) && value>=min && value<=max ? value : throw new ArgumentException(item.Name+" is out of range.");
            switch(item.Name)
            {
                case "enabled": candidate.Enabled=B(); break;
                case "aiDistanceThrottle": candidate.EngineOptimizations.AiDistanceThrottle=B(); break;
                case "distantWanderThrottle": candidate.EngineOptimizations.DistantWanderThrottle=B(); break;
                case "repulsionDistanceThrottle": candidate.EngineOptimizations.RepulsionDistanceThrottle=B(); break;
                case "attributeNoOpSkip": candidate.EngineOptimizations.AttributeNoOpSkip=B(); break;
                case "waypointNodePooling": candidate.EngineOptimizations.WaypointNodePooling=B(); break;
                case "entityProtectionRadius": candidate.EngineOptimizations.EntityProtectionRadius=N(48,256); break;
                case "aiDistanceIntervalMs": candidate.EngineOptimizations.AiDistanceIntervalMs=N(100,1000); break;
                case "distantWanderIntervalMs": candidate.EngineOptimizations.DistantWanderIntervalMs=N(250,10000); break;
                case "repulsionDistanceIntervalMs": candidate.EngineOptimizations.RepulsionDistanceIntervalMs=N(50,500); break;
                case "entityPolicyInclude": candidate.EngineOptimizations.EntityPolicyInclude=Patterns(item.Value); break;
                case "entityPolicyExclude": candidate.EngineOptimizations.EntityPolicyExclude=Patterns(item.Value); break;
                case "safeMode": candidate.Compatibility.SafeMode=B(); break;
                case "worldgenRequestReuse": candidate.EngineOptimizations.WorldgenRequestReuse=B(); break;
                case "saveSnapshotRelease": candidate.EngineOptimizations.SaveSnapshotRelease=B(); break;
                case "pathQueueLimitFix": candidate.EngineOptimizations.PathQueueLimitFix=B(); break;
                case "pathMainThreadBudget": candidate.EngineOptimizations.PathMainThreadBudget=B(); break;
                case "pathMainThreadBudgetMs": candidate.EngineOptimizations.PathMainThreadBudgetMs=N(1,10); break;
                case "pathMainThreadMaxSearches": candidate.EngineOptimizations.PathMainThreadMaxSearches=N(1,8); break;
                case "pathMainThreadGraceMs": candidate.EngineOptimizations.PathMainThreadGraceMs=N(0,5000); break;
                case "nearestEntitySearch": candidate.EngineOptimizations.NearestEntitySearch=B(); break;
                case "interactableEntitySearch": candidate.EngineOptimizations.InteractableEntitySearch=B(); break;
                case "aiTaskStartDispatch": candidate.EngineOptimizations.AiTaskStartDispatch=B(); break;
                case "aiFilterDispatch": candidate.EngineOptimizations.AiFilterDispatch=B(); break;
                case "despawnPacketProjection": candidate.EngineOptimizations.DespawnPacketProjection=B(); break;
                case "physicsPacketListReuse": candidate.EngineOptimizations.PhysicsPacketListReuse=B(); break;
                case "repulseDelegateReuse": candidate.EngineOptimizations.RepulseDelegateReuse=B(); break;
                case "onlinePlayerSnapshots": candidate.EngineOptimizations.OnlinePlayerSnapshots=B(); break;
                case "nearbyPlayerQueries": candidate.EngineOptimizations.NearbyPlayerQueries=B(); break;
                case "minimumNearbyClients": candidate.EngineOptimizations.MinimumNearbyClients=N(0,4096); break;
                case "bulkStreamReads": candidate.EngineOptimizations.BulkStreamReads=B(); break;
                case "assetCompressionCache": candidate.EngineOptimizations.AssetCompressionCache=B(); break;
                case "assetCacheMaxMiB": candidate.EngineOptimizations.AssetCacheMaxMiB=N(1,32); break;
                default: throw new ArgumentException("Unknown optimizer setting: "+item.Name);
            }
        }
        candidate.Normalize();
        // Persist before touching active patches. A storage failure leaves the runtime unchanged.
        api.StoreModConfig(candidate,"optimizer.json");
        optimizations?.Dispose(); optimizations=null;
        currentConfig.Enabled=candidate.Enabled;
        currentConfig.EngineOptimizations=candidate.EngineOptimizations;
        currentConfig.Compatibility.SafeMode=candidate.Compatibility.SafeMode;
        try { optimizations=new(api,currentConfig); }
        finally { if(profiler != null) { profiler.ExternalModules=()=>optimizations?.Modules() ?? []; profiler.ExternalCounters=()=>optimizations?.Counters() ?? new Dictionary<string,long>(); } }
    }
}
