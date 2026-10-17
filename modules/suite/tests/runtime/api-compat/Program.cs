using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BlockLogger.Admin;
using BlockLogger.AntiCheat;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY.");
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (string root in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods"), Path.GetDirectoryName(Environment.GetEnvironmentVariable("NSS_ASSEMBLY")) ?? game }) {
        string path = Path.Combine(root, name.Name + ".dll");
        if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
    }
    return null;
};
Run(game);

[MethodImpl(MethodImplOptions.NoInlining)] static void Run(string game)
{
    void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    Check(PacketSelectionIdReader<OldPacket>.Read(new()) == null, "Missing optional packet ID must be accepted.");
    var field = new FieldPacket { SelectionBoxId = new string('x', 257) };
    Check(PacketSelectionIdReader<FieldPacket>.Read(field)?.Length == 257, "Oversized field ID must reach length validation.");
    field.SelectionBoxId = "face";
    Check(PacketSelectionIdReader<FieldPacket>.Read(field) == "face", "Cached reader retained a previous value.");
    Check(PacketSelectionIdReader<PropertyPacket>.Read(new() { SelectionBoxId = "prop" }) == "prop", "Property packet layout unsupported.");
    Check(PacketSelectionIdReader<WrongPacket>.Read(new()) == null, "Unsupported member type should not be coerced.");
    Console.WriteLine("PASS: optional packet IDs support absent, field and property layouts; oversized IDs remain visible to guards.");

    string temporary = Path.Combine(Path.GetTempPath(), "nss-api-compat-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(temporary, "Saves"));
    string world = Path.Combine(temporary, "Saves", "active.vcdbs");
    File.WriteAllText(world, "read-only-fixture");
    try {
        Check(WorldSavePathResolver.Resolve(new ModernWorld { CurrentWorldFilepath = world }, temporary) == world, "Absolute active path failed.");
        foreach (string name in new[] { "active", "active.vcdbs", "Saves/active.vcdbs", "data/Saves/active.vcdbs" })
            Check(WorldSavePathResolver.Resolve(new LegacyWorld { CurrentWorldName = name }, temporary) == world, "Legacy path failed: " + name);
        Check(WorldSavePathResolver.Resolve(new ExplicitWorld(world), temporary) == world, "Explicit interface path failed.");
        try { WorldSavePathResolver.Resolve(new LegacyWorld { CurrentWorldName = "missing" }, temporary); throw new Exception("Missing save accepted."); }
        catch (FileNotFoundException) { }
        Check(File.ReadAllText(world) == "read-only-fixture" && Directory.GetFiles(Path.Combine(temporary, "Saves")).Length == 1, "Resolver modified or created files.");
        Console.WriteLine("PASS: modern, legacy and explicit-interface active-save paths resolve without creating or modifying databases.");
    } finally { Directory.Delete(temporary, true); }

    var library = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryLib.dll"));
    var runtimeConstructor = DetachedBackpackInventoryFactory.ResolveConstructor(new[] { library });
    Check(runtimeConstructor != null, "Actual runtime backpack implementation unavailable.");
    var backpack = DetachedBackpackInventoryFactory.Create("backpack-fixture", null!);
    var tree = new TreeAttribute();
    tree.SetInt("qslots", 4); tree["slots"] = new TreeAttribute();
    backpack.FromTreeAttributes(tree);
    Check(backpack.Count == 4 && Enumerable.Range(0, 4).All(i => backpack[i] != null), "Detached native bag slots not exposed through the base inventory.");
    backpack.AfterBlocksLoaded(null!);
    Check(backpack.Count == 4, "Empty bag expansion changed slots.");

    var fixture = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("LegacyBackpackFixture"), AssemblyBuilderAccess.Run);
    var module = fixture.DefineDynamicModule("fixture");
    // The former namespace contains an unrelated type: it must not be accepted.
    module.DefineType("Vintagestory.Common.InventoryPlayerBackpacks", TypeAttributes.Public).CreateType();
    var type = module.DefineType("Vintagestory.GameContent.InventoryPlayerBackpacks", TypeAttributes.Public, typeof(InventoryGeneric));
    var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(string), typeof(ICoreAPI) });
    var il = constructor.GetILGenerator();
    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_2);
    il.Emit(OpCodes.Call, typeof(InventoryGeneric).GetConstructor(new[] { typeof(ICoreAPI) })!); il.Emit(OpCodes.Ret);
    var legacyType = type.CreateType()!;
    var legacy = DetachedBackpackInventoryFactory.ResolveConstructor(new[] { fixture });
    Check(legacy?.DeclaringType == legacyType && legacy.Invoke(new object?[] { "backpack-legacy", null }) is InventoryBase, "Alternate backpack namespace failed.");
    Check(DetachedBackpackInventoryFactory.ResolveConstructor(Array.Empty<Assembly>()) == null, "Missing backpack implementation fabricated.");
    Console.WriteLine("PASS: actual detached backpack slots and alternate namespace resolution; unrelated and unavailable implementations rejected.");

    string modPath = Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? throw new Exception("Set NSS_ASSEMBLY to the production DLL.");
    var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(modPath));
    var virtualizerType = mod.GetType("BlockLogger.AntiCheat.OreShieldBlockVirtualizer", true)!;
    var serverType = library.GetType("Vintagestory.Server.ServerMain", true)!;
    var mapType = library.GetType("Vintagestory.Server.ServerWorldMap", true)!;
    object server = RuntimeHelpers.GetUninitializedObject(serverType), map = RuntimeHelpers.GetUninitializedObject(mapType);
    var baseMap = mapType.BaseType!;
    baseMap.GetField("index3dMulX", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(map, 500);
    baseMap.GetField("index3dMulZ", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(map, 700);
    serverType.GetField("WorldMap")!.SetValue(server, map);
    object virtualizer = RuntimeHelpers.GetUninitializedObject(virtualizerType);
    virtualizerType.GetField("server", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(virtualizer, server);
    var index = virtualizerType.GetMethod("ChunkIndex3D", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var keyType = virtualizerType.GetNestedType("ChunkKey", BindingFlags.Public | BindingFlags.NonPublic)!;
    int size = Vintagestory.API.Config.GlobalConstants.DimensionSizeInChunks;
    foreach (int y in new[] { 0, 31, size, size + 12, -1, -size, -size - 3 }) {
        var key = Activator.CreateInstance(keyType, 11, y, 23)!;
        long actual = (long)index.Invoke(virtualizer, new[] { key })!;
        long expected = ((long)y * 700 + 23) * 500 + 11;
        Check(actual == expected, "Chunk index changed for internal Y " + y);
    }
    Console.WriteLine("PASS: production OreShield chunk indices match the previous contract in primary, positive and negative dimensions.");
}

public class OldPacket { public int SelectionBoxIndex; }
public class FieldPacket { public string? SelectionBoxId; }
public class PropertyPacket { public string? SelectionBoxId { get; set; } }
public class WrongPacket { public int SelectionBoxId; }
public class ModernWorld { public string CurrentWorldFilepath { get; set; } = ""; }
public class LegacyWorld { public string CurrentWorldName { get; set; } = ""; }
public interface IExplicitWorld { string CurrentWorldFilepath { get; } }
public class ExplicitWorld(string path) : IExplicitWorld { string IExplicitWorld.CurrentWorldFilepath => path; }
