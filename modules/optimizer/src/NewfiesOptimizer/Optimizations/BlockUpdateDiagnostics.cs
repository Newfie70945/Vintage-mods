using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

// Observation only. Several changed-block queues are drained without a cap, while
// neighbour notifications retain their native 500-entry limit. Never change either.
public static class BlockUpdateDiagnostics
{
    private static volatile bool enabled, handleEnabled, syncEnabled;
    private static int thresholdMs = 50;
    private static long passes, slow, failures, lastLogTicks;
    [ThreadStatic] private static double handleMs, syncMs;
    [ThreadStatic] private static bool handleMeasured, syncMeasured;
    [ThreadStatic] private static int depth;
    private static class Native { internal static readonly AccessTools.FieldRef<ServerSystem, ServerMain> Server = AccessTools.FieldRefAccess<ServerSystem, ServerMain>("server"); }
    public struct Pass
    {
        internal bool Active, PreviousHandleMeasured, PreviousSyncMeasured;
        internal long Start;
        internal double PreviousHandle, PreviousSync;
        internal int PreviousDepth, Updated, Dirty, Modified, NoRelight, Minimal, Decors, BlockEntities;
    }
    public struct Timing { internal long Start; internal int Depth; }
    public static MethodInfo Target => AccessTools.Method(typeof(ServerSystemBlockSimulation), "UpdateEvery100ms");
    public static MethodInfo HandleTarget => AccessTools.Method(typeof(ServerSystemBlockSimulation), "HandleDirtyAndUpdatedBlocks");
    public static MethodInfo SyncTarget => AccessTools.Method(typeof(ServerSystemBlockSimulation), "SendDirtyBlockEntities");
    public static MethodBase[] Dependencies => [Target];
    public static (long Passes, long Slow, long Failures) Counters => (Interlocked.Read(ref passes), Interlocked.Read(ref slow), Interlocked.Read(ref failures));
    public static void Configure(bool value) { enabled = value; if(value) Interlocked.Exchange(ref lastLogTicks, 0); }
    public static void ConfigureHandle(bool value) => handleEnabled = value;
    public static void ConfigureSync(bool value) => syncEnabled = value;
    public static void SetThreshold(int milliseconds) => Volatile.Write(ref thresholdMs, Math.Clamp(milliseconds, 10, 1000));
    public static Action<string>? Sink;
    public static bool ValidateContract(out string reason)
    {
        bool valid = Target?.ReturnType == typeof(void) && Target.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(float) }) &&
            new[] { HandleTarget, SyncTarget }.All(m => m?.ReturnType == typeof(void) && m.GetParameters().Length == 0) &&
            new[] { "UpdatedBlocks", "DirtyBlocks", "ModifiedBlocks", "ModifiedBlocksNoRelight", "ModifiedBlocksMinimal", "ModifiedDecors", "DirtyBlockEntities" }.All(f => AccessTools.Field(typeof(ServerMain), f) != null) && AccessTools.Field(typeof(ServerSystem), "server")?.FieldType == typeof(ServerMain);
        reason = valid ? "Official block-update listener layout verified; elapsed observations, not exclusive CPU attribution." : "Block-update listener layout differs; retain stock.";
        return valid;
    }
    private static void ObserverFailed() { Interlocked.Increment(ref failures); enabled = false; }
    public static void Prefix(ServerSystemBlockSimulation __instance, out Pass __state)
    {
        __state = default;
        if (!enabled) return;
        try
        {
            var server = Native.Server(__instance);
            if (server == null) return;
            var state = new Pass
            {
                Start = Stopwatch.GetTimestamp(), PreviousDepth = depth,
                PreviousHandle = handleMs, PreviousSync = syncMs,
                PreviousHandleMeasured = handleMeasured, PreviousSyncMeasured = syncMeasured,
                Updated = server.UpdatedBlocks.Count, Dirty = server.DirtyBlocks.Count, Modified = server.ModifiedBlocks.Count,
                NoRelight = server.ModifiedBlocksNoRelight.Count, Minimal = server.ModifiedBlocksMinimal.Count,
                Decors = server.ModifiedDecors.Count, BlockEntities = server.DirtyBlockEntities.Count
            };
            handleMs = syncMs = 0; handleMeasured = syncMeasured = false; depth++;
            state.Active = true; __state = state;
        }
        catch { ObserverFailed(); } // A diagnostic read must never prevent the native callback.
    }
    public static Exception? Finalizer(Exception? __exception, Pass __state)
    {
        if (!__state.Active) return __exception;
        try
        {
            Interlocked.Increment(ref passes);
            double total = Stopwatch.GetElapsedTime(__state.Start).TotalMilliseconds;
            if (total < Volatile.Read(ref thresholdMs)) return __exception;
            Interlocked.Increment(ref slow);
            long now = Environment.TickCount64, last = Interlocked.Read(ref lastLogTicks);
            if ((last != 0 && now - last < 10000) || Interlocked.CompareExchange(ref lastLogTicks, now, last) != last) return __exception;
            string Format(double ms, bool measured) => measured ? ms.ToString("F1", CultureInfo.InvariantCulture) + " ms" : "unavailable";
            string line = string.Format(CultureInfo.InvariantCulture,
                "[NewfiesOptimizer] Slow 100 ms block update: {0:F1} ms (changed-block handling {1}, block-entity sync {2}, remaining/unmeasured {3:F1} ms). Queued before: neighbour updates {4}, resend {5}, modified {6}, modified without relight {7}, minimal {8}, decors {9}, dirty block entities {10}. Elapsed timings include pauses/waits; they do not identify a mod or prove a cause.{11}",
                total, Format(handleMs, handleMeasured), Format(syncMs, syncMeasured), Math.Max(0, total - handleMs - syncMs),
                __state.Updated, __state.Dirty, __state.Modified, __state.NoRelight, __state.Minimal, __state.Decors, __state.BlockEntities,
                __exception != null ? " The pass threw " + __exception.GetType().Name + "." : "");
            if (Sink is { } sink) sink(line); else ServerMain.Logger?.Warning(line);
        }
        catch { ObserverFailed(); }
        finally
        {
            handleMs = __state.PreviousHandle; syncMs = __state.PreviousSync;
            handleMeasured = __state.PreviousHandleMeasured; syncMeasured = __state.PreviousSyncMeasured;
            depth = __state.PreviousDepth;
        }
        return __exception; // Preserve the original exception, including when the observer failed.
    }
    private static Timing Begin(bool targetEnabled) => new() { Start = enabled && targetEnabled && depth > 0 ? Stopwatch.GetTimestamp() : 0, Depth = depth };
    public static void HandlePrefix(out Timing __state) => __state = Begin(handleEnabled);
    public static void SyncPrefix(out Timing __state) => __state = Begin(syncEnabled);
    private static void End(Timing state, bool sync)
    {
        if (state.Start == 0 || state.Depth != depth) return;
        try
        {
            double elapsed = Stopwatch.GetElapsedTime(state.Start).TotalMilliseconds;
            if (sync) { syncMs += elapsed; syncMeasured = true; }
            else { handleMs += elapsed; handleMeasured = true; }
        }
        catch { ObserverFailed(); }
    }
    public static Exception? HandleFinalizer(Exception? __exception, Timing __state) { End(__state, false); return __exception; }
    public static Exception? SyncFinalizer(Exception? __exception, Timing __state) { End(__state, true); return __exception; }
}
