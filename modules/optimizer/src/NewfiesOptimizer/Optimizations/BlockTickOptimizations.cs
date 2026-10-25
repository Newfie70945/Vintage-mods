using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace NewfiesOptimizer.Optimizations;

// Exact official types only. No cached recipe results, growth state or item data.
public static class BlockTickOptimizations
{
    private static readonly string[] OfficialIlHashes = ["BC72ADB8EFD3F3671F7C421A8A277D8375A6981C8437E10384AC94B909AC8D20", "7D9743858B48C20B61A623F9C240DF708A69AD95E24A34623CAC3E49ADBF7695", "D2CB9327346C48349C2A6397AFF310F71251854EC0AA3EE1DE4C42AC52FC9035"];
    private static readonly bool[] enabled = new bool[3];
    private static readonly long[] calls = new long[3], fallbacks = new long[3], failures = new long[3];
    // Resolved on first guarded use, not when the registry switches modules off.
    private static class Native
    {
        internal static readonly MethodInfo Match = AccessTools.Method(typeof(RecipeBase), "MatchStackToIngredient");
        internal static readonly System.Func<BarrelRecipe, ItemStack?, IRecipeIngredient, bool> MatchIngredient = Match.CreateDelegate<System.Func<BarrelRecipe, ItemStack?, IRecipeIngredient, bool>>();
    }
    private static MethodInfo Match => Native.Match;
    private static System.Func<BarrelRecipe, ItemStack?, IRecipeIngredient, bool> MatchIngredient => Native.MatchIngredient;
    public static MethodInfo Target(int kind) => kind switch
    {
        0 => AccessTools.Method(typeof(BlockEntityFirepit), "OnBurnTick"),
        1 => AccessTools.Method(typeof(BarrelRecipe), "PairInput"),
        2 => AccessTools.Method(typeof(BlockEntityWildFruitingBush), "beginIntervalledUpdate"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static MethodBase[] Dependencies(int kind) => kind switch
    {
        0 => [Target(0), AccessTools.Method(typeof(BlockEntityFirepit), "enviromentTemperature"), AccessTools.Method(typeof(BlockEntityFirepit), "changeTemperature"), AccessTools.PropertyGetter(typeof(BlockEntityFirepit), "fuelStack"), AccessTools.PropertyGetter(typeof(BlockEntityFirepit), "inputStack"), AccessTools.PropertyGetter(typeof(BlockEntityFirepit), "outputStack"), AccessTools.Method(typeof(BlockEntityFirepit), "getCombustibleOpts"), AccessTools.Method(typeof(BlockEntityFirepit), "canHeatInput"), AccessTools.Method(typeof(BlockEntityFirepit), "canHeatOutput"), AccessTools.Method(typeof(BlockEntityFirepit), "canSmeltInput"), AccessTools.Method(typeof(BlockEntityFirepit), "canSmelt"), AccessTools.PropertyGetter(typeof(BlockEntityFirepit), "IsBurning"), AccessTools.PropertyGetter(typeof(BlockEntityFirepit), "Inventory")],
        1 => [Target(1), Match, AccessTools.PropertyGetter(typeof(ItemSlot),nameof(ItemSlot.Empty)), AccessTools.PropertyGetter(typeof(ItemSlot),nameof(ItemSlot.Itemstack))],
        2 => [Target(2), AccessTools.Method(typeof(BlockEntityFastForwardGrowth), "beginIntervalledUpdate"), AccessTools.Method(typeof(BEBehaviorFruitingBush), "onUpdate")],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static void Configure(int kind, bool value) => Volatile.Write(ref enabled[kind], value);
    public static (long Calls, long Fallbacks, long Failures) Counters(int kind) => (Interlocked.Read(ref calls[kind]), Interlocked.Read(ref fallbacks[kind]), Interlocked.Read(ref failures[kind]));
    public static bool ValidateContract(int kind, out string reason)
    {
        bool valid = Dependencies(kind).All(m => m != null) && Target(kind).GetMethodBody()?.GetILAsByteArray() is { } il && Convert.ToHexString(SHA256.HashData(il)) == OfficialIlHashes[kind];
        if(kind==1)valid &= new[]{typeof(ItemSlot),typeof(ItemSlotLiquidOnly),typeof(ItemSlotBarrelInput)}.All(t=>
            AccessTools.PropertyGetter(t,nameof(ItemSlot.Empty))?.DeclaringType==typeof(ItemSlot) &&
            AccessTools.PropertyGetter(t,nameof(ItemSlot.Itemstack))?.DeclaringType==typeof(ItemSlot));
        if (kind == 2)
        {
            // Flatten only the official no-op base callback, never a mod override.
            var body = PatchProcessor.GetOriginalInstructions(Dependencies(2)[1]).Where(x => x.opcode != System.Reflection.Emit.OpCodes.Nop).ToList();
            valid &= body.Count == 7 && body[0].opcode == System.Reflection.Emit.OpCodes.Ldarg_1 && body[1].opcode == System.Reflection.Emit.OpCodes.Ldnull && body[2].opcode == System.Reflection.Emit.OpCodes.Stind_Ref && body[3].opcode == System.Reflection.Emit.OpCodes.Ldarg_2 && body[4].opcode == System.Reflection.Emit.OpCodes.Ldnull && body[5].opcode == System.Reflection.Emit.OpCodes.Stind_Ref && body[6].opcode == System.Reflection.Emit.OpCodes.Ret;
        }
        reason = valid ? "Official exact-type block callback contract verified; native active work and hooks retained." : "Block callback contract differs; retain stock.";
        return valid;
    }
    public static bool FirepitPrefix(BlockEntityFirepit __instance, out bool __state)
    {
        __state = Volatile.Read(ref enabled[0]);
        if (!__state) return true;
        if (__instance.GetType() != typeof(BlockEntityFirepit) || __instance.Api is not ICoreServerAPI || __instance.Api is ICoreClientAPI ||
            __instance.Inventory?.GetType() != typeof(InventorySmelting) ||
            __instance.Block?.Code?.Path is not { } path || path.Contains("construct", StringComparison.Ordinal) ||
            __instance.Block.Variant?["burnstate"] != "cold" ||
            __instance.fuelBurnTime != 0 || __instance.furnaceTemperature != 20 ||
            __instance.fuelStack != null || __instance.inputStack != null || __instance.outputStack != null)
        { Interlocked.Increment(ref fallbacks[0]); return true; }
        // This is the only native write for an empty, cold official firepit.
        __instance.inputStackCookingTime = 0;
        Interlocked.Increment(ref calls[0]);
        return false;
    }
    public static bool BarrelPrefix(BarrelRecipe __instance, ItemSlot[] inputSlots, ref List<(ItemSlot, BarrelRecipeIngredient)> __result, out bool __state)
    {
        __state = Volatile.Read(ref enabled[1]);
        if (!__state) return true;
        var ingredients = __instance.Ingredients;
        if (__instance.GetType() != typeof(BarrelRecipe) || inputSlots == null || inputSlots.Length > 2 || !HasNativeSlots(inputSlots) || ingredients == null || ingredients.Length > 2 || !HasNativeIngredients(ingredients))
        { Interlocked.Increment(ref fallbacks[1]); return true; }
        int count = 0;
        foreach (var slot in inputSlots) if (!slot.Empty) count++;
        if (count != ingredients.Length) { __result = new(); Interlocked.Increment(ref calls[1]); return false; }
        // Snapshot the same ingredient references as the native temporary list.
        BarrelRecipeIngredient? first = ingredients.Length > 0 ? ingredients[0] : null, second = ingredients.Length > 1 ? ingredients[1] : null;
        bool usedFirst = false, usedSecond = false;
        ItemSlot? slot0 = null, slot1 = null; BarrelRecipeIngredient? item0 = null, item1 = null; int pairs = 0;
        foreach (var slot in inputSlots)
        {
            if (slot.Itemstack == null) continue;
            BarrelRecipeIngredient? found = null;
            if (!usedFirst && first != null && MatchIngredient(__instance, slot.Itemstack, first)) { found = first; usedFirst = true; }
            else if (!usedSecond && second != null && MatchIngredient(__instance, slot.Itemstack, second)) { found = second; usedSecond = true; }
            if (found == null) { __result = new(); Interlocked.Increment(ref calls[1]); return false; }
            if (pairs++ == 0) { slot0 = slot; item0 = found; } else { slot1 = slot; item1 = found; }
        }
        __result = new(pairs);
        // Collectible hooks may replace Ingredients during matching. The native
        // implementation checks the current array here, after using its snapshot.
        if (pairs >= __instance.Ingredients!.Length)
        {
            if (pairs > 0) __result.Add((slot0!, item0!));
            if (pairs > 1) __result.Add((slot1!, item1!));
        }
        Interlocked.Increment(ref calls[1]); return false;
    }
    // Custom accessors can mutate recipe ingredients during the native initial
    // occupancy pass. Do not read any such getter before falling back.
    private static bool HasNativeSlots(ItemSlot[] slots)
    {
        foreach(var slot in slots)
        {
            var type=slot?.GetType();
            if(type!=typeof(ItemSlot)&&type!=typeof(ItemSlotLiquidOnly)&&type!=typeof(ItemSlotBarrelInput))return false;
        }
        return true;
    }
    // Only zero to two entries reach this check. Avoid a generic predicate scan
    // on every recipe attempt; retain exact-type fallback for mod ingredients.
    private static bool HasNativeIngredients(BarrelRecipeIngredient[] ingredients)
    {
        for (int i = 0; i < ingredients.Length; i++)
            if (ingredients[i] == null || ingredients[i].GetType() != typeof(BarrelRecipeIngredient)) return false;
        return true;
    }
    public static bool BushPrefix(BlockEntityWildFruitingBush __instance, ref FarmlandFastForwardUpdate? onInterval, ref FarmlandUpdateEnd? onEnd, out bool __state)
    {
        __state = Volatile.Read(ref enabled[2]);
        if (!__state) return true;
        if (__instance.GetType() != typeof(BlockEntityWildFruitingBush)) { Interlocked.Increment(ref fallbacks[2]); return true; }
        // onUpdate is deliberately evaluated afresh: its young-state closure is per batch.
        onEnd = null;
        onInterval = __instance.GetBehavior<BEBehaviorFruitingBush>()?.onUpdate();
        Interlocked.Increment(ref calls[2]); return false;
    }
    public static Exception? FirepitFinalizer(Exception? __exception, bool __state) => Finish(0, __exception, __state);
    public static Exception? BarrelFinalizer(Exception? __exception, bool __state) => Finish(1, __exception, __state);
    public static Exception? BushFinalizer(Exception? __exception, bool __state) => Finish(2, __exception, __state);
    private static Exception? Finish(int kind, Exception? error, bool state)
    {
        if (error != null && state) { Interlocked.Increment(ref failures[kind]); Configure(kind, false); }
        return error;
    }
}
