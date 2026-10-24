using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Vintagestory.Common;

namespace NewfiesOptimizer.Optimizations;

// Only temporary candidate storage and duplicate membership change. Ingredient checks, insertion
// order, shaped/shapeless ranking, matching and output hooks all still execute.
public static class CraftingCandidateOptimizations
{
    public const int SmallListLimit = 16;
    public const int MaxRetainedRecipes = 4096;
    public const int MaxRetainedContexts = 4;
    private static volatile bool enabled;
    private static int generation;
    private static long queries, hashedQueries, checks, duplicates, fallbacks, created, oversized, failures;
    [ThreadStatic] private static Context? active;
    [ThreadStatic] private static Context? idle;
    [ThreadStatic] private static int idleCount;
    public sealed class Context
    {
        internal readonly HashSet<GridRecipe> Seen = new(ReferenceEqualityComparer.Instance);
        internal readonly List<GridRecipe> Candidates = new();
        internal Context? Next;
        internal List<GridRecipe>? List;
        internal int Generation;
        internal long Checks, Duplicates;
        internal bool Hashed, Custom;
    }
    public static MethodInfo Target => AccessTools.Method(typeof(InventoryCraftingGrid), "FindMatchingRecipe");
    public static MethodInfo NativeAdd => AccessTools.Method(typeof(ListExtensions), nameof(ListExtensions.AddIfNotPresent))!.MakeGenericMethod(typeof(GridRecipe));
    public static MethodBase[] Dependencies => [Target, NativeAdd,
        AccessTools.Method(typeof(List<GridRecipe>), nameof(List<GridRecipe>.Contains)),
        AccessTools.Method(typeof(List<GridRecipe>), nameof(List<GridRecipe>.Add)),
        AccessTools.Method(typeof(object),nameof(object.Equals),[typeof(object)])];
    public static (long Queries,long Hashed,long Checks,long Duplicates,long Fallbacks,long Created,long Oversized,long Failures) Counters =>
        (Interlocked.Read(ref queries),Interlocked.Read(ref hashedQueries),Interlocked.Read(ref checks),Interlocked.Read(ref duplicates),Interlocked.Read(ref fallbacks),Interlocked.Read(ref created),Interlocked.Read(ref oversized),Interlocked.Read(ref failures));
    public static bool CurrentThreadRetainsRecipes => active?.List != null || RetainedContextsContainRecipes();
    private static bool RetainedContextsContainRecipes()
    { for(var entry=idle;entry!=null;entry=entry.Next)if(entry.List!=null || entry.Seen.Count!=0 || entry.Candidates.Count!=0)return true;return false; }
    public static void Configure(bool value)
    { enabled=false;Interlocked.Increment(ref generation);idle=null;idleCount=0;enabled=value; }
    public static bool ValidateContract(out string reason)
    {
        var code=PatchProcessor.GetOriginalInstructions(Target);
        bool valid=Target.ReturnType==typeof(void) && Target.GetParameters().Length==0 &&
            NativeAdd.ReturnType==typeof(void) && NativeAdd.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(List<GridRecipe>),typeof(GridRecipe)}) &&
            code.Count(i=>i.Calls(NativeAdd))==1 && code.Count(i=>i.opcode==OpCodes.Newobj&&Equals(i.operand,typeof(List<GridRecipe>).GetConstructor(Type.EmptyTypes)))==1 && typeof(GridRecipe).GetMethod(nameof(object.Equals),[typeof(object)])?.DeclaringType==typeof(object) &&
            Target.GetMethodBody()?.GetILAsByteArray() is {} il && Convert.ToHexString(SHA256.HashData(il))=="3A0787D67B0AD31F52891CC39F61F0FBF3C9BB14B9598AFCA41F76D763B4C329";
        reason=valid?"Official crafting candidate loop verified; full batches and every native recipe/XP hook retained.":"Crafting candidate loop differs; retain stock.";
        return valid;
    }
    public struct Scope { internal Context? Parent, Entry; }
    public static void Prefix(InventoryCraftingGrid __instance,out Scope __state)
    {
        __state=new Scope{Parent=active};active=null;
        if(!enabled || __instance.GetType()!=typeof(InventoryCraftingGrid))return;
        var entry=idle;
        if(entry!=null){idle=entry.Next;idleCount--;entry.Next=null;}
        if(entry==null || entry.Generation!=generation)
        {
            if(entry!=null){idle=null;idleCount=0;}
            entry=new Context{Generation=generation};Interlocked.Increment(ref created);
        }
        active=__state.Entry=entry;
    }
    public static List<GridRecipe> RentCandidates() => active?.Candidates ?? new();
    public static void AddCandidate(List<GridRecipe> list,GridRecipe recipe)
    {
        var entry=active;
        if(!enabled || entry==null || (entry.List!=null&&!ReferenceEquals(entry.List,list)))
        {ListExtensions.AddIfNotPresent<GridRecipe>(list,recipe!);return;}
        entry.List??=list;entry.Checks++;
        // A subclass may override equality (including asymmetric/stateful equality).
        // Once one is encountered, preserve the native List.Contains path thereafter.
        if(recipe==null || recipe.GetType()!=typeof(GridRecipe))entry.Custom=true;
        if(entry.Custom || (!entry.Hashed&&list.Count<SmallListLimit))
        {ListExtensions.AddIfNotPresent<GridRecipe>(list,recipe!);return;}
        if(!entry.Hashed)
        {
            foreach(var previous in list)entry.Seen.Add(previous);
            entry.Hashed=true;
        }
        if(entry.Seen.Add(recipe!))list.Add(recipe!);else entry.Duplicates++;
    }
    public static Exception? Finalizer(Exception? __exception,Scope __state)
    {
        active=__state.Parent;var entry=__state.Entry;
        if(entry==null)return __exception;
        Interlocked.Increment(ref queries);Interlocked.Add(ref checks,entry.Checks);Interlocked.Add(ref duplicates,entry.Duplicates);
        if(entry.Hashed)Interlocked.Increment(ref hashedQueries);if(entry.Custom)Interlocked.Increment(ref fallbacks);
        if(__exception!=null){Interlocked.Increment(ref failures);enabled=false;}
        entry.List=null;entry.Candidates.Clear();entry.Seen.Clear();entry.Checks=entry.Duplicates=0;entry.Hashed=entry.Custom=false;
        if(entry.Seen.EnsureCapacity(0)>MaxRetainedRecipes || entry.Candidates.Capacity>MaxRetainedRecipes)Interlocked.Increment(ref oversized);
        else if(enabled&&entry.Generation==generation&&idleCount<MaxRetainedContexts){entry.Next=idle;idle=entry;idleCount++;}
        return __exception;
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
    {
        var code=source.ToList();int count=0,allocations=0;
        foreach(var instruction in code)if(instruction.Calls(NativeAdd))
        {instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(CraftingCandidateOptimizations),nameof(AddCandidate));count++;}
        foreach(var instruction in code)if(instruction.opcode==OpCodes.Newobj&&Equals(instruction.operand,typeof(List<GridRecipe>).GetConstructor(Type.EmptyTypes)))
        {instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(CraftingCandidateOptimizations),nameof(RentCandidates));allocations++;}
        if(count!=1||allocations!=1)throw new InvalidOperationException("Unexpected crafting candidate layout; retain stock.");
        return code;
    }
}
