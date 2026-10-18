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
    public const string HarmonyId = "newfies.serveroptimizer.engine.0.3.0";
    private readonly ICoreServerAPI api;
    private readonly Harmony harmony = new(HarmonyId);
    private readonly List<Entry> entries = new();
    private readonly object gate = new();
    private long listener = -1;
    private bool disposed;
    private readonly int minimumNearbyClients;
    private readonly int assetCacheBytes;
    private readonly Func<int> clientCount;
    private sealed class Entry(string name, bool requested, Func<MethodInfo> target, Func<MethodInfo[]> dependencies, MethodInfo prefix, Action<bool> enable)
    {
        public string Name = name, State = "pending", Reason = "Not installed";
        public bool Requested = requested;
        public Func<MethodInfo> Target = target;
        public Func<MethodInfo[]> Dependencies = dependencies;
        public MethodInfo Prefix = prefix;
        public Action<bool> Enable = enable;
        public bool Installed;
        public MethodInfo? Postfix;
    }
    public EngineOptimizations(ICoreServerAPI api, OptimizerConfig config, Func<int>? clientCount = null)
    {
        this.api = api;
        minimumNearbyClients = config.EngineOptimizations.MinimumNearbyClients;
        assetCacheBytes = config.EngineOptimizations.AssetCacheMaxMiB * 1048576;
        AssetCompressionOptimizations.Configure(false);
        this.clientCount = clientCount ?? (() => api.World is ServerMain world ? world.Clients.Values.Count : 0);
        PlayerQueryOptimizations.MinimumNearbyClients = minimumNearbyClients;
        PlayerQueryOptimizations.SnapshotEnabled = false;
        PlayerQueryOptimizations.AroundEnabled = false;
        StreamCopyOptimizations.ConfigureRead(false);
        var options = config.EngineOptimizations;
        entries.Add(new("onlinePlayerSnapshots", options.OnlinePlayerSnapshots, () => PlayerQueryOptimizations.SnapshotTarget,
            () => [PlayerQueryOptimizations.SnapshotTarget], PlayerQueryOptimizations.SnapshotPrefixMethod, value => PlayerQueryOptimizations.SnapshotEnabled = value));
        entries.Add(new("nearbyPlayerQueries", options.NearbyPlayerQueries, () => PlayerQueryOptimizations.AroundTarget,
            () => [PlayerQueryOptimizations.AroundTarget], PlayerQueryOptimizations.AroundPrefixMethod, value => PlayerQueryOptimizations.AroundEnabled = value));
        entries.Add(new("bulkStreamReads", options.BulkStreamReads, () => StreamCopyOptimizations.ReadTarget,
            () => StreamCopyOptimizations.ReadConflictTargets, StreamCopyOptimizations.ReadPrefixMethod, StreamCopyOptimizations.ConfigureRead));
        entries.Add(new("assetCompressionCache", options.AssetCompressionCache, () => AssetCompressionOptimizations.Target,
            () => AssetCompressionOptimizations.ConflictTargets, AssetCompressionOptimizations.PrefixMethod, AssetCompressionOptimizations.Configure)
            { Postfix = AssetCompressionOptimizations.PostfixMethod });
        string? blocked = !config.Enabled ? "Optimizer master switch is disabled." : config.Compatibility.SafeMode ?
            "Compatibility.SafeMode is enabled; stock methods retained." : EngineGuard();
        foreach (var entry in entries)
        {
            if (!entry.Requested) { entry.State = "disabled"; entry.Reason = "Independent module switch is off."; continue; }
            if (blocked != null) { entry.State = "blocked"; entry.Reason = blocked; continue; }
            try
            {
                string why;
                bool contract = entry.Name == "assetCompressionCache" ? AssetCompressionOptimizations.ValidateContract(out why) :
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
            foreach (var entry in entries.Where(x => x.Installed).ToArray())
                Disable(entry, "failed", "Compatibility monitor could not start: " + error.GetType().Name);
        }
        foreach (var entry in entries) api.Logger.Notification("[NewfiesOptimizer] {0}: {1} — {2}", entry.Name, entry.State, entry.Reason);
    }
    private void Activate(Entry entry)
    {
        string? conflict = Conflict(entry);
        if (conflict != null) { entry.State = "conflict"; entry.Reason = conflict; return; }
        harmony.Patch(entry.Target(), prefix: new HarmonyMethod(entry.Prefix), postfix: entry.Postfix == null ? null : new HarmonyMethod(entry.Postfix));
        if(entry.Name == "assetCompressionCache") RefreshAssets();
        entry.Installed = true; entry.Enable(true); entry.State = "active";
        entry.Reason = "Verified official 1.22.7; independently controlled prefix installed.";
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
                (typeof(ServerMain).Assembly, "e08f22b493b92feaf0aaeb79d22437ea0f7efc38aa7f72a04a47f98bc0e40df0") })
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
        foreach (var method in entry.Dependencies())
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
                    if (entry.Installed && Harmony.GetPatchInfo(entry.Target())?.Owners.Contains(HarmonyId) != true)
                    { Disable(entry, "failed", "Own runtime patch was removed; stock behavior retained. Reapply settings to retry."); continue; }
                    if(entry.Name == "assetCompressionCache") RefreshAssets();
                    string? conflict = Conflict(entry);
                    if (conflict != null) { Disable(entry, "conflict", conflict); continue; }
                    var counters = PlayerQueryOptimizations.GetCounters();
                    long failures = entry.Name switch { "onlinePlayerSnapshots" => counters.SnapshotFailures,
                        "nearbyPlayerQueries" => counters.AroundFailures, "assetCompressionCache" => AssetCompressionOptimizations.Counters.Failures, _ => StreamCopyOptimizations.Snapshot.Failures };
                    if (failures > 0) Disable(entry, "failed", "Observed a query/copy exception; original exception was preserved. Module stood down to stock behavior.");
                }
                catch (Exception error) { Disable(entry, "failed", "Compatibility monitor failed: " + error.GetType().Name); }
            }
        }
    }
    private void Disable(Entry entry, string state, string reason)
    {
        entry.Enable(false);
        try { harmony.Unpatch(entry.Target(), HarmonyPatchType.All, HarmonyId); }
        catch (Exception error) { reason += "; own patch removal failed: " + error.GetType().Name + ". Prefix is disabled."; }
        entry.Installed = false; entry.State = state; entry.Reason = reason;
    }
    public ModuleStatus[] Modules() { lock (gate) return entries.Select(x => new ModuleStatus(x.Name, x.State, x.Reason)).ToArray(); }
    public IReadOnlyDictionary<string,long> Counters()
    {
        var q = PlayerQueryOptimizations.GetCounters(); var s = StreamCopyOptimizations.Snapshot;
        return new Dictionary<string,long> { ["onlineSnapshotCalls"] = q.SnapshotCalls,
            ["nearbyQueryCalls"] = q.AroundCalls, ["snapshotFallbacks"] = q.SnapshotFallbacks,
            ["nearbyFallbacks"] = q.AroundFallbacks, ["snapshotFailures"] = q.SnapshotFailures,
            ["nearbyFailures"] = q.AroundFailures, ["assetCacheHits"] = AssetCompressionOptimizations.Counters.Hits, ["assetCacheMisses"] = AssetCompressionOptimizations.Counters.Misses,
            ["assetCacheBytes"] = AssetCompressionOptimizations.Counters.Bytes, ["assetCacheFailures"] = AssetCompressionOptimizations.Counters.Failures, ["bulkReadCalls"] = s.AcceleratedCalls,
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
            if (listener >= 0) api.Event.UnregisterGameTickListener(listener);
            foreach (var entry in entries) Disable(entry, "stopped", "Own patch removed during disposal.");
            PlayerQueryOptimizations.ClearCurrentThreadScratch();
        }
    }
}
