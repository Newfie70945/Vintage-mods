using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY")
    ?? throw new InvalidOperationException("Set VINTAGE_STORY to the official Vintage Story 1.22.7 install directory."));
string modPath = Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? args.FirstOrDefault()
    ?? throw new InvalidOperationException("Set NSS_ASSEMBLY to the compiled NewfiesServerEssentialsSuite.dll path."));
string output = Path.GetDirectoryName(modPath)!;
var roots = new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods"), output };
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    foreach (string root in roots)
    {
        string path = Path.Combine(root, name.Name + ".dll");
        if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
    }
    return null;
};
var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);
var type = mod.GetType("BlockLogger.WebMap.WebMapBridgeService", true)!;
var configType = mod.GetType("BlockLogger.WebMap.WebMapConfig", true)!;
object config = Activator.CreateInstance(configType)!;
void Property(string name, object value) => configType.GetProperty(name)!.SetValue(config, value);
int Rate() => (int)configType.GetProperty("PlayerIconRefreshSeconds")!.GetValue(config)!;
if (Rate() != 60) throw new Exception("Legacy default changed");
foreach (var pair in new[] { (-5, 1), (10, 10), (999, 300) }) {
    Property("PlayerIconRefreshSeconds", pair.Item1);
    configType.GetMethod("Clamp")!.Invoke(config, null);
    if (Rate() != pair.Item2) throw new Exception("Invalid interval normalization");
    var clone = configType.GetMethod("Clone")!.Invoke(config, null)!;
    if ((int)configType.GetProperty("PlayerIconRefreshSeconds")!.GetValue(clone)! != pair.Item2) throw new Exception("Config clone lost interval");
}
var reloaded = System.Text.Json.JsonSerializer.Deserialize("{\"Enabled\":true}", configType)!;
if ((int)configType.GetProperty("PlayerIconRefreshSeconds")!.GetValue(reloaded)! != 60) throw new Exception("Old config migration changed default");
if ((bool)configType.GetProperty("LiveTerrainUpdatesEnabled")!.GetValue(config)!) throw new Exception("Automatic terrain must be opt-in");
Property("LiveTerrainUpdatesEnabled",true); Property("LiveTerrainUpdateSeconds",-1); Property("LiveTerrainMaxTilesPerPass",99);
configType.GetMethod("Clamp")!.Invoke(config,null);
var liveClone=configType.GetMethod("Clone")!.Invoke(config,null)!;
if(!(bool)configType.GetProperty("LiveTerrainUpdatesEnabled")!.GetValue(liveClone)! || (int)configType.GetProperty("LiveTerrainUpdateSeconds")!.GetValue(liveClone)! !=30 || (int)configType.GetProperty("LiveTerrainMaxTilesPerPass")!.GetValue(liveClone)! !=16)throw new Exception("Live terrain settings lost or unbounded");
var liveReload=System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(liveClone,configType),configType)!;
if(!(bool)configType.GetProperty("LiveTerrainUpdatesEnabled")!.GetValue(liveReload)!)throw new Exception("Live terrain choice lost after reload");
object service = RuntimeHelpers.GetUninitializedObject(type);
void Field(string name, object value) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, value);
Field("gate", new object()); Field("config", config);
Field("jsonOptions", new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
var markerType = mod.GetType("BlockLogger.WebMap.WebMapPlayerMarker", true)!;
var rows = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(markerType))!;
var marker = Activator.CreateInstance(markerType)!;
markerType.GetProperty("Name")!.SetValue(marker, "Visible fixture"); rows.Add(marker);
Field("playerCache", rows); Field("playerCacheUnixMilliseconds", 123L);
foreach (bool enabled in new[] { false, true }) foreach (bool allow in new[] { false, true }) foreach (bool show in new[] { false, true }) {
    Property("Enabled", enabled); Property("AllowLivePlayerData", allow); Property("ShowPlayers", show);
    // API/world fields deliberately remain null: serialization must read cached data only.
    var bytes = (byte[])type.GetMethod("GetPublicPlayerSnapshotBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, null)!;
    using var json = System.Text.Json.JsonDocument.Parse(bytes);
    bool visible = enabled && allow && show;
    if (json.RootElement.GetProperty("players").GetArrayLength() != (visible ? 1 : 0) || json.RootElement.GetProperty("showPlayers").GetBoolean() != visible || json.RootElement.GetProperty("activePlayerCount").GetInt32() != (visible ? 1 : 0)) throw new Exception("Privacy gate bypass");
    if (json.RootElement.GetProperty("playerRefreshSeconds").GetInt32() != 300) throw new Exception("Payload lost configured rate");
}
Console.WriteLine("PASS: legacy config default, bounds, clone and cached public player endpoint privacy matrix; no world access");
int worldReads = 0; long nextId = 10;
var registrations = new List<int>(); var unregistered = new List<long>(); var callbacks = new List<Delegate>(); var queued = new List<Action>();
var apiField = type.GetField("api", BindingFlags.Instance | BindingFlags.NonPublic)!;
Type apiType = apiField.FieldType, eventsType = apiType.GetProperty("Event")!.PropertyType, worldType = apiType.GetProperty("World")!.PropertyType;
object Proxy(Type t, Func<MethodInfo, object?[]?, object?> handler) { var proxy = DispatchProxy.Create(t, typeof(ApiProxy)); ((ApiProxy)proxy).Handler = handler; return proxy; }
object world = Proxy(worldType, (m, a) => { if (m.Name == "get_AllOnlinePlayers") { worldReads++; return Array.CreateInstance(m.ReturnType.GetElementType()!, 0); } throw new Exception("Unexpected world call " + m.Name); });
object events = Proxy(eventsType, (m, a) => {
    if (m.Name == "RegisterGameTickListener") { callbacks.Add((Delegate)a![0]!); registrations.Add((int)a[2]!); return nextId++; }
    if (m.Name == "UnregisterGameTickListener") { unregistered.Add((long)a![0]!); return null; }
    if (m.Name == "EnqueueMainThreadTask") { queued.Add((Action)a![0]!); return null; }
    throw new Exception("Unexpected event call " + m.Name);
});
Field("api", Proxy(apiType, (m, a) => m.Name == "get_Event" ? events : m.Name == "get_World" ? world : throw new Exception("Unexpected API call " + m.Name)));
Field("playerListenerId", -1L);
object? Call(string name) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, null);
Property("Enabled", true); Property("AllowLivePlayerData", true); Property("ShowPlayers", true); Property("PlayerIconRefreshSeconds", 5);
Call("ConfigurePlayerListener");
if (!registrations.SequenceEqual(new[] { 5000 }) || worldReads != 1) throw new Exception("Configured capture interval not registered");
callbacks[^1].DynamicInvoke(0f);
if (worldReads != 1) throw new Exception("Capture without viewer");
Property("PlayerIconRefreshSeconds", 2); Call("ConfigurePlayerListener");
if (!registrations.SequenceEqual(new[] { 5000, 2000 }) || !unregistered.SequenceEqual(new[] { 10L })) throw new Exception("Live interval change did not replace listener");
Field("playerCacheUnixMilliseconds", 0L); var before = worldReads;
Call("MarkPublicViewer"); Call("MarkPublicViewer");
if (queued.Count != 1 || worldReads != before) throw new Exception("Viewer requests do not coalesce or accessed world on HTTP thread");
queued[0](); callbacks[^1].DynamicInvoke(0f);
if (worldReads != before + 1) throw new Exception("Capture duplicated despite fresh cache");
Property("ShowPlayers", false); Call("ConfigurePlayerListener");
if (!unregistered.SequenceEqual(new[] { 10L, 11L })) throw new Exception("Privacy disable left listener active");
Console.WriteLine("PASS: game-thread capture cadence, listener replacement, viewer gating, coalesced requests and duplicate suppression");
public class ApiProxy : DispatchProxy {
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
