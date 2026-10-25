using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

// Enumerate an immutable delegate snapshot without allocating an invocation array
// or an entity-capturing Action. Never skip, defer or repeat subscriber work.
public static class EntityEventDispatchOptimizations
{
    private static readonly bool[] enabled = new bool[2];
    private static readonly long[] calls = new long[2], failures = new long[2];
    private sealed class Label(Delegate callback) { public readonly string Text = "nos-entity-event:" + callback.Method.DeclaringType?.FullName + "." + callback.Method.Name; }
    private static readonly ConditionalWeakTable<Delegate, Label> labels = new();
    public static MethodInfo Target(int kind) => AccessTools.Method(typeof(ServerEventManager), kind == 0 ? "TriggerEntityLoaded" : "TriggerEntityDespawned");
    public static MethodBase[] Dependencies(int kind) => [Target(kind), PatchProcessor.GetOriginalInstructions(Target(kind)).Select(i => i.operand).OfType<MethodInfo>().Single(m => m.Name == "Trigger" && m.IsGenericMethod)];
    public static void Configure(int kind, bool value) => Volatile.Write(ref enabled[kind], value);
    public static (long Calls,long Failures) Counters(int kind) => (Interlocked.Read(ref calls[kind]),Interlocked.Read(ref failures[kind]));
    public static bool ValidateContract(int kind, out string reason)
    {
        var target = Target(kind);
        var field = AccessTools.Field(typeof(ServerEventManager),kind == 0 ? "OnEntityLoaded" : "OnEntityDespawn");
        var code = PatchProcessor.GetOriginalInstructions(target);
        bool valid = target.ReturnType == typeof(void) && field?.FieldType == (kind == 0 ? typeof(EntityDelegate) : typeof(EntityDespawnDelegate))
            && target.GetParameters().Select(p => p.ParameterType).SequenceEqual(kind == 0 ? [typeof(Entity)] : new[] {typeof(Entity),typeof(EntityDespawnData)})
            && code.Count(i => Equals(i.operand, field)) == 1
            && code.Count(i => i.operand is MethodInfo m && m.Name == nameof(Delegate.GetInvocationList)) == 1
            && code.Count(i => i.operand is MethodInfo m && m.Name == "Trigger" && m.IsGenericMethod) == 1;
        reason = valid ? "Official entity event dispatch verified; subscriber snapshot, order and catch/log preserved." : "Entity event dispatch differs; retain stock.";
        return valid;
    }
    private static bool Trace => Environment.CurrentManagedThreadId == RuntimeEnv.ServerMainThreadId && ServerMain.FrameProfiler?.Enabled == true;
    private static void Begin(Delegate callback, bool trace)
    {
        if (trace) ServerMain.FrameProfiler.Enter(labels.GetValue(callback, static d => new Label(d)).Text);
    }
    private static void End(bool trace) { if (trace) ServerMain.FrameProfiler.Leave(); }
    public static bool LoadedPrefix(ServerEventManager __instance, Entity __0, EntityDelegate? ___OnEntityLoaded)
    {
        if (!Volatile.Read(ref enabled[0])) return true;
        Interlocked.Increment(ref calls[0]);
        var snapshot = ___OnEntityLoaded;
        if (snapshot == null) return false;
        bool trace = Trace;
        foreach (var callback in Delegate.EnumerateInvocationList(snapshot))
        {
            Begin(callback, trace);
            try { callback(__0); }
            catch (Exception e) { __instance.Logger.Error("Mod exception during event OnEntityLoaded. Will skip to next event"); __instance.Logger.Error(e); }
            finally { End(trace); }
        }
        return false;
    }
    public static bool DespawnPrefix(ServerEventManager __instance, Entity __0, EntityDespawnData __1, EntityDespawnDelegate? ___OnEntityDespawn)
    {
        if (!Volatile.Read(ref enabled[1])) return true;
        Interlocked.Increment(ref calls[1]);
        var snapshot = ___OnEntityDespawn;
        if (snapshot == null) return false;
        bool trace = Trace;
        foreach (var callback in Delegate.EnumerateInvocationList(snapshot))
        {
            Begin(callback, trace);
            try { callback(__0,__1); }
            catch (Exception e) { __instance.Logger.Error("Mod exception during event OnEntityDespawned. Will skip to next event"); __instance.Logger.Error(e); }
            finally { End(trace); }
        }
        return false;
    }
    private static Exception? Finish(Exception? error,int kind)
    {
        // Subscriber exceptions were handled just as stock. Infrastructure failures
        // disable future optimized dispatch and propagate; never replay cleanup.
        if (error != null && Volatile.Read(ref enabled[kind])) { Volatile.Write(ref enabled[kind],false); Interlocked.Increment(ref failures[kind]); }
        return error;
    }
    public static Exception? LoadedFinalizer(Exception? __exception) => Finish(__exception,0);
    public static Exception? DespawnFinalizer(Exception? __exception) => Finish(__exception,1);
}
