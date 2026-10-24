using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
namespace NewfiesOptimizer.Optimizations;
// Reject only impossible native input counts. Never cache recipes or skip
// collectible matching hooks for a potentially matching recipe.
public static class BarrelCountOptimizations
{
    private static volatile bool enabled;
    private static long calls, fallbacks, failures;
    public static MethodInfo Target => AccessTools.Method(typeof(BarrelRecipe), nameof(BarrelRecipe.Matches), [typeof(ItemSlot[]), typeof(int).MakeByRefType()]);
    public static MethodBase[] Dependencies => [Target, BlockTickOptimizations.Target(1), AccessTools.PropertyGetter(typeof(ItemSlot), nameof(ItemSlot.Empty)), AccessTools.PropertyGetter(typeof(ItemSlot), nameof(ItemSlot.Itemstack))];
    public static void Configure(bool value) => enabled = value;
    public static (long Calls,long Fallbacks,long Failures) Counters => (Interlocked.Read(ref calls),Interlocked.Read(ref fallbacks),Interlocked.Read(ref failures));
    public static bool ValidateContract(out string reason)
    {
        bool valid = BlockTickOptimizations.ValidateContract(1,out _) && Target.ReturnType==typeof(bool) &&
            new[]{typeof(ItemSlot),typeof(ItemSlotLiquidOnly),typeof(ItemSlotBarrelInput)}.All(t =>
                AccessTools.PropertyGetter(t,nameof(ItemSlot.Empty))?.DeclaringType==typeof(ItemSlot) &&
                AccessTools.PropertyGetter(t,nameof(ItemSlot.Itemstack))?.DeclaringType==typeof(ItemSlot));
        reason=valid?"Native barrel input-count guard; matching hooks and output calculation retained.":"Barrel matching contract differs; retain stock.";
        return valid;
    }
    public static bool Prefix(BarrelRecipe __instance, ItemSlot[] inputSlots, ref int outputStackSize, ref bool __result, out bool __state)
    {
        __state=enabled;
        if(!__state)return true;
        if(__instance.GetType()!=typeof(BarrelRecipe)||inputSlots==null||inputSlots.Length>2||__instance.Ingredients==null)
        {Interlocked.Increment(ref fallbacks);return true;}
        int count=0;
        foreach(var slot in inputSlots)
        {
            // Custom slots can have observable accessors: never read them twice.
            if(slot==null || (slot.GetType()!=typeof(ItemSlot) && slot.GetType()!=typeof(ItemSlotLiquidOnly) && slot.GetType()!=typeof(ItemSlotBarrelInput)))
            {Interlocked.Increment(ref fallbacks);return true;}
            if(!slot.Empty)count++;
        }
        if(count==__instance.Ingredients.Length)return true;
        outputStackSize=0;__result=false;Interlocked.Increment(ref calls);return false;
    }
    public static Exception? Finalizer(Exception? __exception,bool __state)
    {if(__exception!=null&&__state){Interlocked.Increment(ref failures);enabled=false;}return __exception;}
}
