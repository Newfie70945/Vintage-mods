using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.API.Common;

namespace NewfiesOptimizer.Optimizations;

// Preserve every AI veto and its ordering; avoid GetInvocationList's per-check array.
public static class AiFilterOptimizations
{
    private static volatile bool enabled;
    public static MethodInfo Target => AccessTools.Method(typeof(AiTaskManager), "ShouldExecuteTask");
        public static void Configure(bool value) => enabled=value;
    public static bool ValidateContract(out string reason)
    {
        bool valid=Target?.ReturnType==typeof(bool) && AccessTools.Field(typeof(AiTaskManager), "OnShouldExecuteTask")?.FieldType==typeof(ActionBoolReturn<IAiTask>);
        reason=valid?"Official AI filter delegate contract verified.":"AI filter contract differs.";return valid;
    }
    public static MethodInfo TranspilerMethod=>AccessTools.Method(typeof(AiFilterOptimizations),nameof(Transpiler));
    public static bool Evaluate(IAiTask task,ActionBoolReturn<IAiTask>? filters)
    {
        if(filters is null)throw new NullReferenceException();
        bool allow=true;
        if(enabled) { foreach(var callback in Delegate.EnumerateInvocationList(filters))allow &= callback(task); }
        else { foreach(ActionBoolReturn<IAiTask> callback in filters.GetInvocationList())allow &= callback(task); }
        return allow;
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source,ILGenerator generator)
    {
        var original=source.ToArray();
        if(original.Count(x=>x.operand is MethodInfo method&&method.Name==nameof(Delegate.GetInvocationList))!=1)
            throw new InvalidOperationException("Unexpected AI filter body; retain stock.");
        var field=AccessTools.Field(typeof(AiTaskManager),"OnShouldExecuteTask");
        var present=generator.DefineLabel();var label=new CodeInstruction(OpCodes.Nop);label.labels.Add(present);
        return [new(OpCodes.Ldarg_0),new(OpCodes.Ldfld,field),new(OpCodes.Brtrue,present),new(OpCodes.Ldc_I4_1),new(OpCodes.Ret),label,
            new(OpCodes.Ldarg_1),new(OpCodes.Ldarg_0),new(OpCodes.Ldfld,field),new(OpCodes.Call,AccessTools.Method(typeof(AiFilterOptimizations),nameof(Evaluate))),new(OpCodes.Ret)];
    }

}

// Replace only the two delegate constructions in the stock repulsion tick.
// All stock collision, mounting, custom-repulse, dimension and movement work remains.
public static class RepulseDelegateOptimizations
{
    private static volatile bool enabled;
    private static ConditionalWeakTable<EntityBehaviorRepulseAgents, Callbacks> cache=new();
    private static readonly MethodInfo Walk=AccessTools.Method(typeof(EntityBehaviorRepulseAgents), "WalkEntity");
    private static readonly MethodInfo Range=AccessTools.Method(typeof(EntityBehaviorRepulseAgents), "IsInRangePartition");
    public static MethodInfo Target=>AccessTools.Method(typeof(EntityBehaviorRepulseAgents), "OnGameTick");
    public static MethodInfo TranspilerMethod=>AccessTools.Method(typeof(RepulseDelegateOptimizations),nameof(Transpiler));
    public static MethodInfo[] Dependencies=>[Target, Walk, Range, AccessTools.Method(typeof(EntityPartitioning),"WalkEntities",[typeof(double),typeof(double),typeof(double),typeof(double),typeof(ActionConsumable<Entity>),typeof(EntityPartitioning.RangeTestDelegate),typeof(EnumEntitySearchType)])];
    public sealed class Callbacks(EntityBehaviorRepulseAgents owner)
    {
        public readonly ActionConsumable<Entity> WalkCallback=Walk.CreateDelegate<ActionConsumable<Entity>>(owner);
    }
    public static void Configure(bool value) { enabled=value; if(!value) Interlocked.Exchange(ref cache,new()); }
    public static ActionConsumable<Entity> GetWalk(EntityBehaviorRepulseAgents owner)=>enabled?GetCallbacks(owner).WalkCallback:Walk.CreateDelegate<ActionConsumable<Entity>>(owner);
    public static Callbacks GetCallbacks(EntityBehaviorRepulseAgents owner) => !enabled?new(owner):cache.TryGetValue(owner,out var callbacks)?callbacks:cache.GetValue(owner,static x=>new(x));
    public static bool ValidateContract(out string reason)
    {
        bool valid=Walk?.ReturnType==typeof(bool)&&Range?.ReturnType==typeof(bool)&&Target?.ReturnType==typeof(void)&&Dependencies.All(x=>x!=null)
            && Range.GetMethodBody()?.GetILAsByteArray()?.SequenceEqual(new byte[]{0x17,0x2a})==true;
        reason=valid?"Official repulsion callback layout verified.":"Repulsion callback contract differs.";return valid;
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source, ILGenerator generator)
    {
        var code=source.ToList();int walk=-1,range=-1;
        for(int i=1;i<code.Count-1;i++)
        {
            if(code[i].opcode!=OpCodes.Ldftn)continue;
            bool isWalk=Equals(code[i].operand,Walk),isRange=Equals(code[i].operand,Range);
            if(!isWalk&&!isRange)continue;
            if(code[i-1].opcode!=OpCodes.Ldarg_0 || code[i+1].opcode!=OpCodes.Newobj ||
                code.Skip(i-1).Take(3).Any(x=>x.labels.Count!=0||x.blocks.Count!=0) || (isWalk?walk>=0:range>=0))
                throw new InvalidOperationException("Unexpected repulsion callback IL; retain stock.");
            if(isWalk)walk=i;else range=i;
        }
        if(walk<0||range<=walk)throw new InvalidOperationException("Expected exactly two stock callback constructions in order.");
        // The inspected private range predicate is literally `return true`.
        // Null chooses the official walker's no-range-test branch, avoiding a
        // delegate construction AND a redundant predicate call per neighbor.
        code[range-1]=new(OpCodes.Ldnull);code.RemoveRange(range,2);
        code[walk]=new(OpCodes.Call,AccessTools.Method(typeof(RepulseDelegateOptimizations),nameof(GetWalk)));
        code.RemoveAt(walk+1);
        return code;
    }
}
