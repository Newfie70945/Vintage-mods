using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY."));
string dll = Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? throw new Exception("Set NSS_ASSEMBLY."));
var roots = new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods"), Path.GetDirectoryName(dll)! };
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (string root in roots) { string file = Path.Combine(root, name.Name + ".dll"); if (File.Exists(file)) return context.LoadFromAssemblyPath(file); }
    return null;
};
var apiAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryAPI.dll"));
var library = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryLib.dll"));
var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
Type A(string name) => apiAssembly.GetType(name, true)!;
Type E(string name) => library.GetType(name, true)!;
Type M(string name) => mod.GetType(name, true)!;
const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Set(object value, string name, object? data) {
    for (Type? type = value.GetType(); type != null; type = type.BaseType) {
        var field = type.GetField(name, instance | BindingFlags.DeclaredOnly);
        if (field != null) { field.SetValue(value, data); return; }
        var property = type.GetProperty(name, instance | BindingFlags.DeclaredOnly);
        if (property?.GetSetMethod(true) != null) { property.SetValue(value, data); return; }
    }
    throw new Exception("Fixture cannot set " + value.GetType().FullName + "." + name);
}
object Empty(Type type) => RuntimeHelpers.GetUninitializedObject(type);
object player = Empty(E("Vintagestory.Server.ServerPlayer"));
var worldDataField = player.GetType().GetField("worlddata", instance)!;
object worldData = Empty(worldDataField.FieldType);
Set(worldData, "PlayerUID", "interaction-fixture");
Set(worldData, "PickingRange", 5f);
Set(worldData, "connected", true);
Set(worldData, "GameMode", Enum.Parse(worldData.GetType().GetField("GameMode", instance)!.FieldType, "Survival"));
object entity = Empty(A("Vintagestory.API.Common.EntityPlayer"));
object position = Activator.CreateInstance(A("Vintagestory.API.Common.Entities.EntityPos"))!;
Set(position, "X", 100d); Set(position, "Y", 100d); Set(position, "Z", 100d); Set(position, "Dimension", 0);
Set(entity, "Pos", position);
Set(entity, "LocalEyePos", Activator.CreateInstance(A("Vintagestory.API.MathTools.Vec3d"), 0d, 1.6d, 0d));
Set(entity, "controls", Activator.CreateInstance(A("Vintagestory.API.Common.EntityControls"))!);
Set(worldData, "EntityPlayer", entity); worldDataField.SetValue(player, worldData);
object client = Empty(E("Vintagestory.Server.ConnectedClient")); Set(client, "Player", player);
Type guardType = M("BlockLogger.AntiCheat.PacketIngressGuard");
var constructor = guardType.GetConstructors(instance).Single();
Type apiType = constructor.GetParameters()[0].ParameterType;
Type worldType = A("Vintagestory.API.Server.IServerWorldAccessor");
Type accessorType = A("Vintagestory.API.Common.IBlockAccessor");
int reports = 0, reads = 0, loads = 0;
bool loaded = true;
object chunk = Empty(E("Vintagestory.Server.ServerChunk"));
object accessor = Proxy(accessorType, (method, args) => method.Name switch {
    "get_MapSizeX" or "get_MapSizeZ" => 1000,
    "get_MapSizeY" => 256,
    "GetChunkAtBlockPos" => ReadChunk(),
    _ => throw new Exception("Unexpected block API call: " + method.Name)
});
object? ReadChunk() { reads++; return loaded ? chunk : null; }
object world = Proxy(worldType, (method, args) => method.Name == "get_BlockAccessor" ? accessor : throw new Exception("Unexpected world API call: " + method.Name));
Type eventsType = apiType.GetProperty("Event")!.PropertyType;
object events = Proxy(eventsType, (method, args) => { if (method.Name == "EnqueueMainThreadTask") { reports++; return null; } throw new Exception("Unexpected events call: " + method.Name); });
object api = Proxy(apiType, (method, args) => method.Name switch { "get_World" => world, "get_Event" => events, _ => throw new Exception("Unexpected core API call: " + method.Name) });
object config = Activator.CreateInstance(M("BlockLogger.AntiCheat.AntiCheatConfig"))!; Set(config, "PacketGuardExemptAdmins", false);
object guard = constructor.Invoke(new[] { api, config, null }); Set(guard, "active", true);
var validate = guardType.GetMethod("ValidateBlockTarget", instance)!;
bool Target(int x, int y, int z) => (bool)validate.Invoke(guard, new[] { client, (object)x, y, z })!;

