using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using System.Text.Json;

string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY."));
string assemblyPath = Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? args.FirstOrDefault() ?? throw new Exception("Set NSS_ASSEMBLY."));
var roots = new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods"), Path.GetDirectoryName(assemblyPath)! };
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (string root in roots) { string path = Path.Combine(root, name.Name + ".dll"); if (File.Exists(path)) return context.LoadFromAssemblyPath(path); }
    return null;
};
Assembly mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
Type privacyType = mod.GetType("BlockLogger.WebMap.WebMapPlayerPrivacyStore", true)!;
Type waypointType = mod.GetType("BlockLogger.WebMap.WebMapWaypointStore", true)!;
Type markerType = mod.GetType("BlockLogger.WebMap.WebMapWaypoint", true)!;
Type apiType = privacyType.GetConstructors().Single().GetParameters()[0].ParameterType;
PropertyInfo Property(Type type, string name) => type.GetProperty(name) ?? type.GetInterfaces().Select(t => t.GetProperty(name)).First(p => p != null)!;
object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler) {
    object proxy = DispatchProxy.Create(type, typeof(FakeApi)); ((FakeApi)proxy).Handler = handler; return proxy;
}
object logger = Proxy(Property(apiType, "Logger").PropertyType, (_, _) => null);
object world = Proxy(Property(apiType, "World").PropertyType, (m, _) => m.Name == "get_SavegameIdentifier" ? "map-state-check-world" : throw new Exception("Unexpected world call " + m.Name));
object api = Proxy(apiType, (m, _) => m.Name switch { "get_World" => world, "get_Logger" => logger, _ => throw new Exception("Unexpected API call " + m.Name) });
string tempRoot = Path.Combine(Path.GetTempPath(), "nss-map-state-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
object Store(Type type, string folder) { Directory.CreateDirectory(folder); return Activator.CreateInstance(type, api, folder)!; }
string StorePath(object store) => (string)store.GetType().GetField("filePath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
object? Call(object store, string method, params object?[] values) => store.GetType().GetMethods().Single(m => m.Name == method && m.GetParameters().Length == values.Length).Invoke(store, values);
bool Hidden(object store, string uid) => (bool)Call(store, "IsHidden", uid)!;
void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
void Refuses(Action run) { try { run(); throw new Exception("Write unexpectedly succeeded."); } catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { } }
IList Rows(object store) => (IList)Call(store, "Snapshot")!;
string Value(object row, string name) => (string)markerType.GetProperty(name)!.GetValue(row)!;
object Marker(string name, string id = "") { object row = Activator.CreateInstance(markerType)!; markerType.GetProperty("Name")!.SetValue(row, name); markerType.GetProperty("Id")!.SetValue(row, id); return row; }
try {
    string folder = Path.Combine(tempRoot, "privacy");
    object privacy = Store(privacyType, folder); string path = StorePath(privacy);
    Check(!Hidden(privacy, "alice"), "Fresh opt-out state unexpectedly hides all players.");
    Call(privacy, "SetHidden", "alice", true);
    privacy = Store(privacyType, folder);
    Check(Hidden(privacy, "alice") && !Hidden(privacy, "bob"), "Successful privacy save did not survive reload.");
    byte[] original = File.ReadAllBytes(path);
    Directory.CreateDirectory(path + ".tmp");
    Refuses(() => Call(privacy, "SetHidden", "alice", false));
    Check(Hidden(privacy, "alice") && File.ReadAllBytes(path).SequenceEqual(original), "Failed show save exposed a hidden player or changed the file.");
    Refuses(() => Call(privacy, "SetHidden", "bob", true));
    Check(Hidden(privacy, "bob") && File.ReadAllBytes(path).SequenceEqual(original), "Failed hide save left new player publicly visible.");
    Directory.Delete(path + ".tmp");
    Call(privacy, "SetHidden", "bob", true); // Same request retries its pending save.
    privacy = Store(privacyType, folder);
    Check(Hidden(privacy, "alice") && Hidden(privacy, "bob"), "Retry forgot a pending opt-out.");
    Console.WriteLine("PASS privacy: persisted opt-outs, failed show stays hidden, failed hide takes effect, retry persists pending state.");
    foreach (string damaged in new[] { "{broken", "null", "{\"SchemaVersion\":1}", "{\"SchemaVersion\":99,\"HiddenPlayerUids\":[\"alice\"]}", "{\"SchemaVersion\":1,\"HiddenPlayerUids\":null}", "{\"SchemaVersion\":1,\"HiddenPlayerUids\":[null]}" }) {
        File.WriteAllText(path, damaged); privacy = Store(privacyType, folder);
        Check(Hidden(privacy, "alice") && Hidden(privacy, "new-player"), "Unreadable privacy document defaulted to public visibility.");
        Refuses(() => Call(privacy, "SetHidden", "alice", false));
        Check(File.ReadAllText(path) == damaged, "Damaged privacy original was overwritten.");
    }
    using (var file = new FileStream(path, FileMode.Create, FileAccess.Write)) file.SetLength(65L * 1024 * 1024);
    privacy = Store(privacyType, folder);
    Check(Hidden(privacy, "alice") && new FileInfo(path).Length == 65L * 1024 * 1024, "Oversized privacy file was accepted or destroyed.");
    File.WriteAllText(path, "{\"SchemaVersion\":1,\"HiddenPlayerUids\":[\"alice\"]}");
    privacy = Store(privacyType, folder);
    Check(Hidden(privacy, "alice") && !Hidden(privacy, "new-player"), "Owner-restored privacy file did not recover after reload.");
    Call(privacy, "SetHidden", "alice", false);
    Check(!Hidden(Store(privacyType, folder), "alice"), "Recovered privacy file stayed incorrectly read-only.");
    Console.WriteLine("PASS privacy: malformed/null/missing-list/future-schema/oversized state fails closed; originals untouched; owner restore+reload recovers.");

    folder = Path.Combine(tempRoot, "waypoints");
    object waypoints = Store(waypointType, folder); path = StorePath(waypoints);
    object alpha = Call(waypoints, "Upsert", Marker("Alpha"), "admin")!;
    string alphaId = Value(alpha, "Id");
    Call(waypoints, "CreatePlayerWaypoint", "alice", "Alice", "minor", "Beta", "base", 1d, 2d, 3d, 0, 10);
    waypoints = Store(waypointType, folder);
    Check(Rows(waypoints).Count == 2, "Waypoints failed to reload.");
    original = File.ReadAllBytes(path); Directory.CreateDirectory(path + ".tmp");
    Refuses(() => Call(waypoints, "Upsert", Marker("Changed", alphaId), "admin"));
    Refuses(() => Call(waypoints, "Delete", alphaId));
    Refuses(() => Call(waypoints, "CreatePlayerWaypoint", "alice", "Alice", "major", "Gamma", "mine", 1d, 2d, 3d, 0, 10));
    Refuses(() => Call(waypoints, "DeletePlayerWaypoint", "alice", "Beta", null));
    var unchanged = Rows(waypoints);
    Check(unchanged.Count == 2 && Value(unchanged[0]!, "Name") == "Alpha" && Value(unchanged[1]!, "Name") == "Beta" && File.ReadAllBytes(path).SequenceEqual(original), "Failed write mutated waypoint state or file.");
    Directory.Delete(path + ".tmp");
    Call(waypoints, "Upsert", Marker("Changed", alphaId), "admin");
    Check(Value(Rows(Store(waypointType, folder))[0]!, "Name") == "Changed", "Waypoint retry failed to persist.");
    Console.WriteLine("PASS waypoints: failed edit/delete/create/player delete preserve memory and file; successful retry survives reload.");
    foreach (string damaged in new[] { "{broken", "null", "{\"SchemaVersion\":3,\"WorldId\":\"map-state-check-world\"}", "{\"SchemaVersion\":99,\"WorldId\":\"map-state-check-world\",\"Waypoints\":[]}", "{\"SchemaVersion\":3,\"WorldId\":\"another-world\",\"Waypoints\":[]}", "{\"SchemaVersion\":3,\"WorldId\":\"map-state-check-world\",\"Waypoints\":null}", "{\"SchemaVersion\":3,\"WorldId\":\"map-state-check-world\",\"Waypoints\":[{\"Id\":\"one\",\"Name\":\"Owned\",\"IsPlayerWaypoint\":true}]}" }) {
        File.WriteAllText(path, damaged); waypoints = Store(waypointType, folder);
        Refuses(() => Call(waypoints, "Upsert", Marker("Overwrite"), "admin"));
        Check(File.ReadAllText(path) == damaged, "Unusable waypoint file was overwritten.");
    }
    File.WriteAllText(path, "{\"SchemaVersion\":1,\"WorldId\":\"map-state-check-world\",\"Waypoints\":[{\"Id\":\"legacy\",\"Name\":\"Legacy\"}]}");
    waypoints = Store(waypointType, folder);
    Check(Rows(waypoints).Count == 1 && Value(Rows(waypoints)[0]!, "Name") == "Legacy", "Compatible old waypoint schema was rejected.");
    Call(waypoints, "Upsert", Marker("Upgraded", "legacy"), "admin");
    Check(JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("SchemaVersion").GetInt32() == 3, "Compatible waypoint update did not retain current schema.");
    Console.WriteLine("PASS waypoints: broken/future/foreign-world/ownership-invalid originals protected; compatible schema migration works.");

    // Pause a real public snapshot after it captures marker state, then invalidate
    // privacy while it is still serializing. The response must not repopulate the cache.
    Type bridgeType = mod.GetType("BlockLogger.WebMap.WebMapBridgeService", true)!;
    object bridge = RuntimeHelpers.GetUninitializedObject(bridgeType);
    void Field(string name, object? value) => bridgeType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bridge, value);
    object? Read(string name) => bridgeType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge);
    object? Invoke(string name) => bridgeType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bridge, null);
    Delegate Callback(string name, Func<object?> handler) {
        Type delegateType = bridgeType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType;
        return Expression.Lambda(delegateType, Expression.Convert(Expression.Invoke(Expression.Constant(handler)), delegateType.GetGenericArguments()[0])).Compile();
    }
    Type configType = mod.GetType("BlockLogger.WebMap.WebMapConfig", true)!;
    object config = Activator.CreateInstance(configType)!;
    void Config(string name, object value) => configType.GetProperty(name)!.SetValue(config, value);
    Field("gate", new object()); Field("publicSnapshotBuildGate", new object()); Field("config", config);
    Field("publicSnapshotCache", Array.Empty<byte>());
    Field("jsonOptions", new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    Field("playerPrivacyStore", privacy); Field("waypointStore", waypoints);
    Field("terrainStatus", Activator.CreateInstance(mod.GetType("BlockLogger.WebMap.WebMapTerrainExportStatus", true)!));
    Field("activityCache", Activator.CreateInstance(typeof(List<>).MakeGenericType(mod.GetType("BlockLogger.WebMap.WebMapActivityMarker", true)!)));
    Type playerMarkerType = mod.GetType("BlockLogger.WebMap.WebMapPlayerMarker", true)!;
    IList playerRows = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(playerMarkerType))!;
    object playerMarker = Activator.CreateInstance(playerMarkerType)!;
    playerMarkerType.GetProperty("Name")!.SetValue(playerMarker, "Formerly visible"); playerRows.Add(playerMarker);
    Field("playerCache", playerRows); Field("dashboardPortProvider", Callback("dashboardPortProvider", () => 0));
    Config("Enabled", true); Config("ShowActivity", false); Config("ShowWaypoints", false);
    Config("AllowLivePlayerData", true); Config("ShowPlayers", true);
    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
    Field("badBlockProvider", Callback("badBlockProvider", () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Snapshot barrier timed out."); return null; }));
    Task<byte[]> pending = Task.Run(() => (byte[])Invoke("GetPublicSnapshotBytes")!);
    try {
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "Snapshot did not reach controlled concurrent invalidation point.");
        Config("ShowPlayers", false); Invoke("InvalidatePublicSnapshot");
    } finally { release.Set(); }
    Check(pending.Wait(TimeSpan.FromSeconds(10)), "Snapshot did not complete.");
    Check(((byte[])Read("publicSnapshotCache")!).Length == 0, "Old snapshot was republished after privacy invalidation.");
    Field("badBlockProvider", Callback("badBlockProvider", () => null));
    byte[] fresh = (byte[])Invoke("GetPublicSnapshotBytes")!;
    using (JsonDocument json = JsonDocument.Parse(fresh)) Check(json.RootElement.GetProperty("players").GetArrayLength() == 0, "Next public snapshot retained hidden markers.");
    Check(((byte[])Read("publicSnapshotCache")!).Length > 0, "Valid fresh snapshot was not cached.");
    Console.WriteLine("PASS public map: concurrent privacy invalidation prevents stale cache republish; next response omits hidden players.");

    // A pending activity refresh for an old setting must not delay the next one by
    // publishing an old timestamp when its query provider finally becomes available.
    Config("ShowActivity", true); Field("activityCacheUnixMilliseconds", 0L);
    entered.Reset(); release.Reset();
    Field("queryProvider", Callback("queryProvider", () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Activity barrier timed out."); return null; }));
    Invoke("QueueActivityRefreshIfNeeded");
    try {
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "Activity refresh did not reach controlled invalidation point.");
        Field("activityRefreshGeneration", 1L);
    } finally { release.Set(); }
    Check(SpinWait.SpinUntil(() => (int)Read("activityRefreshInFlight")! == 0, TimeSpan.FromSeconds(10)), "Old activity task remained admitted.");
    Check((long)Read("activityCacheUnixMilliseconds")! == 0, "Old activity task restored invalidated timestamp.");
    Field("queryProvider", Callback("queryProvider", () => null)); Invoke("QueueActivityRefreshIfNeeded");
    Check(SpinWait.SpinUntil(() => (int)Read("activityRefreshInFlight")! == 0, TimeSpan.FromSeconds(10)), "Fresh activity task remained admitted.");
    Check((long)Read("activityCacheUnixMilliseconds")! > 0, "Fresh activity task was incorrectly blocked after settings invalidation.");
    Field("activityCacheUnixMilliseconds", 0L); Field("disposed", true); Invoke("QueueActivityRefreshIfNeeded");
    Check((int)Read("activityRefreshInFlight")! == 0, "Disposed map admitted another activity task.");
    Console.WriteLine("PASS activity: invalidated async work cannot restore its old timestamp; next refresh proceeds and disposed maps reject new work.");

} finally { Directory.Delete(tempRoot, true); }
public class FakeApi : DispatchProxy {
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
}
