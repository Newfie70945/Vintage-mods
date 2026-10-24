using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace NewfiesOptimizer.Optimizations;

// Reuse only the stock nearest-search closure/delegate. Partition traversal,
// distance semantics, tie order and predicate side effects remain unchanged.
public static class EntitySearchOptimizations
{
    private static volatile bool nearestEnabled, interactableEnabled;
    private static long generation, nearestCalls, interactableCalls, created;
    public const int MaximumCachedContexts = 4;
    [ThreadStatic] private static Search? idle;
    [ThreadStatic] private static Search?[]? spares;
    [ThreadStatic] private static int spareCount;
    public static int CurrentThreadCachedContexts => (idle==null?0:1)+spareCount;
    public static MethodInfo NearestTarget => AccessTools.Method(typeof(EntityPartitioning), "GetNearestEntity",
        [typeof(Vec3d), typeof(double), typeof(ActionConsumable<Entity>), typeof(EnumEntitySearchType)]);
    public static MethodInfo InteractableTarget => AccessTools.Method(typeof(EntityPartitioning), "GetNearestInteractableEntity",
        [typeof(Vec3d), typeof(double), typeof(ActionConsumable<Entity>)]);
    public static MethodInfo WalkTarget => AccessTools.Method(typeof(EntityPartitioning), "WalkEntities",
        [typeof(double), typeof(double), typeof(double), typeof(double), typeof(ActionConsumable<Entity>), typeof(EntityPartitioning.RangeTestDelegate), typeof(EnumEntitySearchType)]);
    public static MethodInfo NearestPrefixMethod => AccessTools.Method(typeof(EntitySearchOptimizations), nameof(NearestPrefix));
    public static MethodInfo InteractablePrefixMethod => AccessTools.Method(typeof(EntitySearchOptimizations), nameof(InteractablePrefix));
    public static MethodInfo[] NearestDependencies => [NearestTarget, WalkTarget];
    public static MethodInfo[] InteractableDependencies => [InteractableTarget, NearestTarget, WalkTarget];

    public static bool ValidateContract(out string reason)
    {
        bool valid = NearestTarget?.ReturnType == typeof(Entity) && InteractableTarget?.ReturnType == typeof(Entity) &&
            WalkTarget?.ReturnType == typeof(void) &&
            AccessTools.Field(typeof(EntityPartitioning), "api")?.FieldType == typeof(ICoreAPI);
        reason = valid ? "Official entity search signatures verified; stock partition walker retained." : "Entity search contract differs; retain stock.";
        return valid;
    }

    public static void ConfigureNearest(bool value) { nearestEnabled = value; if (!value) InvalidateScratch(); }
    public static void ConfigureInteractable(bool value) { interactableEnabled = value; if (!value) InvalidateScratch(); }
    private static void InvalidateScratch() { Interlocked.Increment(ref generation); idle = null; spares=null;spareCount=0; }
    public static (long NearestCalls, long InteractableCalls, long ContextsCreated) Counters =>
        (Interlocked.Read(ref nearestCalls), Interlocked.Read(ref interactableCalls), Interlocked.Read(ref created));
    public static bool CurrentThreadRetainsInputs
    {
        get
        {
            if(idle!=null&&(idle.Position!=null||idle.Matches!=null||idle.Nearest!=null))return true;
            for(int i=0;i<spareCount;i++){var state=spares![i]!;if(state.Position!=null||state.Matches!=null||state.Nearest!=null)return true;}
            return false;
        }
    }
    private static Search? RentSpare(long epoch)
    {
        if(spareCount==0)return null;
        var state=spares![--spareCount]!;spares[spareCount]=null;
        if(state.Generation==epoch)return state;
        spares=null;spareCount=0;return null;
    }
    private static void KeepSpare(Search state)
    {
        if(spareCount>=MaximumCachedContexts-1)return;
        spares??=new Search?[MaximumCachedContexts-1];spares[spareCount++]=state;
    }

    public static bool NearestPrefix(EntityPartitioning __instance, Vec3d position, double radius,
        ActionConsumable<Entity>? matches, EnumEntitySearchType searchType, ICoreAPI ___api, ref Entity? __result)
    {
        if (!nearestEnabled || ___api is not ICoreServerAPI) return true;
        Interlocked.Increment(ref nearestCalls);
        __result = Find(__instance, position, radius, matches, searchType, false);
        return false;
    }
    public static bool InteractablePrefix(EntityPartitioning __instance, Vec3d position, double radius,
        ActionConsumable<Entity>? matches, ICoreAPI ___api, ref Entity? __result)
    {
        if (!interactableEnabled || ___api is not ICoreServerAPI) return true;
        Interlocked.Increment(ref interactableCalls);
        __result = Find(__instance, position, radius, matches, EnumEntitySearchType.Creatures, true);
        return false;
    }

    private static Entity? Find(EntityPartitioning owner, Vec3d position, double radius,
        ActionConsumable<Entity>? matches, EnumEntitySearchType kind, bool interactable)
    {
        long epoch = Volatile.Read(ref generation);
        var state = idle;
        idle = null; // Recursive queries check out a different context.
        if (state == null || state.Generation != epoch)
        {
            if(state!=null){spares=null;spareCount=0;}
            state=RentSpare(epoch);
            if(state==null){state = new Search(epoch);Interlocked.Increment(ref created);}
        }
        state.Position = position; state.Matches = matches;
        state.DistanceSq = radius * radius; state.Interactable = interactable;
        try
        {
            // Preserve the stock server branch, including predicates being called
            // only for a nearer candidate and its original strict comparison.
            owner.WalkEntities(position.X, position.Y, position.Z, radius, state.Visit, null!, kind);
            return state.Nearest;
        }
        finally
        {
            state.Position = null; state.Matches = null; state.Nearest = null;
            if (Volatile.Read(ref generation) == epoch)
            { if(idle==null)idle=state;else KeepSpare(state); }
        }
    }

    private sealed class Search
    {
        public readonly long Generation;
        public readonly ActionConsumable<Entity> Visit;
        public Vec3d? Position;
        public ActionConsumable<Entity>? Matches;
        public Entity? Nearest;
        public double DistanceSq;
        public bool Interactable;
        public Search(long generation) { Generation = generation; Visit = Check; }
        private bool Check(Entity entity)
        {
            double distance = entity.Pos.SquareDistanceTo(Position!);
            if (distance < DistanceSq && (Interactable
                ? (Matches == null || Matches(entity)) && entity.IsInteractable
                : Matches!(entity)))
            {
                DistanceSq = distance; Nearest = entity;
            }
            return true;
        }
    }
}
