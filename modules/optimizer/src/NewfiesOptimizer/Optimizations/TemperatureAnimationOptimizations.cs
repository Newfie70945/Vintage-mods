using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
namespace NewfiesOptimizer.Optimizations;

// Replace only two params-array animation lookups. The native behavior retains
// temperature, heat-source scans, damage, held-item checks and animation changes.
public static class TemperatureAnimationOptimizations
{
    private static volatile bool enabled;
    private static long calls, fallbacks, failures;
    public static MethodInfo Target=>AccessTools.Method(typeof(EntityBehaviorBodyTemperature),"updateFreezingAnimState");
    public static MethodInfo Lookup=>AccessTools.Method(typeof(IAnimationManager),nameof(IAnimationManager.IsAnimationActive),[typeof(string[])]);
    public static MethodBase[] Dependencies=>[Target,AccessTools.Method(typeof(AnimationManager),nameof(AnimationManager.IsAnimationActive),[typeof(string[])])];
    public static (long Calls,long Fallbacks,long Failures) Counters=>(Interlocked.Read(ref calls),Interlocked.Read(ref fallbacks),Interlocked.Read(ref failures));
    public static void Configure(bool value)=>enabled=value;
    public static bool ValidateContract(out string reason)
    {
        bool valid=Target?.ReturnType==typeof(void)&&Target.GetParameters().Length==0&&PatchProcessor.GetOriginalInstructions(Target).Count(c=>c.Calls(Lookup))==2;
        reason=valid?"Official temperature animation lookups verified; native temperature behavior retained.":"Temperature animation contract differs; retain stock.";return valid;
    }
    public static void Prefix(out bool __state)=>__state=enabled;
    public static bool Check(IAnimationManager manager,string code)
    {
        if(!enabled||manager?.GetType()!=typeof(AnimationManager))
        {Interlocked.Increment(ref fallbacks);return manager!.IsAnimationActive(new[]{code});}
        Interlocked.Increment(ref calls);
        return ((AnimationManager)manager).ActiveAnimationsByAnimCode.ContainsKey(code);
    }
    public static Exception? Finalizer(Exception? __exception,bool __state)
    {if(__exception!=null&&__state){Interlocked.Increment(ref failures);enabled=false;}return __exception;}
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
    {
        var code=source.ToList();int changed=0;
        for(int i=code.Count-7;i>=0;i--)
        {
            if(!code[i+6].Calls(Lookup))continue;
            if(code[i].opcode!=OpCodes.Ldc_I4_1||code[i+1].opcode!=OpCodes.Newarr||!Equals(code[i+1].operand,typeof(string))||
               code[i+2].opcode!=OpCodes.Dup||code[i+3].opcode!=OpCodes.Ldc_I4_0||code[i+4].opcode!=OpCodes.Ldstr||code[i+5].opcode!=OpCodes.Stelem_Ref||
               code.Skip(i).Take(7).Any(c=>c.labels.Count!=0||c.blocks.Count!=0))
                throw new InvalidOperationException("Unexpected temperature animation array IL; retain stock.");
            var literal=code[i+4];code.RemoveRange(i,7);code.InsertRange(i,new[]{literal,new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(TemperatureAnimationOptimizations),nameof(Check)))});changed++;
        }
        if(changed!=2)throw new InvalidOperationException("Expected two temperature animation lookups; retain stock.");
        return code;
    }
}
