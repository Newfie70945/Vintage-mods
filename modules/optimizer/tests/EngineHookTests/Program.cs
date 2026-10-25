using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using NewfiesOptimizer;
using Vintagestory.Server;

string directory = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? (args.Length > 0 ? args[0] : throw new ArgumentException("Set VINTAGE_STORY to the official 1.22.7 server installation."));
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (string path in new[] { Path.Combine(directory, name.Name + ".dll"), Path.Combine(directory, "Lib", name.Name + ".dll"), Path.Combine(directory, "Mods", name.Name + ".dll") })
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
    return null;
};
EngineTests.Run();

static class EngineTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Run()
    {
        static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        Assert(EngineVersion.Actual == "1.22.7", "Must inspect the loaded official engine version, not a compiled constant.");
        var phases = EngineHookTargets.PhaseMethods();
        Assert(phases.Length == 5 && phases.Select(m => m.Name).Contains("TickEntities"), "All five inspected phase targets must resolve on a dedicated server.");
        Console.WriteLine("PASS five exact engine phase targets");
        var lifecycle = EngineHookTargets.ChunkLifecycleMethods(out var missingLifecycle);
        Assert(missingLifecycle.Length == 0 && lifecycle.Length == 3, "Chunk lifecycle targets must resolve on the inspected build; missing: " + string.Join(", ", missingLifecycle));
        Assert(lifecycle.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(new[] { "SendEntityDespawns", "UnloadChunkColumns", "mainThreadLoadChunkColumn" }), "Unexpected chunk lifecycle target names.");
        Assert(lifecycle.Single(m => m.Name == "mainThreadLoadChunkColumn").GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(ChunkColumnLoadRequest) }), "Column-load completion must take the load request.");
        Assert(phases.Length == 5 && lifecycle.All(l => !phases.Contains(l)), "Chunk lifecycle targets must stay separate from the all-or-nothing phase group.");
        Console.WriteLine("PASS three optional chunk lifecycle phase targets, separate from the five phase targets");
        Assert(RuntimeProfiler.IsCompanionOwner(EngineOptimizations.HarmonyId) && RuntimeProfiler.IsCompanionOwner("newfies.serveressentials.anticheat.oreshield")
            && RuntimeProfiler.IsCompanionOwner("newfiesserveressentialssuite.afk") && !RuntimeProfiler.IsCompanionOwner("steepandbrew.patches")
            && !RuntimeProfiler.IsCompanionOwner("newfies.serveressentialsEVIL") && !RuntimeProfiler.IsCompanionOwner("") && !RuntimeProfiler.IsCompanionOwner(null),
            "Only Newfie's own Harmony owners may coexist with profiler timing hooks.");
        Console.WriteLine("PASS companion Harmony owners are tolerated, unknown owners remain conflicts");
        var callbacks = EngineHookTargets.CallbackMethods();
        Assert(callbacks.Length == 2 && callbacks.All(m => m.Name == "OnTriggered"), "Both official callback wrappers must resolve without scanning unrelated client types.");
        Console.WriteLine("PASS two official callback wrappers");
        var systems = EngineHookTargets.SystemMethodsForTypes(new[] { typeof(ServerSystemEntitySimulation), typeof(ServerSystemAutoSaveGame), typeof(ServerSystemEntitySimulation) });
        Assert(systems.Length == 2 && systems.All(m => m.Name == "OnServerTick"), "Concrete system targets must be deduplicated without Assembly.GetTypes.");
        Console.WriteLine("PASS inspected concrete system target discovery and deduplication");
        Assert(typeof(ServerMain).GetField("Systems", BindingFlags.NonPublic | BindingFlags.Instance)?.FieldType == typeof(ServerSystem[]), "Official instantiated systems field must match the inspected layout.");
        Console.WriteLine("PASS official instantiated systems field layout");
        Console.WriteLine("6 engine metadata regression checks passed. No player simulation, game startup or performance claim is implied.");
    }
}
