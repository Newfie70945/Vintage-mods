using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VSEssentialsMod.Entity.AI.Task;

namespace NewfiesOptimizer.Optimizations;

// Only the per-search captured predicate is reused. Every query and mutable state
// field is read afresh; no nearby-entity result or behavior decision is cached.
public static class EmotionSearchOptimizations
{
    private static volatile bool enabled;
    private static ConditionalWeakTable<EmotionState, Callback> callbacks = new();
    private static long calls, created, failures;
    public static MethodInfo Target => AccessTools.Method(typeof(EntityBehaviorEmotionStates), "entitiesNearby");
    public static MethodInfo Search => AccessTools.Method(typeof(EntityPartitioning), "GetNearestEntity", [typeof(Vec3d), typeof(double), typeof(ActionConsumable<Entity>), typeof(EnumEntitySearchType)]);
    private delegate Entity NearestQuery(EntityPartitioning partition,Vec3d position,double range,ActionConsumable<Entity> matches,EnumEntitySearchType kind);
    // Bind the native entry point so JIT inlining cannot bake an unpatched nearest
    // search into this prefix before another NOS module is enabled at runtime.
    // Engine members resolve on first guarded use, not when the registry switches modules off.
    private static class Native
    {
        internal static readonly AccessTools.FieldRef<EntityBehavior, Entity> Owner = AccessTools.FieldRefAccess<EntityBehavior, Entity>("entity");
        internal static readonly AccessTools.FieldRef<EntityBehaviorEmotionStates, EntityPartitioning> Partition = AccessTools.FieldRefAccess<EntityBehaviorEmotionStates, EntityPartitioning>("epartSys");
        internal static readonly NearestQuery Query=Search.CreateDelegate<NearestQuery>();
    }
    public static MethodBase[] Dependencies => [Target, Search];
    public static (long Calls,long Created,long Failures) Counters => (Interlocked.Read(ref calls),Interlocked.Read(ref created),Interlocked.Read(ref failures));
    public static void Configure(bool value) { enabled=value; if(!value) Interlocked.Exchange(ref callbacks,new()); }
    public static bool ValidateContract(out string reason)
    {
        bool valid=Target?.ReturnType==typeof(bool) && Target.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(EmotionState)}) && Search!=null;
        reason=valid?"Official emotion search verified; native query, range and live state retained.":"Emotion search contract differs; retain stock.";
        return valid;
    }
    public sealed class Callback
    {
        private readonly EmotionState state;
        public readonly ActionConsumable<Entity> Predicate;
        public Callback(EmotionState state) { this.state=state; Predicate=Matches; Interlocked.Increment(ref created); }
        private bool Matches(Entity candidate)
        {
            if(state.EntityCodeLocs==null)return false;
            for(int i=0;i<state.EntityCodeLocs.Length;i++)
                if(state.EntityCodeLocs[i].Equals(candidate.Code))return candidate.IsInteractable;
            return false;
        }
    }
    public static ActionConsumable<Entity> Predicate(EmotionState state) => callbacks.TryGetValue(state,out var value)?value.Predicate:callbacks.GetValue(state,static s=>new Callback(s)).Predicate;
    public static bool Prefix(EntityBehaviorEmotionStates __instance, EmotionState newstate, ref bool __result, out bool __state)
    {
        __state=enabled;
        if(!__state || __instance.GetType()!=typeof(EntityBehaviorEmotionStates) || newstate==null)return true;
        Interlocked.Increment(ref calls);
        // Keep native XYZ ownership: partition/mod code can retain this fresh vector.
        __result=Native.Query(Native.Partition(__instance),Native.Owner(__instance).Pos.XYZ,newstate.NotifyRange,Predicate(newstate),EnumEntitySearchType.Creatures)!=null;
        return false;
    }
    public static Exception? Finalizer(Exception? __exception,bool __state)
    {
        if(__exception!=null&&__state){Interlocked.Increment(ref failures);Configure(false);}
        return __exception; // Never replay a query after a throwing mod callback.
    }
}
