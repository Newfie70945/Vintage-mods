using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Datastructures;
namespace NewfiesOptimizer.Optimizations;

// A missing-key removal has no data change but stock marks the entire tree dirty.
// Opt-in: preserve null-key exceptions, subclasses and every real removal.
public static class AttributeSyncOptimizations
{
    private static volatile bool enabled;
    private static long skipped;
    public static MethodInfo Target=>AccessTools.Method(typeof(SyncedTreeAttribute),nameof(SyncedTreeAttribute.RemoveAttribute));
    public static MethodInfo[] Dependencies=>[Target,AccessTools.Method(typeof(TreeAttribute),nameof(TreeAttribute.HasAttribute)),AccessTools.Method(typeof(TreeAttribute),nameof(TreeAttribute.RemoveAttribute))];
    public static void Configure(bool value)=>enabled=value;
    public static long Skipped=>Interlocked.Read(ref skipped);
    public static bool ValidateContract(out string reason)
    {bool valid=Target?.ReturnType==typeof(void)&&Target.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(string)});reason=valid?"Official synchronized removal contract verified.":"Attribute removal contract differs.";return valid;}
    public static bool Prefix(SyncedTreeAttribute __instance,string key)
    {
        if(!enabled||key==null||__instance.GetType()!=typeof(SyncedTreeAttribute)||__instance.HasAttribute(key))return true;
        Interlocked.Increment(ref skipped);return false;
    }
}
