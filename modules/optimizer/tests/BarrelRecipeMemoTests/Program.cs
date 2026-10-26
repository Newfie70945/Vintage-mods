using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var d in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(d,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
var defaults=new NewfiesOptimizer.OptimizerConfig();
if(defaults.EngineOptimizations.BarrelRecipeMemo||defaults.EngineOptimizations.BarrelRecipeRescanEvery!=10)throw new Exception("Barrel recipe memo must be opt-in with a 10-check rescan.");
var clamped=JsonSerializer.Deserialize<NewfiesOptimizer.OptimizerConfig>("{\"EngineOptimizations\":{\"BarrelRecipeRescanEvery\":0}}")!;clamped.Normalize();
if(clamped.EngineOptimizations.BarrelRecipeRescanEvery!=2)throw new Exception("Rescan interval must be clamped.");
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);
 public static T Make<T>(System.Func<MethodInfo,object?[]?,object?> h)where T:class{var p=Create<T,Proxy>();((Proxy)(object)p).Handler=h;return p;}
}
public class HookItem:Item
{
 public Action? Hook; public bool Accept;
 public override bool MatchesForCrafting(ItemStack stack,IRecipeBase recipe,IRecipeIngredient ingredient){Hook?.Invoke();return Accept;}
}
public class CustomBarrel:BlockEntityBarrel {}
static class Checks
{
 static int checks;static void Assert(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 static string Error(Action action){try{action();return "none";}catch(Exception e){return e.GetType().Name+":"+e.Message;}}
 public static void Run()
 {
  Assert(BarrelRecipeMemoOptimizations.ValidateContract(out var why),why);
  var tick=BarrelRecipeMemoOptimizations.Target.CreateDelegate<Action<BlockEntityBarrel,float>>();
  var registry=new RecipeRegistrySystem();
  bool failDirty=false;
  var accessor=Proxy.Make<IBlockAccessor>((m,a)=>{if(failDirty)throw new InvalidOperationException("barrel-dirty-sentinel");return null;});
  var world=Proxy.Make<IServerWorldAccessor>((m,a)=>m.Name=="get_BlockAccessor"?accessor:null);
  var loader=Proxy.Make<IModLoader>((m,a)=>m.Name=="GetModSystem"?registry:null);
  var api=Proxy.Make<ICoreServerAPI>((m,a)=>m.Name switch{"get_World"=>world,"get_Side"=>EnumAppSide.Server,"get_ModLoader"=>loader,_=>null});
  var itemApi=Proxy.Make<ICoreAPI>((m,a)=>null);
  HookItem Item(int id,string code){var item=new HookItem{ItemId=id,Code=new(code)};AccessTools.Field(typeof(CollectibleObject),"api").SetValue(item,itemApi);return item;}
  var stored=Item(1,"test:stored");var other=Item(2,"test:other");
  int hooks=0;stored.Hook=()=>hooks++;
  // A long registry where every recipe reaches the collectible hook: one hook call per recipe per native scan.
  const int recipeCount=300;
  for(int i=0;i<recipeCount;i++)registry.BarrelRecipes.Add(new BarrelRecipe{Code="test"+i,SealHours=10,Ingredients=[new(){Code=new("test:*"),Type=EnumItemClass.Item,Quantity=1,MatchingType=EnumRecipeMatchType.Wildcard}],Output=new(){Code=new("test:out"),StackSize=1}});
  T Barrel<T>(Item? item,BlockPos? pos=null) where T:BlockEntityBarrel,new(){var barrel=new T{Api=api,Pos=pos??new BlockPos(1,2,3)};if(item!=null)barrel.Inventory[0].Itemstack=new ItemStack(item);return barrel;}
  var harmony=new Harmony("test.nos.barrelmemo");
  harmony.Patch(BarrelRecipeMemoOptimizations.Target,prefix:new HarmonyMethod(typeof(BarrelRecipeMemoOptimizations),nameof(BarrelRecipeMemoOptimizations.Prefix)),
   postfix:new HarmonyMethod(typeof(BarrelRecipeMemoOptimizations),nameof(BarrelRecipeMemoOptimizations.Postfix)),finalizer:new HarmonyMethod(typeof(BarrelRecipeMemoOptimizations),nameof(BarrelRecipeMemoOptimizations.Finalizer)));
  try
  {
   // Disabled: identical to stock, every tick scans the whole registry.
   BarrelRecipeMemoOptimizations.Configure(false);var plain=Barrel<BlockEntityBarrel>(stored);hooks=0;
   for(int i=0;i<10;i++)tick(plain,3);Assert(hooks==10*recipeCount&&plain.CurrentRecipe==null,"disabled module keeps the native scan on every tick");

   // Enabled: an unchanged storage barrel scans once, then once per rescan interval.
   BarrelRecipeMemoOptimizations.SetRescanEvery(10);BarrelRecipeMemoOptimizations.Configure(true);
   var storage=Barrel<BlockEntityBarrel>(stored);hooks=0;var before=BarrelRecipeMemoOptimizations.Counters;
   for(int i=0;i<20;i++)tick(storage,3);
   var after=BarrelRecipeMemoOptimizations.Counters;
   // Expected schedule: scan once, then a native rescan every 10 checks, offset by the barrel's position phase.
   int phase=BarrelRecipeMemoOptimizations.Phase(storage,10),expectedScans=0,skips=-1;
   for(int i=0;i<20;i++){if(skips>=0&&skips<9)skips++;else{skips=expectedScans==0?phase:0;expectedScans++;}}
   Assert(phase>=0&&phase<9,"position phase stays inside the rescan interval");
   Assert(hooks==expectedScans*recipeCount&&expectedScans<=3,"unchanged contents rescan only on the phased schedule, got "+hooks/recipeCount+" expected "+expectedScans);
   Assert(after.Skipped-before.Skipped==20-expectedScans&&after.Scans-before.Scans==expectedScans&&after.Checks-before.Checks==20,"counters report skips and scans");

   // Barrels that load together must not all rescan on the same tick.
   BarrelRecipeMemoOptimizations.Configure(true);
   var spread=Enumerable.Range(0,1000).Select(i=>Barrel<BlockEntityBarrel>(stored,new BlockPos(512000+i%37*3,110+i%7,511000+i/37*2))).ToArray();
   foreach(var b in spread)tick(b,3);int worst=0;
   for(int round=0;round<30;round++){long s0=BarrelRecipeMemoOptimizations.Counters.Scans;foreach(var b in spread)tick(b,3);worst=Math.Max(worst,(int)(BarrelRecipeMemoOptimizations.Counters.Scans-s0));}
   Assert(worst<=250,"forced rescans spread across ticks; worst round rescanned "+worst+" of 1000 barrels");

   // Any content change forces the very next tick to scan natively.
   hooks=0;storage.Inventory[0].Itemstack!.StackSize++;tick(storage,3);Assert(hooks==recipeCount,"stack size change rescans");
   hooks=0;storage.Inventory[0].Itemstack=new ItemStack(stored);tick(storage,3);Assert(hooks==recipeCount,"replaced stack rescans");
   // Two occupied slots fail native count checks before any hook runs, so count scans directly.
   long scans=BarrelRecipeMemoOptimizations.Counters.Scans;storage.Inventory[1].Itemstack=new ItemStack(other);tick(storage,3);tick(storage,3);
   Assert(BarrelRecipeMemoOptimizations.Counters.Scans==scans+1,"liquid slot change rescans once, then skips");
   hooks=0;storage.Inventory[1].Itemstack=null;tick(storage,3);Assert(hooks==recipeCount,"liquid slot removal rescans");

   // State-only hook changes are picked up by the forced rescan, never later.
   stored.Accept=true;int waited=0;while(storage.CurrentRecipe==null&&waited<20){tick(storage,3);waited++;}
   Assert(storage.CurrentRecipe!=null&&waited<=10,"hook-only change starts the recipe within the rescan interval, waited "+waited);
   stored.Accept=false;storage.CurrentRecipe=null;

   // A barrel with a current recipe never consults the memo.
   var active=Barrel<BlockEntityBarrel>(stored);stored.Accept=true;tick(active,3);Assert(active.CurrentRecipe!=null,"native recipe found");
   long skippedBefore=BarrelRecipeMemoOptimizations.Counters.Skipped;for(int i=0;i<5;i++)tick(active,3);
   Assert(BarrelRecipeMemoOptimizations.Counters.Skipped==skippedBefore,"active recipe barrel untouched");stored.Accept=false;

   // Sealed without a recipe: the skip path unseals exactly as the native remainder does.
   var sealedNative=Barrel<BlockEntityBarrel>(stored);var sealedMemo=Barrel<BlockEntityBarrel>(stored);
   BarrelRecipeMemoOptimizations.Configure(false);tick(sealedNative,3);sealedNative.Sealed=true;tick(sealedNative,3);
   BarrelRecipeMemoOptimizations.Configure(true);tick(sealedMemo,3);sealedMemo.Sealed=true;skippedBefore=BarrelRecipeMemoOptimizations.Counters.Skipped;tick(sealedMemo,3);
   Assert(BarrelRecipeMemoOptimizations.Counters.Skipped==skippedBefore+1&&sealedMemo.Sealed==sealedNative.Sealed&&!sealedMemo.Sealed,"skip path unseals like native");

   // A scan that changes the inventory (e.g. an instant recipe) is never memoized.
   var mutating=Barrel<BlockEntityBarrel>(stored);hooks=0;
   stored.Hook=()=>{hooks++;if(hooks==recipeCount)mutating.Inventory[0].Itemstack!.StackSize++;};
   tick(mutating,3);hooks=0;stored.Hook=()=>hooks++;tick(mutating,3);
   Assert(hooks==recipeCount,"inventory changed during scan forces a native rescan next tick");

   // Empty barrels, custom subclasses and client-side APIs keep the native path.
   var empty=Barrel<BlockEntityBarrel>(null);hooks=0;long checksBefore=BarrelRecipeMemoOptimizations.Counters.Checks;tick(empty,3);
   Assert(hooks==0&&BarrelRecipeMemoOptimizations.Counters.Checks==checksBefore,"empty barrel neither scans nor counts");
   var custom=Barrel<CustomBarrel>(stored);long fallbacks=BarrelRecipeMemoOptimizations.Counters.Fallbacks;hooks=0;
   for(int i=0;i<3;i++)tick(custom,3);Assert(hooks==3*recipeCount&&BarrelRecipeMemoOptimizations.Counters.Fallbacks==fallbacks+3,"custom barrel subclass excluded");

   // Equivalence over a scripted mixed sequence (every step changes contents or recipe state).
   string Script(bool on)
   {
    BarrelRecipeMemoOptimizations.Configure(on);var barrel=Barrel<BlockEntityBarrel>(stored);var log=new List<string>();
    for(int step=0;step<40;step++)
    {
     if(step%7==3)barrel.Inventory[0].Itemstack!.StackSize++;
     if(step%11==5)barrel.Inventory[1].Itemstack=step%2==0?new ItemStack(other):null;
     if(step==20){stored.Accept=true;barrel.Inventory[0].Itemstack!.StackSize++;}
     if(step==30){stored.Accept=false;barrel.CurrentRecipe=null;barrel.Inventory[0].Itemstack=new ItemStack(stored);}
     tick(barrel,3);log.Add((barrel.CurrentRecipe?.Code??"-")+":"+barrel.Sealed+":"+barrel.Inventory[0].StackSize);
    }
    stored.Accept=false;return string.Join(',',log);
   }
   Assert(Script(false)==Script(true),"content-changing sequence matches native results exactly");

   // A throwing mod hook propagates once, is never replayed, and stands the module down.
   BarrelRecipeMemoOptimizations.Configure(true);var faulty=Barrel<BlockEntityBarrel>(stored);int attempts=0;
   stored.Hook=()=>{attempts++;throw new InvalidOperationException("barrel-memo-sentinel");};
   Assert(Error(()=>tick(faulty,3))=="InvalidOperationException:barrel-memo-sentinel"&&attempts==1,"hook failure propagates once");
   Assert(BarrelRecipeMemoOptimizations.Counters.Failures>0,"failure counted for the registry monitor");
   stored.Hook=()=>hooks++;hooks=0;long checksAfterFailure=BarrelRecipeMemoOptimizations.Counters.Checks;for(int i=0;i<3;i++)tick(faulty,3);
   Assert(hooks==3*recipeCount&&BarrelRecipeMemoOptimizations.Counters.Checks==checksAfterFailure,"failed module keeps stock until reapply");

   BarrelRecipeMemoOptimizations.Configure(true);var failedSkip=Barrel<BlockEntityBarrel>(stored);tick(failedSkip,3);failedSkip.Sealed=true;
   failDirty=true;long failuresBefore=BarrelRecipeMemoOptimizations.Counters.Failures;
   Assert(Error(()=>tick(failedSkip,3))=="InvalidOperationException:barrel-dirty-sentinel","skip remainder failure propagates");
   failDirty=false;failedSkip.Sealed=false;
   Assert(BarrelRecipeMemoOptimizations.Counters.Failures==failuresBefore+1,"skip remainder failure stands module down");
   hooks=0;tick(failedSkip,3);tick(failedSkip,3);Assert(hooks==recipeCount*2,"skip failure leaves stock active until reapply");

   // Measurement: 1,000 storage barrels against the 300-recipe registry, twenty 3-second ticks each.
   object Measure(bool on)
   {
    BarrelRecipeMemoOptimizations.SetRescanEvery(10);BarrelRecipeMemoOptimizations.Configure(on);stored.Hook=null;
    var barrels=Enumerable.Range(0,1000).Select(i=>Barrel<BlockEntityBarrel>(stored,new BlockPos(512000+i%37*3,110+i%7,511000+i/37*2))).ToArray();
    foreach(var b in barrels)tick(b,3);
    long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();double worstRound=0;
    for(int round=0;round<20;round++){long r=Stopwatch.GetTimestamp();foreach(var b in barrels)tick(b,3);worstRound=Math.Max(worstRound,Stopwatch.GetElapsedTime(r).TotalMilliseconds);}
    double ms=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    return new{enabled=on,barrels=1000,recipes=recipeCount,msPerTickOfAllBarrels=ms/20,worstTickOfAllBarrelsMs=worstRound,bytesPerBarrelTick=(GC.GetAllocatedBytesForCurrentThread()-bytes)/20000d};
   }
   var measurements=new List<object>();for(int trial=0;trial<3;trial++){measurements.Add(Measure(false));measurements.Add(Measure(true));}
   string json=JsonSerializer.Serialize(new{checks,measurements,scope="Actual official BlockEntityBarrel.OnEvery3Second and BarrelRecipe matching with a synthetic 300-recipe registry and stored items that match no recipe. Not a production TPS claim."});
   Console.WriteLine(json);var file=Environment.GetEnvironmentVariable("NOS_MEASUREMENTS");if(file!=null)File.WriteAllText(file,json+"\n");
  }
  finally{BarrelRecipeMemoOptimizations.Configure(false);harmony.UnpatchAll(harmony.Id);}
  Console.WriteLine($"PASS {checks} barrel recipe memo behavior, change detection, unseal, failure and equivalence checks.");
 }
}
