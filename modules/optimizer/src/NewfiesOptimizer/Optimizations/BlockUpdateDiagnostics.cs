using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

// Diagnostics only. The 100 ms block-update listener drains every changed-block
// queue without a cap, and the frame profiler reports it as one opaque entry.
// When one pass exceeds the threshold, log how much work was queued beforehand and
// how the time split between changed-block handling and block-entity sync.
// Observational prefixes and finalizers: native work, order and exceptions are unchanged.
public static class BlockUpdateDiagnostics
{
    private static volatile bool enabled;
    private static int thresholdMs = 50;
    private static long passes, slow, lastLogTicks;
    [ThreadStatic] private static double handleMs;
    // Resolved on first guarded use, not when the registry switches modules off.
    private static class Native { internal static readonly AccessTools.FieldRef<ServerSystem, ServerMain> Server = AccessTools.FieldRefAccess<ServerSystem, ServerMain>("server"); }
    public struct Pass { internal bool Active; internal long Start; internal int Updated, Dirty, Modified, NoRelight, Minimal, Decors, BlockEntities; }
    public struct Timing { internal long Start; }
    public static MethodInfo Target => AccessTools.Method(typeof(ServerSystemBlockSimulation), "UpdateEvery100ms");
    public static MethodInfo HandleTarget => AccessTools.Method(typeof(ServerSystemBlockSimulation), "HandleDirtyAndUpdatedBlocks");
    public static MethodBase[] Dependencies => [Target];
    public static (long Passes, long Slow) Counters => (Interlocked.Read(ref passes), Interlocked.Read(ref slow));
    public static void Configure(bool value) => enabled = value;
    public static void SetThreshold(int milliseconds) => Volatile.Write(ref thresholdMs, Math.Clamp(milliseconds, 10, 1000));
    public static Action<string>? Sink;
    public static bool ValidateContract(out string reason)
    {
        bool valid = Target?.ReturnType == typeof(void) && Target.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(float) }) &&
            HandleTarget?.ReturnType == typeof(void) && HandleTarget.GetParameters().Length == 0 &&
            new[] { "UpdatedBlocks", "DirtyBlocks", "ModifiedBlocks", "ModifiedBlocksNoRelight", "ModifiedBlocksMinimal", "ModifiedDecors", "DirtyBlockEntities" }.All(f => AccessTools.Field(typeof(ServerMain), f) != null) && AccessTools.Field(typeof(ServerSystem), "server")?.FieldType == typeof(ServerMain);
        reason = valid ? "Official 100 ms block-update listener verified; observation only." : "Block-update listener layout differs; retain stock.";
        return valid;
    }
    public static void Prefix(ServerSystemBlockSimulation __instance, out Pass __state)
    {
        __state = default;
        if (!enabled) return;
        var server = Native.Server(__instance);
        if (server == null) return;
        handleMs = 0;
        __state = new Pass
        {
            Active = true, Start = Stopwatch.GetTimestamp(),
            Updated = server.UpdatedBlocks.Count, Dirty = server.DirtyBlocks.Count, Modified = server.ModifiedBlocks.Count,
            NoRelight = server.ModifiedBlocksNoRelight.Count, Minimal = server.ModifiedBlocksMinimal.Count,
            Decors = server.ModifiedDecors.Count, BlockEntities = server.DirtyBlockEntities.Count
        };
    }
    public static Exception? Finalizer(Exception? __exception, Pass __state)
    {
        if (!__state.Active) return __exception;
        Interlocked.Increment(ref passes);
        double total = Stopwatch.GetElapsedTime(__state.Start).TotalMilliseconds;
        if (total < Volatile.Read(ref thresholdMs)) return __exception;
        Interlocked.Increment(ref slow);
        // At most one line every 10 s; the counter still records every slow pass.
        long now = Environment.TickCount64, last = Interlocked.Read(ref lastLogTicks);
        if (now - last < 10000 || Interlocked.CompareExchange(ref lastLogTicks, now, last) != last) return __exception;
        string line = string.Format(CultureInfo.InvariantCulture,
            "[NewfiesOptimizer] Slow 100 ms block update: {0:F1} ms (changed-block handling {1:F1} ms, block-entity sync {2:F1} ms). Queued before: neighbour updates {3}, resend {4}, modified {5}, modified without relight {6}, minimal {7}, decors {8}, dirty block entities {9}.{10}",
            total, handleMs, Math.Max(0, total - handleMs), __state.Updated, __state.Dirty, __state.Modified, __state.NoRelight, __state.Minimal, __state.Decors, __state.BlockEntities,
            __exception != null ? " The pass threw " + __exception.GetType().Name + "." : "");
        try { (Sink ?? (text => ServerMain.Logger?.Warning(text)))(line); } catch { /* Diagnostics never throw into gameplay. */ }
        return __exception;
    }
    public static void HandlePrefix(out Timing __state) => __state = new Timing { Start = enabled ? Stopwatch.GetTimestamp() : 0 };
    public static Exception? HandleFinalizer(Exception? __exception, Timing __state)
    {
        if (__state.Start != 0) handleMs = Stopwatch.GetElapsedTime(__state.Start).TotalMilliseconds;
        return __exception;
    }
}
