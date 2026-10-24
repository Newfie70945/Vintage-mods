using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace NewfiesOptimizer.Optimizations;

// Only the base helpers change. Virtual recipe overrides, collectible hooks,
// stack comparisons/clones and caller-owned scratch lists retain their ownership.
public static class RecipeOptimizations
{
    private static readonly bool[] enabled = new bool[2];
    private static readonly long[] calls = new long[2], failures = new long[2];
    public static MethodInfo Target(int kind) => kind switch
    {
        0 => AccessTools.Method(typeof(RecipeBase), "MergeStacks", [typeof(ItemSlot[]), typeof(List<ItemStack>)]),
        1 => AccessTools.Method(typeof(RecipeBase), "MatchWildcardIngredients", [typeof(List<ItemStack>), typeof(IRecipeIngredient[])]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static long variantFastPaths;
    public static long VariantFastPaths => Interlocked.Read(ref variantFastPaths);
    public static MethodBase[] Dependencies(int kind) => kind==0 ? [Target(0)] :
        [Target(1), AccessTools.Method(typeof(WildcardUtil),nameof(WildcardUtil.Match),[typeof(AssetLocation),typeof(AssetLocation),typeof(string[])]),
         AccessTools.Method(typeof(WildcardUtil),nameof(WildcardUtil.MatchesVariants))];
    public static bool MatchCode(AssetLocation? wildcard, AssetLocation? code, string[]? variants)
    {
        // Optimize only the allocation-producing native trailing-star variant case.
        // All other patterns, nulls and mod AssetLocation subclasses retain native behavior.
        if(variants==null || wildcard?.GetType()!=typeof(AssetLocation) || code?.GetType()!=typeof(AssetLocation))
            return WildcardUtil.Match(wildcard!,code!,variants);
        string pattern=wildcard.Path;
        int prefix=pattern.Length-1;
        if(prefix<0 || pattern[prefix]!='*' || pattern.IndexOf('*')!=prefix)
            return WildcardUtil.Match(wildcard,code,variants);
        Interlocked.Increment(ref variantFastPaths);
        if(wildcard.Domain=="*" && pattern=="*")return true;
        if(wildcard.Equals(code))return true;
        if(wildcard.Domain!="*" && !wildcard.Domain.Equals(code.Domain))return false;
        if(!StringUtil.FastStartsWith(code.Path,pattern,prefix) || code.Path.Length<=prefix)return false;
        var value=code.Path.AsSpan(prefix);
        foreach(string? variant in variants)
            if(variant!=null && value.SequenceEqual(variant.AsSpan()))return true;
        return false;
    }
    public static bool ValidateContract(int kind, out string reason)
    {
        var method = Target(kind);
        bool valid = method?.DeclaringType == typeof(RecipeBase) && method.IsVirtual && method.ReturnType == (kind == 0 ? typeof(void) : typeof(bool));
        reason = valid ? "Official base-recipe helper verified; comparisons, clones and crafting hooks retained." : "Base-recipe helper differs; retain stock.";
        return valid;
    }
    public static void Configure(int kind, bool value) => Volatile.Write(ref enabled[kind], value);
    public static (long Calls, long Failures) Counters(int kind) => (Interlocked.Read(ref calls[kind]), Interlocked.Read(ref failures[kind]));
    public static bool MergePrefix(ItemSlot[] slots, List<ItemStack> stacks, out bool __state)
    {
        __state = Volatile.Read(ref enabled[0]);
        if (!__state) return true;
        Interlocked.Increment(ref calls[0]);
        ArgumentNullException.ThrowIfNull(slots, "source");
        foreach (var slot in slots)
        {
            var supplied = slot.Itemstack;
            if (supplied == null) continue;
            ItemStack? match = null;
            for (int i = 0; i < stacks.Count; i++)
            {
                var candidate = stacks[i];
                if (!candidate.Satisfies(supplied)) continue;
                match = candidate; break;
            }
            if (match == null) stacks.Add(supplied.Clone());
            else match.StackSize += supplied.StackSize;
        }
        return false;
    }
    public static bool WildcardPrefix(RecipeBase __instance, List<ItemStack> suppliedStacks, IRecipeIngredient?[] ingredients, ref bool __result, out bool __state)
    {
        __state = Volatile.Read(ref enabled[1]);
        if (!__state) return true;
        Interlocked.Increment(ref calls[1]);
        ArgumentNullException.ThrowIfNull(ingredients, "source");
        foreach (var ingredient in ingredients)
        {
            if (ingredient == null || ingredient.MatchingType == EnumRecipeMatchType.Exact) continue;
            bool found = false;
            int index = 0;
            while (!found && index < suppliedStacks.Count)
            {
                var stack = suppliedStacks[index++];
                found = ingredient.Type == stack.Class && MatchCode(ingredient.Code, stack.Collectible.Code, ingredient.AllowedVariants)
                    && stack.StackSize >= ingredient.Quantity && ingredient.Tags.Matches(in stack.Collectible.Tags);
                // Stock calls this even when the preliminary comparison failed.
                found &= stack.Collectible.MatchesForCrafting(stack, __instance, ingredient);
            }
            if (!found) { __result = false; return false; }
            suppliedStacks.RemoveAt(index - 1);
        }
        __result = true;
        return false;
    }
    public static Exception? MergeFinalizer(Exception? __exception, bool __state) => Finish(0, __exception, __state);
    public static Exception? WildcardFinalizer(Exception? __exception, bool __state) => Finish(1, __exception, __state);
    private static Exception? Finish(int kind, Exception? error, bool executed)
    {
        if (error != null && executed) { Interlocked.Increment(ref failures[kind]); Volatile.Write(ref enabled[kind], false); }
        // Never replay a partially completed merge or a mod crafting hook.
        return error;
    }
}
