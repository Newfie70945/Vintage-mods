using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var d in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(d,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
var defaults = new NewfiesOptimizer.OptimizerConfig();
if (!defaults.EngineOptimizations.BarrelIngredientPairing || !defaults.EngineOptimizations.WildBushCallbackFlatten || !defaults.EngineOptimizations.AnimationCollisionFlagScan)
    throw new Exception("Validated allocation defaults must be enabled.");
var preserved = JsonSerializer.Deserialize<NewfiesOptimizer.OptimizerConfig>("{\"EngineOptimizations\":{\"BarrelIngredientPairing\":false,\"WildBushCallbackFlatten\":false,\"AnimationCollisionFlagScan\":false}}")!;
preserved.Normalize();
if (preserved.EngineOptimizations.BarrelIngredientPairing || preserved.EngineOptimizations.WildBushCallbackFlatten || preserved.EngineOptimizations.AnimationCollisionFlagScan)
    throw new Exception("Saved opt-outs must remain disabled.");
if (defaults.EngineOptimizations.IdleFirepitFastPath || defaults.EngineOptimizations.AiDistanceThrottle || defaults.EngineOptimizations.ChunkUnloadStagingReuse)
    throw new Exception("Unpromoted modules must remain opt-in.");
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);
 public static T Make<T>(System.Func<MethodInfo,object?[]?,object?> h)where T:class{var p=Create<T,Proxy>();((Proxy)(object)p).Handler=h;return p;}
}
public class HookItem:Item
{
 public Action? Hook; public bool Accept=true;
 public override bool MatchesForCrafting(ItemStack stack,IRecipeBase recipe,IRecipeIngredient ingredient){Hook?.Invoke();return Accept;}
}
public class CustomBarrelRecipe:BarrelRecipe {}
public class CustomFirepit:BlockEntityFirepit {}
public class CustomBush:BlockEntityWildFruitingBush {}
static class Checks
{
 static int checks;static void Assert(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 delegate void Burn(BlockEntityFirepit firepit,float dt);
 delegate void Bush(BlockEntityWildFruitingBush bush,out FarmlandFastForwardUpdate interval,out FarmlandUpdateEnd end);
 delegate List<(ItemSlot,BarrelRecipeIngredient)> Pair(BarrelRecipe recipe,ItemSlot[] slots);
 static string Error(Action action){try{action();return "none";}catch(Exception e){return e.GetType().Name+":"+e.Message;}}
 static object Measure(Action action){for(int i=0;i<20000;i++)action();const int n=100000;long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();for(int i=0;i<n;i++)action();return new{bytesPerCall=(GC.GetAllocatedBytesForCurrentThread()-bytes)/(double)n,nsPerCall=Stopwatch.GetElapsedTime(start).TotalNanoseconds/n};}
 static BlockEntityFirepit Firepit()
 {
  var block=new Block{Code=new("game:firepit-cold")};block.VariantStrict["burnstate"]="cold";
  var api=Proxy.Make<ICoreServerAPI>((m,a)=>m.Name=="get_Side"?EnumAppSide.Server:null);
  var fire=new BlockEntityFirepit{Api=api,Block=block,furnaceTemperature=20,inputStackCookingTime=7};return fire;
 }
 public static void Run()
 {
  for(int i=0;i<3;i++)Assert(BlockTickOptimizations.ValidateContract(i,out var why),why);
  Assert(ChunkUnloadStagingOptimizations.ValidateContract(out var reason),reason);
  var burn=BlockTickOptimizations.Target(0).CreateDelegate<Burn>();var pair=BlockTickOptimizations.Target(1).CreateDelegate<Pair>();var bush=BlockTickOptimizations.Target(2).CreateDelegate<Bush>();
  var harmony=new Harmony("test.nos.blockticks");
  var fire=Firepit();burn(fire,.1f);Assert(fire.inputStackCookingTime==0&&fire.furnaceTemperature==20,"native cold fixture");
  var itemApi=Proxy.Make<ICoreAPI>((m,a)=>null);
  HookItem Item(int id,string code){var item=new HookItem{ItemId=id,Code=new(code)};AccessTools.Field(typeof(CollectibleObject),"api").SetValue(item,itemApi);return item;}
  var a=Item(1,"test:fruit-a");var b=Item(2,"test:fruit-b");var c=Item(3,"test:other");
  ItemSlot Slot(Item? item)=>new(null){Itemstack=item==null?null:new ItemStack(item)};
  BarrelRecipeIngredient Ingredient(string code)=>new(){Code=new(code),Type=EnumItemClass.Item,Quantity=1,MatchingType=EnumRecipeMatchType.Wildcard};
  string Describe(List<(ItemSlot,BarrelRecipeIngredient)> value)=>string.Join('|',value.Select(x=>x.Item1.Itemstack!.Id+":"+x.Item2.Code));
  var recipe=new BarrelRecipe{Ingredients=[Ingredient("test:fruit-*"),Ingredient("test:fruit-b")]};var slots=new[]{Slot(a),Slot(b)};
  var hookOrder=new List<string>();a.Hook=()=>hookOrder.Add("a");b.Hook=()=>hookOrder.Add("b");
  var nativePairs=pair(recipe,slots);string expected=Describe(nativePairs),order=string.Join(',',hookOrder);Assert(nativePairs.Count==2,"native barrel matcher fixture");
  Assert(BarrelCountOptimizations.ValidateContract(out var countWhy),countWhy);
  var countHarmony=new Harmony("test.nos.barrelcount");
  countHarmony.Patch(BarrelCountOptimizations.Target,prefix:new HarmonyMethod(typeof(BarrelCountOptimizations),nameof(BarrelCountOptimizations.Prefix)),finalizer:new HarmonyMethod(typeof(BarrelCountOptimizations),nameof(BarrelCountOptimizations.Finalizer)));
  foreach(var inputs in new[]{Array.Empty<ItemSlot>(),new[]{Slot(a)},new[]{Slot(null),Slot(b)},new[]{Slot(null),Slot(null)}})
  {
   BarrelCountOptimizations.Configure(false);bool stock=recipe.Matches(inputs,out int nativeSize);
   BarrelCountOptimizations.Configure(true);Assert(recipe.Matches(inputs,out int fastSize)==stock&&fastSize==nativeSize,"count rejection preserves native result/output");
  }
  int guardOutput=99;bool result=true;BarrelCountOptimizations.Configure(true);
  Assert(BarrelCountOptimizations.Prefix(recipe,slots,ref guardOutput,ref result,out _)&&guardOutput==99&&result,"matching counts retain native hooks");
  Assert(BarrelCountOptimizations.Prefix(new CustomBarrelRecipe{Ingredients=recipe.Ingredients},new[]{Slot(a)},ref guardOutput,ref result,out _),"custom recipe count guard falls back");
  var mismatch=new[]{Slot(a)};
  var countMeasurements=new List<object>();
  foreach(bool mode in new[]{false,true,false,true})
  {
   BarrelCountOptimizations.Configure(mode);for(int warm=0;warm<1000;warm++)recipe.Matches(mismatch,out _);
   long alloc=GC.GetAllocatedBytesForCurrentThread();var timing=Stopwatch.StartNew();
   for(int repeat=0;repeat<100000;repeat++)if(recipe.Matches(mismatch,out _))throw new Exception("impossible count matched");
   double ns=timing.Elapsed.TotalNanoseconds/100000;long bytes=GC.GetAllocatedBytesForCurrentThread()-alloc;
   countMeasurements.Add(new{enabled=mode,bytesPerCall=bytes/100000.0,nsPerCall=ns});
  }
  Console.WriteLine(JsonSerializer.Serialize(new{barrelCountGuard=countMeasurements,scope="Actual native Matches count mismatch, no player/TPS claim"}));
  countHarmony.UnpatchAll(countHarmony.Id);BarrelCountOptimizations.Configure(false);
  var noBehavior=new BlockEntityWildFruitingBush();bush(noBehavior,out var nativeInterval,out var nativeEnd);Assert(nativeInterval==null&&nativeEnd==null,"native no-behavior bush fixture");
  string[] prefixes=[nameof(BlockTickOptimizations.FirepitPrefix),nameof(BlockTickOptimizations.BarrelPrefix),nameof(BlockTickOptimizations.BushPrefix)];
  string[] finalizers=[nameof(BlockTickOptimizations.FirepitFinalizer),nameof(BlockTickOptimizations.BarrelFinalizer),nameof(BlockTickOptimizations.BushFinalizer)];
  void Mode(bool enabled){for(int i=0;i<3;i++)BlockTickOptimizations.Configure(i,enabled);}
  for(int i=0;i<3;i++)harmony.Patch(BlockTickOptimizations.Target(i),prefix:new HarmonyMethod(typeof(BlockTickOptimizations),prefixes[i]),finalizer:new HarmonyMethod(typeof(BlockTickOptimizations),finalizers[i]));
  Mode(true);fire.inputStackCookingTime=9;burn(fire,.1f);Assert(fire.inputStackCookingTime==0&&fire.furnaceTemperature==20&&BlockTickOptimizations.Counters(0).Calls>0,"cold empty path preserves native write");
  var hot=Firepit();hot.fuelBurnTime=10;hot.furnaceTemperature=100;hot.maxTemperature=1000;Mode(false);burn(hot,.1f);float nativeFuel=hot.fuelBurnTime,nativeTemp=hot.furnaceTemperature;
  hot.fuelBurnTime=10;hot.furnaceTemperature=100;Mode(true);burn(hot,.1f);Assert(hot.fuelBurnTime==nativeFuel&&hot.furnaceTemperature==nativeTemp,"active burning keeps native time/temperature");
  var customFire=new CustomFirepit{Api=fire.Api,Block=fire.Block};long before=BlockTickOptimizations.Counters(0).Fallbacks;burn(customFire,.1f);Assert(BlockTickOptimizations.Counters(0).Fallbacks==before+1,"custom firepit excluded");
  hookOrder.Clear();Assert(Describe(pair(recipe,slots))==expected&&string.Join(',',hookOrder)==order,"barrel matches and collectible hook order unchanged");
  var owned=pair(recipe,slots);owned.Clear();Assert(pair(recipe,slots).Count==2&&nativePairs.Count==2,"matcher results own independent lists");
  foreach(var data in new[]{Array.Empty<ItemSlot>(),new[]{Slot(a)},new[]{Slot(c),Slot(b)},new[]{Slot(null),Slot(b)},new[]{Slot(b),Slot(a)}})
  {Mode(false);var native=Describe(pair(recipe,data));Mode(true);Assert(Describe(pair(recipe,data))==native,"barrel empty/mismatch/order equivalent");}
  a.Accept=false;Assert(pair(recipe,slots).Count==0,"collectible veto preserved");a.Accept=true;
  bool nested=false;a.Hook=()=>{if(nested)return;nested=true;Assert(pair(recipe,new[]{Slot(a),Slot(b)}).Count==2,"recursive pairing isolated");nested=false;};pair(recipe,slots);a.Hook=null;
  int attempts=0;a.Hook=()=>{attempts++;throw new InvalidOperationException("barrel-sentinel");};Assert(Error(()=>pair(recipe,slots))=="InvalidOperationException:barrel-sentinel"&&attempts==1,"hook failure preserved once");
  long previous=BlockTickOptimizations.Counters(1).Calls;a.Hook=null;pair(recipe,slots);Assert(BlockTickOptimizations.Counters(1).Calls==previous,"failed barrel gate falls back until reapply");Mode(true);
  var customRecipe=new CustomBarrelRecipe{Ingredients=recipe.Ingredients};before=BlockTickOptimizations.Counters(1).Fallbacks;pair(customRecipe,slots);Assert(BlockTickOptimizations.Counters(1).Fallbacks==before+1,"custom recipe excluded");
  bush(noBehavior,out var interval,out var end);Assert(interval==null&&end==null&&BlockTickOptimizations.Counters(2).Calls>0,"empty berry callback flattened");
  before=BlockTickOptimizations.Counters(2).Fallbacks;bush(new CustomBush(),out _,out _);Assert(BlockTickOptimizations.Counters(2).Fallbacks==before+1,"custom bush excluded");
  (BlockEntityWildFruitingBush Entity,BEBehaviorFruitingBush Behavior,System.Func<int> Dirty) BushFixture(EnumFruitingBushGrowthState state)
  {
   int dirty=0;
   var accessor=Proxy.Make<IBlockAccessor>((m,a)=>{if(m.Name=="MarkBlockEntityDirty"||m.Name=="MarkBlockDirty")dirty++;return null;});
   var calendar=Proxy.Make<IGameCalendar>((m,a)=>m.Name=="get_TotalDays"?100d:m.Name=="get_HoursPerDay"?24f:m.Name=="get_DaysPerMonth"?9:m.Name=="get_DaysPerYear"?108:null);
   var world=Proxy.Make<IServerWorldAccessor>((m,a)=>m.Name=="get_BlockAccessor"?accessor:m.Name=="get_Calendar"?calendar:null);
   var api=Proxy.Make<ICoreServerAPI>((m,a)=>m.Name=="get_World"?world:m.Name=="get_Side"?EnumAppSide.Server:null);
   var block=new Block{Code=new("game:berrybush-test")};var entity=new BlockEntityWildFruitingBush{Api=api,Block=block,Pos=new(1,2,3)};
   var behavior=new BEBehaviorFruitingBush(entity){Api=api};
   var definition=new BlockBehaviorFruitingBush(block){PauseGrowthBelowTemperature=5,PauseGrowthAboveTemperature=35,ResetGrowthBelowTemperature=-20,ResetGrowthAboveTemperature=50,GoDormantBelowTemperature=-10,LeaveDormantAboveTemperature=8,growthStageMonths=Enumerable.Range(0,6).Select(_=>NatFloat.createUniform(1,0)).ToArray()};
   AccessTools.Field(typeof(BEBehaviorFruitingBush),"bhBush").SetValue(behavior,definition);AccessTools.Field(typeof(BEBehaviorFruitingBush),"soilPos").SetValue(behavior,new BlockPos(1,1,3));
   behavior.BState.WildBushState=EnumFruitingBushHealthState.Healthy;behavior.BState.PrevHealthState=EnumFruitingBushHealthState.Healthy;behavior.BState.Growthstate=state;behavior.BState.TransitionHoursLeft=100;behavior.BState.MatureTotalDays=200;behavior.BState.Traits=[];
   entity.Behaviors.Add(behavior);return(entity,behavior,()=>dirty);
  }
  string BerryResult(bool on,EnumFruitingBushGrowthState state,float temperature)
  {
   Mode(on);var f=BushFixture(state);bush(f.Entity,out var callback,out var finish);Assert(callback!=null,"native growth callback remains present");
   callback!(3.5,new ClimateCondition{Temperature=temperature},.75,false);finish?.Invoke();var value=f.Behavior.BState;
   return $"{value.Growthstate}:{value.TransitionHoursLeft:R}:{value.MatureTotalDays:R}:{value.PrevHealthState}:{f.Dirty()}";
  }
  foreach(var state in new[]{EnumFruitingBushGrowthState.Young,EnumFruitingBushGrowthState.Mature,EnumFruitingBushGrowthState.Ripe,EnumFruitingBushGrowthState.Dormant})
   foreach(float temp in new[]{-30f,0,20,40,100})Assert(BerryResult(false,state,temp)==BerryResult(true,state,temp),"berry growth/young/dormancy/climate/dirty behavior equals native");
  Mode(true);var fresh=BushFixture(EnumFruitingBushGrowthState.Young);bush(fresh.Entity,out var firstCallback,out _);fresh.Behavior.BState.Growthstate=EnumFruitingBushGrowthState.Mature;bush(fresh.Entity,out var secondCallback,out _);
  Assert(!ReferenceEquals(firstCallback,secondCallback),"per-batch berry closure stays fresh; never cached");


  Mode(false);var nullFire=Firepit();nullFire.Block=null!;var nativeFireError=Error(()=>burn(nullFire,.1f));Mode(true);
  Assert(Error(()=>burn(nullFire,.1f))==nativeFireError&&BlockTickOptimizations.Counters(0).Failures>0,"firepit failure retains native exception");
  var gatedFire=Firepit();long fireCalls=BlockTickOptimizations.Counters(0).Calls;burn(gatedFire,.1f);Assert(BlockTickOptimizations.Counters(0).Calls==fireCalls,"firepit failure disables fast path until reapply");Mode(true);
  var faultBush=new BlockEntityWildFruitingBush();faultBush.Behaviors.Add(new BEBehaviorFruitingBush(faultBush));Mode(false);var nativeBerryError=Error(()=>bush(faultBush,out _,out _));Mode(true);
  Assert(Error(()=>bush(faultBush,out _,out _))==nativeBerryError&&BlockTickOptimizations.Counters(2).Failures>0,"bush setup failure retains native exception");long bushCalls=BlockTickOptimizations.Counters(2).Calls;bush(noBehavior,out _,out _);Assert(BlockTickOptimizations.Counters(2).Calls==bushCalls,"failed bush gate keeps stock until reapply");Mode(true);
  a.Hook=b.Hook=null;
  var activeBush=BushFixture(EnumFruitingBushGrowthState.Mature);
  var measurements=new List<object>();
  for(int trial=0;trial<3;trial++)foreach(bool on in new[]{false,true}){Mode(on);measurements.Add(new{trial,enabled=on,coldFirepit=Measure(()=>burn(fire,.1f)),barrelPairing=Measure(()=>pair(recipe,slots)),emptyBush=Measure(()=>bush(noBehavior,out _,out _)),activeBushBatch=Measure(()=>bush(activeBush.Entity,out _,out _))});}
  Mode(false);harmony.UnpatchAll(harmony.Id);
  var output=new{checks,measurements,tieredCompilation=Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")??"default",scope="Actual official callbacks with isolated native containers/recipes and custom collectible hooks. No player workload or production TPS claim."};
  string json=JsonSerializer.Serialize(output);Console.WriteLine(json);var file=Environment.GetEnvironmentVariable("NOS_MEASUREMENTS");if(file!=null)File.WriteAllText(file,json+"\n");
  Console.WriteLine($"PASS {checks} block callback behavior, custom-type, ownership, recursion and failure checks.");
 }
}
