using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;

namespace NewfiesOptimizer.Optimizations;

// Keep OnFrame, collision-box assignment and triggers in the native method.
// Direct Dictionary enumeration preserves predicate order and mutation checks.
public static class AnimationScanOptimizations
{
    private static volatile bool enabled;
    private static long calls,fallbacks,failures;
    public static MethodInfo Target=>AccessTools.Method(typeof(AnimationManager),nameof(AnimationManager.OnServerTick));
    public static MethodInfo Any=>typeof(Enumerable).GetMethods().Single(m=>m.Name==nameof(Enumerable.Any)&&m.GetParameters().Length==2).MakeGenericMethod(typeof(KeyValuePair<string,AnimationMetaData>));
    public static MethodBase[] Dependencies=>[Target,Any];
    public static bool ValidateContract(out string reason){bool valid=Target.ReturnType==typeof(void)&&Target.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(float)})&&PatchProcessor.GetOriginalInstructions(Target).Count(c=>c.Calls(Any))==1;reason=valid?"Official server animation flag scan verified; frames/triggers retained.":"Animation flag scan differs; retain stock.";return valid;}
    public static void Configure(bool value)=>enabled=value;
    public static (long Calls,long Fallbacks,long Failures) Counters=>(Interlocked.Read(ref calls),Interlocked.Read(ref fallbacks),Interlocked.Read(ref failures));
    public static void Prefix(out bool __state)=>__state=enabled;
    public static bool Check(IEnumerable<KeyValuePair<string,AnimationMetaData>> source,System.Func<KeyValuePair<string,AnimationMetaData>,bool> predicate)
    {
        if(!enabled)return source.Any(predicate);
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(predicate);
        if(source.GetType()!=typeof(Dictionary<string,AnimationMetaData>)){Interlocked.Increment(ref fallbacks);return source.Any(predicate);}
        Interlocked.Increment(ref calls);
        foreach(var item in (Dictionary<string,AnimationMetaData>)source)if(predicate(item))return true;
        return false;
    }
    public static Exception? Finalizer(Exception? __exception,bool __state){if(__exception!=null&&__state){Interlocked.Increment(ref failures);enabled=false;}return __exception;}
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source){var code=source.ToList();int n=0;foreach(var c in code)if(c.Calls(Any)){c.opcode=OpCodes.Call;c.operand=AccessTools.Method(typeof(AnimationScanOptimizations),nameof(Check));n++;}if(n!=1)throw new InvalidOperationException("Unexpected animation scan IL; retain stock.");return code;}
}
