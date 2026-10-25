using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Server;

namespace NewfiesOptimizer;

// Discover specific inspected types and instantiated systems, never Assembly.GetTypes():
// the combined engine assembly also contains client-only types unavailable on a dedicated server.
public static class EngineHookTargets
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    public static MethodInfo[] SystemMethods(ServerMain server)
    {
        var field = typeof(ServerMain).GetField("Systems", Instance) ?? throw new MissingFieldException("ServerMain.Systems");
        if (field.GetValue(server) is not ServerSystem[] systems || systems.Length == 0)
            throw new InvalidOperationException("Official main-thread systems are not available.");
        return SystemMethodsForTypes(systems.Select(system => system.GetType()));
    }
    public static MethodInfo[] SystemMethodsForTypes(IEnumerable<Type> types) => types
        .Select(type => Required(type, "OnServerTick", [typeof(float)]))
        .Where(method => !method.IsAbstract && method.DeclaringType != typeof(ServerSystem))
        .Distinct().ToArray();
    public static MethodInfo[] PhaseMethods() => [
        Required(typeof(ServerMain), "ProcessMain", []),
        Required(typeof(ServerSystemEntitySimulation), "TickEntities", [typeof(float)]),
        Required(typeof(CoreServerEventManager), "TriggerGameTick", [typeof(long), typeof(IWorldAccessor)]),
        Required(typeof(CoreServerEventManager), "TriggerGameTickDebug", [typeof(long), typeof(IWorldAccessor)]),
        Required(typeof(ServerSystemAutoSaveGame), "doAutoSave", [])
    ];
    // Main-thread chunk lifecycle stages that PhaseMethods() does not time: completing a loaded or
    // generated column, unloading columns, and sending entity despawns. PhaseMethods() is
    // all-or-nothing, so these are separate and best effort: a method that is missing or reshaped
    // on some build is reported in `missing` and skipped instead of failing the existing group.
    // Other modules in this repository already patch or validate each of these methods, so their
    // names are known on the supported 1.22.7 build.
    public static MethodInfo[] ChunkLifecycleMethods(out string[] missing)
    {
        var found = new List<MethodInfo>();
        var absent = new List<string>();
        void Add(string typeName, string method, Type[]? parameters)
        {
            try
            {
                var type = typeof(ServerMain).Assembly.GetType("Vintagestory.Server." + typeName, throwOnError: false);
                var target = type is null ? null : parameters is null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, parameters);
                if (target is not null && !target.IsAbstract) found.Add(target);
                else absent.Add(typeName + "." + method);
            }
            catch (Exception) { absent.Add(typeName + "." + method); }
        }
        Add("ServerSystemSupplyChunks", "mainThreadLoadChunkColumn", [typeof(ChunkColumnLoadRequest)]);
        Add("ServerSystemUnloadChunks", "UnloadChunkColumns", null);
        Add("ServerSystemEntitySimulation", "SendEntityDespawns", null);
        missing = absent.ToArray();
        return found.ToArray();
    }
    public static MethodInfo[] CallbackMethods() => new[] { "Vintagestory.Common.GameTickListener", "Vintagestory.Common.GameTickListenerBlock" }
        .Select(name => typeof(ServerMain).Assembly.GetType(name, throwOnError: true)!)
        .Select(type => type.GetMethod("OnTriggered", Instance) ?? throw new MissingMethodException(type.FullName, "OnTriggered")).ToArray();
    private static MethodInfo Required(Type type, string name, Type[] parameters) =>
        type.GetMethod(name, Instance, parameters) ?? throw new MissingMethodException(type.FullName, name);
}
