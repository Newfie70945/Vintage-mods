using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace NewfiesOptimizer.Optimizations;

// Opt-in. Every 3 s a non-empty barrel without a current recipe rescans the whole
// barrel recipe registry; storage barrels never match and repeat the same scan
// forever. Skip that scan only while both slots still hold the very same stacks
// (reference, collectible and size) as the last scan that found nothing and did
// not change the inventory. A native rescan is forced every N checks so mod
// MatchesForCrafting hooks and in-place attribute changes are still observed.
// Recipes, outputs, sealing and every other barrel path remain native.
public static class BarrelRecipeMemoOptimizations
{
    // SHA-256 of the inspected official 1.22.7 IL for OnEvery3Second and FindMatchingRecipe(IPlayer).
    private const string TickIlHash = "FD37A2932FB344FF7355B3EFE1390875EC55645132E601AD18D2E905379CF458";
    private const string SearchIlHash = "48594379ADB4B0702D54CF7F440B92AB2AB0307B585B23EC8E42CDFFA3EF985C";
    private static volatile bool enabled;
    private static int rescanEvery = 10;
    private static ConditionalWeakTable<BlockEntityBarrel, Memo> memos = new();
    private static long checks, skipped, scans, fallbacks, failures;
    private sealed class Memo { public Contents Last; public int Skips; }
    public struct Contents
    {
        internal ItemStack? Input, Liquid; internal CollectibleObject? InputType, LiquidType; internal int InputSize, LiquidSize;
        internal static Contents Of(ItemSlot input, ItemSlot liquid)
        {
            var a = input.Itemstack; var b = liquid.Itemstack;
            return new() { Input = a, Liquid = b, InputType = a?.Collectible, LiquidType = b?.Collectible, InputSize = a?.StackSize ?? 0, LiquidSize = b?.StackSize ?? 0 };
        }
        internal readonly bool Same(in Contents other) => ReferenceEquals(Input, other.Input) && ReferenceEquals(Liquid, other.Liquid) &&
            ReferenceEquals(InputType, other.InputType) && ReferenceEquals(LiquidType, other.LiquidType) && InputSize == other.InputSize && LiquidSize == other.LiquidSize;
    }
    public struct Scan { internal bool Active; internal Contents Before; }
    public static MethodInfo Target => AccessTools.Method(typeof(BlockEntityBarrel), "OnEvery3Second");
    public static MethodInfo Search => AccessTools.Method(typeof(BlockEntityBarrel), "FindMatchingRecipe", [typeof(IPlayer)]);
    public static MethodBase[] Dependencies => [Target, Search, AccessTools.Method(typeof(BlockEntityBarrel), "FindMatchingRecipe", Type.EmptyTypes),
        AccessTools.Method(typeof(ApiAdditions), nameof(ApiAdditions.GetBarrelRecipes)), BarrelCountOptimizations.Target, BlockTickOptimizations.Target(1),
        AccessTools.Method(typeof(BarrelRecipe), nameof(BarrelRecipe.Matches), [typeof(IPlayer), typeof(ItemSlot[]), typeof(int).MakeByRefType()])];
    public static (long Checks, long Skipped, long Scans, long Fallbacks, long Failures) Counters =>
        (Interlocked.Read(ref checks), Interlocked.Read(ref skipped), Interlocked.Read(ref scans), Interlocked.Read(ref fallbacks), Interlocked.Read(ref failures));
    public static void Configure(bool value) { enabled = false; Interlocked.Exchange(ref memos, new()); enabled = value; }
    public static void SetRescanEvery(int value) => Volatile.Write(ref rescanEvery, Math.Clamp(value, 2, 100));
    public static bool ValidateContract(out string reason)
    {
        static string? Hash(MethodInfo? method) => method?.GetMethodBody()?.GetILAsByteArray() is { } il ? Convert.ToHexString(SHA256.HashData(il)) : null;
        bool valid = Dependencies.All(m => m != null) && Hash(Target) == TickIlHash && Hash(Search) == SearchIlHash &&
            AccessTools.Field(typeof(BlockEntityBarrel), nameof(BlockEntityBarrel.CurrentRecipe))?.FieldType == typeof(BarrelRecipe) &&
            AccessTools.Field(typeof(BlockEntityBarrel), nameof(BlockEntityBarrel.Sealed))?.FieldType == typeof(bool) &&
            AccessTools.PropertyGetter(typeof(BlockEntityBarrel), nameof(BlockEntityBarrel.Inventory))?.DeclaringType == typeof(BlockEntityLiquidContainer);
        reason = valid ? "Official barrel recipe rescan verified; matching, sealing and crafting retained natively." : "Barrel tick or recipe search differs; retain stock.";
        return valid;
    }
    public static bool Prefix(BlockEntityBarrel __instance, out Scan __state)
    {
        __state = default;
        if (!enabled) return true;
        var inventory = __instance.Inventory;
        if (__instance.GetType() != typeof(BlockEntityBarrel) || __instance.Api is not ICoreServerAPI || inventory?.GetType() != typeof(InventoryGeneric) || inventory.Count != 2 ||
            inventory[0].GetType() != typeof(ItemSlotBarrelInput) || inventory[1].GetType() != typeof(ItemSlotLiquidOnly))
        { Interlocked.Increment(ref fallbacks); return true; }
        ItemSlot input = inventory[0], liquid = inventory[1];
        // The native body scans only for a non-empty barrel without a current recipe.
        if (input.Empty || __instance.CurrentRecipe != null) { memos.Remove(__instance); return true; }
        Interlocked.Increment(ref checks);
        var now = Contents.Of(input, liquid);
        if (memos.TryGetValue(__instance, out var memo) && memo.Skips < Volatile.Read(ref rescanEvery) - 1 && memo.Last.Same(now))
        {
            memo.Skips++; Interlocked.Increment(ref skipped);
            // The remainder of the native body when the scan finds no recipe.
            if (__instance.Sealed) { __instance.Sealed = false; __instance.MarkDirty(redrawOnClient: true); }
            return false;
        }
        Interlocked.Increment(ref scans);
        __state = new Scan { Active = true, Before = now };
        return true;
    }
    public static void Postfix(BlockEntityBarrel __instance, Scan __state)
    {
        if (!__state.Active || !enabled) return;
        var inventory = __instance.Inventory;
        // Memoize only a scan that found nothing and left the contents untouched:
        // an instant recipe may have crafted, and its output must be rescanned natively.
        if (__instance.CurrentRecipe != null || !Contents.Of(inventory[0], inventory[1]).Same(__state.Before)) { memos.Remove(__instance); return; }
        bool first = !memos.TryGetValue(__instance, out var memo);
        if (first) memo = memos.GetOrCreateValue(__instance);
        // Barrels loaded together share one 3 s listener beat. Start each new memo at a
        // position-derived phase so forced rescans spread over the interval instead of
        // every barrel rescanning on the same tick.
        memo!.Last = __state.Before; memo.Skips = first ? Phase(__instance, Volatile.Read(ref rescanEvery)) : 0;
    }
    internal static int Phase(BlockEntityBarrel barrel, int every)
    {
        var pos = barrel.Pos;
        if (pos == null || every <= 2) return 0;
        uint hash = unchecked((uint)(pos.X * 73856093 ^ pos.InternalY * 19349663 ^ pos.Z * 83492791));
        return (int)(hash % (uint)(every - 1));
    }
    public static Exception? Finalizer(Exception? __exception, Scan __state)
    {
        if (__exception != null && enabled) { Interlocked.Increment(ref failures); Configure(false); }
        return __exception; // Never replay a recipe scan or craft.
    }
}
