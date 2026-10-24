using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

static class RecipeVariantComparison
{
 delegate bool Prefix(RecipeBase recipe,List<ItemStack> stacks,IRecipeIngredient?[] ingredients,ref bool result,out bool state);
 public static void Run()
 {
  string previous=Environment.GetEnvironmentVariable("NOS_PREVIOUS_OPTIMIZER_DLL")??throw new Exception("Specify previous release DLL");
  var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(previous));
  var old=assembly.GetType("NewfiesOptimizer.Optimizations.RecipeOptimizations",true)!;
  old.GetMethod("Configure")!.Invoke(null,[1,true]);RecipeOptimizations.Configure(1,true);
  var before=old.GetMethod("WildcardPrefix")!.CreateDelegate<Prefix>();
  var after=typeof(RecipeOptimizations).GetMethod(nameof(RecipeOptimizations.WildcardPrefix))!.CreateDelegate<Prefix>();
  var world=Proxy.Make<IWorldAccessor>((m,a)=>null);var api=Proxy.Make<ICoreAPI>((m,a)=>m.Name=="get_World"?world:null);
  var item=new HookItem{ItemId=1,Code=new("test:wood-oak")};AccessTools.Field(typeof(CollectibleObject),"api").SetValue(item,api);
  int hooks=0;item.Hook=()=>hooks++;
  var stack=new ItemStack(item,64);var recipe=new GridRecipe();
  IRecipeIngredient?[] ingredients={new CraftingRecipeIngredient{Code=new("test:wood-*"),Type=EnumItemClass.Item,Quantity=1,MatchingType=EnumRecipeMatchType.Wildcard,AllowedVariants=new[]{"oak","pine"}}};
  var list=new List<ItemStack>(1);var runs=new List<object>();
  foreach(int trial in Enumerable.Range(0,4))
  foreach(string mode in trial%2==0?new[]{"previous","candidate"}:new[]{"candidate","previous"})
  {
   var call=mode=="previous"?before:after;
   void Invoke(){list.Clear();list.Add(stack);bool result=false;if(call(recipe,list,ingredients,ref result,out _)||!result||list.Count!=0)throw new Exception("Native match result/ownership changed");}
   for(int i=0;i<50000;i++)Invoke();
   long fastBefore=RecipeOptimizations.VariantFastPaths;int hookBefore=hooks;
   const int n=500000;long allocated=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
   for(int i=0;i<n;i++)Invoke();
   double ns=Stopwatch.GetElapsedTime(start).TotalNanoseconds/n;allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
   if(hooks-hookBefore!=n||stack.StackSize!=64)throw new Exception("Crafting hooks or input stack changed");
   if(mode=="candidate"&&RecipeOptimizations.VariantFastPaths-fastBefore!=n)throw new Exception("Variant optimization not exercised");
   runs.Add(new{trial,mode,nsPerMatch=ns,bytesPerMatch=allocated/(double)n});
  }
  string json=JsonSerializer.Serialize(new{previousVersion=assembly.GetName().Version!.ToString(),runs,scope="Actual previous release and current shapeless wildcard prefixes with official matching, native item stacks and a crafting hook. One trailing-star allowed-variant fixture; no production TPS claim."});
  Console.WriteLine(json);string? output=Environment.GetEnvironmentVariable("NOS_RECIPE_COMPARE_REPORT");if(output!=null)File.WriteAllText(output,json+"\n");
 }
}