if (args.Contains("--expect-field-failure")) {
    try { Target(101, 100, 100); throw new Exception("Stale field reference did not reproduce the exception."); }
    catch (TargetInvocationException ex) when (ex.InnerException is MissingFieldException missing && missing.Message.Contains("Entity.Pos")) {
        Console.WriteLine("PASS: stale field-bound guard reproduces the reported Entity.Pos MissingFieldException.");
    }
    return;
}
Check(Target(101, 100, 100) && reports == 0, "Normal target failed or generated findings.");
Check(!Target(500, 100, 100), "Distant target accepted.");
Check(!Target(101, 32768 + 100, 100), "Cross-dimension target accepted.");
int before = reports;
loaded = false; Check(!Target(101, 100, 100) && reports == before, "Unloaded target accepted or blamed on player."); loaded = true;
Check(!Target(-1, 100, 100) && !Target(1000, 100, 100), "Out-of-world target accepted.");
Set(worldData, "EntityPlayer", null); before = reports; Check(!Target(101, 100, 100) && reports == before, "Missing entity caused a finding or accepted action."); Set(worldData, "EntityPlayer", entity);
Set(entity, "Pos", null); before = reports; Check(!Target(101, 100, 100) && reports == before, "Missing position caused a finding or accepted action."); Set(entity, "Pos", position);
Set(position, "X", double.NaN); Check(!Target(101, 100, 100) && reports == before, "Non-finite server position caused a finding or accepted action."); Set(position, "X", 100d);

// Run the actual packet branches that converge on ValidateBlockTarget.
var validatePacket = guardType.GetMethod("ValidatePacket", instance)!;
foreach (var kind in new[] { ("BlockPlaceOrBreak", "Packet_ClientBlockPlaceOrBreak", 3), ("BlockEntityPacket", "Packet_BlockEntityPacket", 22), ("HandInteraction", "Packet_ClientHandInteraction", 25), ("BlockDamage", "Packet_BlockDamage", 28) }) {
    object packet = Activator.CreateInstance(E("Packet_Client"))!, payload = Activator.CreateInstance(E(kind.Item2))!;
    Set(packet, "Id", kind.Item3); Set(packet, kind.Item1, payload);
    if (kind.Item1 == "BlockDamage") { Set(payload, "PosX", 101); Set(payload, "PosY", 100); Set(payload, "PosZ", 100); Set(payload, "Facing", 0); }
    else { Set(payload, "X", 101); Set(payload, "Y", 100); Set(payload, "Z", 100); }
    if (kind.Item1 == "BlockPlaceOrBreak") {
        Set(payload, "Mode", 1);
        Type block = A("Vintagestory.API.Common.Block");
        ((CallbackProxy)world).Callback = (method, args) => method.Name switch { "get_BlockAccessor" => accessor, "get_Blocks" => Array.CreateInstance(block, 1), _ => throw new Exception("Unexpected world API: " + method.Name) };
    }
    if (kind.Item1 == "HandInteraction") { Set(payload, "EnumHandInteract", 4); Set(payload, "UseType", 0); }
    int prior = reports;
    Check((bool)validatePacket.Invoke(guard, new[] { client, packet })! && reports == prior, "Normal " + kind.Item1 + " failed or generated findings.");
}
Console.WriteLine("PASS: production normal block, block-entity, right-click hand and damage packets; distant/cross-dimension/out-of-world/unloaded targets rejected; missing/non-finite positions safely dropped.");
Console.WriteLine("Pos runtime layout: " + (A("Vintagestory.API.Common.Entities.Entity").GetField("Pos") != null ? "field" : "property") + "; chunk reads=" + reads + "; unexpected load requests=" + loads);

object Proxy(Type type, Func<MethodInfo, object?[]?, object?> callback) { var proxy = DispatchProxy.Create(type, typeof(CallbackProxy)); ((CallbackProxy)proxy).Callback = callback; return proxy; }
public class CallbackProxy : DispatchProxy {
    public Func<MethodInfo, object?[]?, object?> Callback = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Callback(method!, args);
}
