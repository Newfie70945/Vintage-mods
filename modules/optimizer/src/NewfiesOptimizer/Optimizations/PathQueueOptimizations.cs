using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Essentials;

namespace NewfiesOptimizer.Optimizations;

public static class PathQueueOptimizations
{
    private static volatile bool limitEnabled, budgetEnabled;
    private static int budgetMs = 2, maxSearches = 1, graceMs = 300;
    private static ConditionalWeakTable<PathfindingAsync, HeadAge> ages = new();
    private static long workerLimitStops, mainSearches, deferredTicks, mainFailures;
    public static long MainFailures => Interlocked.Read(ref mainFailures);
    public static MethodInfo WorkerTarget => AccessTools.Method(typeof(PathfindingAsync), "ProcessQueue");
    public static MethodInfo MainTarget => AccessTools.Method(typeof(PathfindingAsync), "OnMainThreadTick");
    public static MethodInfo NextTarget => AccessTools.Method(typeof(PathfindingAsync), "Next");
    public static MethodInfo WorkerTranspilerMethod => AccessTools.Method(typeof(PathQueueOptimizations), nameof(WorkerTranspiler));
    public static MethodInfo MainPrefixMethod => AccessTools.Method(typeof(PathQueueOptimizations), nameof(MainPrefix));
    public static MethodInfo[] WorkerDependencies => [WorkerTarget, NextTarget];
    public static MethodInfo[] MainDependencies => [MainTarget, NextTarget];
    public static void ConfigureLimit(bool value) => limitEnabled = value;
    public static void ConfigureBudget(bool value) { budgetEnabled = value; if (!value) Interlocked.Exchange(ref ages, new()); }
    public static void SetBudget(int milliseconds, int maximumSearches, int graceMilliseconds)
    {
        budgetMs = Math.Clamp(milliseconds, 1, 10); maxSearches = Math.Clamp(maximumSearches, 1, 8); graceMs = Math.Clamp(graceMilliseconds, 0, 5000);
    }
    public static (long WorkerLimitStops, long MainSearches, long DeferredTicks) Counters =>
        (Interlocked.Read(ref workerLimitStops), Interlocked.Read(ref mainSearches), Interlocked.Read(ref deferredTicks));
    public static bool ValidateContract(out string reason)
    {
        bool valid = WorkerTarget?.ReturnType == typeof(void) && WorkerTarget.GetParameters().Length == 2 &&
            NextTarget?.ReturnType == typeof(PathfinderTask) && MainTarget?.ReturnType == typeof(void) &&
            AccessTools.Field(typeof(PathfindingAsync), "astar_mainthread")?.FieldType == typeof(AStar) &&
            AccessTools.Field(typeof(PathfindingAsync), "api")?.FieldType == typeof(ICoreServerAPI);
        reason = valid ? "Official path-queue contract verified; A* algorithm retained." : "Path-queue contract differs; retain stock.";
        return valid;
    }
    public static PathfinderTask? TakeNext(PathfindingAsync owner, int remaining)
    {
        // Stock checks this only AFTER dequeuing, abandoning one request at the cap.
        if (limitEnabled && remaining <= 0) { Interlocked.Increment(ref workerLimitStops); return null; }
        return owner.PathfinderTasks.TryDequeue(out var task) ? task : null;
    }
    public static IEnumerable<CodeInstruction> WorkerTranspiler(IEnumerable<CodeInstruction> input)
    {
        var code = input.ToList();
        var calls = code.Select((instruction, index) => (instruction, index)).Where(x => Equals(x.instruction.operand, NextTarget)).ToArray();
        if (calls.Length != 1 || calls[0].instruction.blocks.Count != 0)
            throw new InvalidOperationException("Unexpected queue loop; retain stock.");
        int i = calls[0].index;
        var count = new CodeInstruction(OpCodes.Ldarg_2);
        count.labels.AddRange(code[i].labels);
        code[i] = new(OpCodes.Call, AccessTools.Method(typeof(PathQueueOptimizations), nameof(TakeNext)));
        code.Insert(i, count);
        return code;
    }
    private sealed class HeadAge { public PathfinderTask? Head; public long Seen; }
    public static bool MainPrefix(PathfindingAsync __instance, ICoreServerAPI ___api, AStar ___astar_mainthread, bool ___isShuttingDown)
    {
        if (!budgetEnabled || ___api == null || ___astar_mainthread == null) return true;
        var state = ages.GetValue(__instance, static _ => new());
        if (___isShuttingDown || !__instance.PathfinderTasks.TryPeek(out var head)) { state.Head = null; return false; }
        long now = Stopwatch.GetTimestamp();
        if (!ReferenceEquals(state.Head, head)) { state.Head = head; state.Seen = now; }
        if (Stopwatch.GetElapsedTime(state.Seen, now).TotalMilliseconds < graceMs)
        { Interlocked.Increment(ref deferredTicks); return false; }
        var profile = ___api.World.FrameProfiler;
        long started = Stopwatch.GetTimestamp(); int processed = 0;
        profile.Enter("nos-pathfinding-help");
        try
        {
            while (budgetEnabled && processed < maxSearches && !___isShuttingDown &&
                   Stopwatch.GetElapsedTime(started).TotalMilliseconds < budgetMs &&
                   __instance.PathfinderTasks.TryDequeue(out var task))
            {
                try
                {
                    task.waypoints = ___astar_mainthread.FindPathAsWaypoints(task.startBlockPos, task.targetBlockPos,
                        task.modeMinFleeDistance, task.maxFallHeight, task.stepHeight, task.collisionBox,
                        task.searchDepth, task.mhdistanceTolerance, task.CreatureType);
                }
                catch (Exception error)
                {
                    task.waypoints = null;
                    budgetEnabled = false;
                    Interlocked.Increment(ref mainFailures);
                    ___api.World.Logger.Error("[NewfiesOptimizer] Path request failed ({0}); request completed with no path; assistance disabled until settings are reapplied.", error.GetType().Name);
                }
                finally { task.Finished = true; processed++; Interlocked.Increment(ref mainSearches); }
            }
        }
        finally { state.Head = null; profile.Leave(); }
        // Soft budget: one A* search cannot be interrupted safely and may exceed it.
        return false;
    }
}
