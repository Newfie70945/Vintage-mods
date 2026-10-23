using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Server;

string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var path=Path.Combine(folder,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);}return null;};
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? method,object?[]? args)=>Handler(method!,args);
 public static T Make<T>(System.Func<MethodInfo,object?[]?,object?> handler)where T:class{var proxy=Create<T,Proxy>();((Proxy)(object)proxy).Handler=handler;return proxy;}
}
public class Creature:Entity { public override bool IsCreature=>true; }
public class HookItem:Item
{
 public Action? Hook;
 public bool Accept=true;
 public override bool MatchesForCrafting(ItemStack inputStack,IRecipeBase recipe,IRecipeIngredient ingredient){Hook?.Invoke();return Accept;}
}
public class CustomRecipe:GridRecipe
{
 public int Overrides;
 protected override void MergeStacks(ItemSlot[] slots,List<ItemStack> stacks){Overrides++;}
 public void Check(ItemSlot[] slots,List<ItemStack> stacks)=>MergeStacks(slots,stacks);
}
static class Checks
{
 static int checks;
 static readonly Harmony harmony=new("test.nos.recipe-query");
 delegate void Merge(RecipeBase recipe,ItemSlot[] slots,List<ItemStack> stacks);
 delegate bool Wild(RecipeBase recipe,List<ItemStack> stacks,IRecipeIngredient?[] ingredients);
 delegate Entity[] Around(Vintagestory.Common.GameMain world,Vec3d pos,float horizontal,float vertical,ActionConsumable<Entity>? matches);
 delegate Entity[] Cuboid(Vintagestory.Common.GameMain world,BlockPos a,BlockPos b,ActionConsumable<Entity>? matches);
 static readonly Merge merge=RecipeOptimizations.Target(0).CreateDelegate<Merge>();
 static readonly Wild wild=RecipeOptimizations.Target(1).CreateDelegate<Wild>();
 static readonly Around around=EntityQueryScratchOptimizations.Target(0).CreateDelegate<Around>();
 static readonly Cuboid cuboid=EntityQueryScratchOptimizations.Target(1).CreateDelegate<Cuboid>();
 static void Check(bool yes,string why){if(!yes)throw new Exception(why);checks++;}
 static T New<T>()where T:class=>(T)RuntimeHelpers.GetUninitializedObject(typeof(T));
 static void Mode(bool on)
 {
  harmony.UnpatchAll(harmony.Id);
  for(int i=0;i<2;i++)
  {
   RecipeOptimizations.Configure(i,on);EntityQueryScratchOptimizations.Configure(i,on);
   if(!on)continue;
   harmony.Patch(RecipeOptimizations.Target(i),prefix:new HarmonyMethod(typeof(RecipeOptimizations),i==0?nameof(RecipeOptimizations.MergePrefix):nameof(RecipeOptimizations.WildcardPrefix)),finalizer:new HarmonyMethod(typeof(RecipeOptimizations),i==0?nameof(RecipeOptimizations.MergeFinalizer):nameof(RecipeOptimizations.WildcardFinalizer)));
   harmony.Patch(EntityQueryScratchOptimizations.Target(i),prefix:new HarmonyMethod(typeof(EntityQueryScratchOptimizations),i==0?nameof(EntityQueryScratchOptimizations.AroundPrefix):nameof(EntityQueryScratchOptimizations.CuboidPrefix)),transpiler:new HarmonyMethod(typeof(EntityQueryScratchOptimizations),nameof(EntityQueryScratchOptimizations.Transpiler)),finalizer:new HarmonyMethod(typeof(EntityQueryScratchOptimizations),nameof(EntityQueryScratchOptimizations.Finalizer)));
  }
 }
 static readonly IWorldAccessor itemWorld=Proxy.Make<IWorldAccessor>((m,a)=>null);
 static readonly ICoreAPI itemApi=Proxy.Make<ICoreAPI>((m,a)=>m.Name=="get_World"?itemWorld:null);
 static HookItem Item(int id,string code){var item=new HookItem{ItemId=id,Code=new(code)};AccessTools.Field(typeof(CollectibleObject),"api").SetValue(item,itemApi);return item;}
 static ItemStack Stack(Item item,int count=1,int variant=0){var s=new ItemStack(item,count);s.Attributes.SetInt("variant",variant);return s;}
 static ItemSlot Slot(ItemStack? stack)=>new(null){Itemstack=stack};
 static string Describe(IEnumerable<ItemStack> stacks)=>string.Join('|',stacks.Select(s=>$"{s.Id}:{s.StackSize}:{s.Attributes.GetInt("variant")}"));
 static IRecipeIngredient Ingredient(string pattern,int count=1)=>new CraftingRecipeIngredient{Type=EnumItemClass.Item,Code=new(pattern),Quantity=count,MatchingType=EnumRecipeMatchType.Wildcard};
 static (ServerMain World,Dictionary<(int,int,int),IWorldChunk> Chunks) World(int n=16)
 {
  var chunks=new Dictionary<(int,int,int),IWorldChunk>();
  var access=Proxy.Make<IBlockAccessor>((m,a)=>m.Name=="GetChunk"?chunks.GetValueOrDefault(((int)a![0]!, (int)a[1]!, (int)a[2]!)):m.ReturnType!=typeof(void)&&m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null);
  var world=New<ServerMain>();world.WorldMap=New<ServerWorldMap>();world.WorldMap.RelaxedBlockAccess=access;
  var entities=new Entity[n+2];
  for(int i=0;i<n;i++){var e=New<Creature>();e.EntityId=i+1;e.State=i==3?EnumEntityState.Despawned:EnumEntityState.Active;AccessTools.Property(typeof(Entity),"Pos").SetValue(e,new EntityPos(2+i%6,2+i%4,2+i%5));entities[i]=e;}
  chunks[(0,0,0)]=Proxy.Make<IWorldChunk>((m,a)=>m.Name=="get_Entities"?entities:m.Name=="get_EntitiesCount"?n:m.ReturnType!=typeof(void)&&m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null);
  return(world,chunks);
 }
 static string Ids(IEnumerable<Entity> values)=>string.Join(',',values.Select(e=>e.EntityId));
 static string Error(Action call){try{call();return "none";}catch(Exception e){return e.GetType().Name+":"+(e is ArgumentException arg?arg.ParamName:e.Message);}}
 static object Measure(Action call){for(int i=0;i<3000;i++)call();const int n=20000;long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();for(int i=0;i<n;i++)call();return new{bytesPerCall=(GC.GetAllocatedBytesForCurrentThread()-bytes)/(double)n,nsPerCall=Stopwatch.GetElapsedTime(start).TotalNanoseconds/n};}
 [MethodImpl(MethodImplOptions.NoInlining)]
 public static void Run()
 {
  for(int i=0;i<2;i++){Check(RecipeOptimizations.ValidateContract(i,out var why),why);Check(EntityQueryScratchOptimizations.ValidateContract(i,out why),why);}
  var recipe=new GridRecipe();HookItem a=Item(1,"test:wood-oak"),b=Item(2,"test:wood-pine"),c=Item(3,"test:stone");
  var slots=new[]{Slot(Stack(a,2)),Slot(null),Slot(Stack(a,4)),Slot(Stack(a,3,1)),Slot(Stack(b,2))};
  var destination=new List<ItemStack>{Stack(c,5)};merge(recipe,slots,destination);string expectedMerge=Describe(destination),inputs=Describe(slots.Where(s=>s.Itemstack!=null).Select(s=>s.Itemstack!));
  var hooks=new List<string>();a.Hook=()=>hooks.Add("oak");b.Hook=()=>hooks.Add("pine");c.Hook=()=>hooks.Add("stone");
  var ingredients=new IRecipeIngredient?[]{null,new CraftingRecipeIngredient{MatchingType=EnumRecipeMatchType.Exact},Ingredient("test:wood-*",2),Ingredient("test:stone",1)};
  var wildInput=new List<ItemStack>{Stack(c),Stack(b),Stack(a,2),Stack(c,2)};
  bool stockWild=wild(recipe,wildInput,ingredients);string expectedWild=Describe(wildInput),expectedHooks=string.Join(',',hooks);
  var failedInput=new List<ItemStack>{Stack(a)};hooks.Clear();bool stockFailed=wild(recipe,failedInput,new[]{Ingredient("test:wood-*",2)});string failedHooks=string.Join(',',hooks);
  string nullSlots=Error(()=>merge(recipe,null!,new()));string nullIngredient=Error(()=>wild(recipe,new(),null!));string nullSlot=Error(()=>merge(recipe,new ItemSlot[]{null!},new()));
  var fixture=World();var pos=new Vec3d(4,4,4);BlockPos first=new BlockPos(0,0,0),last=new BlockPos(10,10,10);var order=new List<long>();ActionConsumable<Entity> predicate=e=>{order.Add(e.EntityId);return e.EntityId%2!=0;};
  var stockAround=around(fixture.World,pos,6,4,predicate);var aroundOrder=order.ToArray();order.Clear();var stockCuboid=cuboid(fixture.World,last,first,predicate);var cuboidOrder=order.ToArray();
  var edgeCases=new (Vec3d Pos,float H,float V)[]{(pos,0,0),(new(-1,4,4),6,4),(pos,-1,2),(pos,2,-1),(new(4,32772,4),6,4)};
  var stockEdges=edgeCases.Select(v=>Ids(around(fixture.World,v.Pos,v.H,v.V,null))).ToArray();
  Mode(true);
  destination=new(){Stack(c,5)};merge(recipe,slots,destination);Check(Describe(destination)==expectedMerge,"merge quantities, first match, attributes and initial destination preserved");
  Check(inputs==Describe(slots.Where(s=>s.Itemstack!=null).Select(s=>s.Itemstack!)),"input stacks and attributes remain unchanged");
  Check(!ReferenceEquals(destination[1],slots[0].Itemstack),"merged stacks retain independent clones");
  hooks.Clear();wildInput=new(){Stack(c),Stack(b),Stack(a,2),Stack(c,2)};Check(wild(recipe,wildInput,ingredients)==stockWild&&Describe(wildInput)==expectedWild,"wildcard selection/removal, null and exact ingredients preserved");
  Check(string.Join(',',hooks)==expectedHooks,"crafting hook order preserved, including nonmatching candidates");
  failedInput=new(){Stack(a)};hooks.Clear();Check(wild(recipe,failedInput,new[]{Ingredient("test:wood-*",2)})==stockFailed&&string.Join(',',hooks)==failedHooks&&failedInput.Count==1,"failed quantity check still calls hook and keeps stack");
  a.Accept=false;Check(!wild(recipe,new(){Stack(a,5)},new[]{Ingredient("test:wood-*",2)}),"custom collectible veto honored");a.Accept=true;
  var clone=destination[1];clone.Attributes.SetInt("variant",99);Check(slots[0].Itemstack!.Attributes.GetInt("variant")==0,"cloned attributes do not alias live inventory");
  var custom=new CustomRecipe();custom.Check(slots,new());Check(custom.Overrides==1,"virtual recipe override remains native");
  a.Hook=()=>{if(!wild(recipe,new(){Stack(b,2)},new[]{Ingredient("test:wood-*")}))throw new Exception("nested wildcard failed");};
  Check(wild(recipe,new(){Stack(a,2)},new[]{Ingredient("test:wood-*")}),"recursive crafting hook supported");
  int hookCalls=0;a.Hook=()=>{hookCalls++;throw new InvalidOperationException("hook-sentinel");};
  Check(Error(()=>wild(recipe,new(){Stack(a,2)},new[]{Ingredient("test:wood-*")}))=="InvalidOperationException:hook-sentinel"&&hookCalls==1,"throwing hook is preserved, never replayed");
  var failedCalls=RecipeOptimizations.Counters(1).Calls;a.Hook=null;wild(recipe,new(){Stack(a,2)},new[]{Ingredient("test:wood-*")});Check(RecipeOptimizations.Counters(1).Calls==failedCalls,"error disables wildcard replacement until reapplied");RecipeOptimizations.Configure(1,true);
  Check(Error(()=>merge(recipe,null!,new()))==nullSlots,"null merge array retains native exception");RecipeOptimizations.Configure(0,true);
  Check(Error(()=>wild(recipe,new(),null!))==nullIngredient,"null ingredients retain native exception");RecipeOptimizations.Configure(1,true);
  Check(Error(()=>merge(recipe,new ItemSlot[]{null!},new()))==nullSlot,"null slot retains native exception");RecipeOptimizations.Configure(0,true);
  order.Clear();var actual=around(fixture.World,pos,6,4,predicate);Check(Ids(actual)==Ids(stockAround)&&order.SequenceEqual(aroundOrder),"nearby entity order/filter side effects preserved");order.Clear();
  Check(Ids(cuboid(fixture.World,last,first,predicate))==Ids(stockCuboid)&&order.SequenceEqual(cuboidOrder),"cuboid reversed bounds/order/filter effects preserved");
  for(int i=0;i<edgeCases.Length;i++){var v=edgeCases[i];Check(Ids(around(fixture.World,v.Pos,v.H,v.V,null))==stockEdges[i],"native query edge behavior "+i);}
  var held=around(fixture.World,pos,6,4,null);string heldIds=Ids(held);var later=around(fixture.World,pos,6,4,predicate);Check(!ReferenceEquals(held,later)&&Ids(held)==heldIds,"returned arrays owned by caller and unchanged by subsequent query");
  var nested=around(fixture.World,pos,6,4,e=>{Check(Ids(around(fixture.World,pos,6,4,null))==heldIds,"nested query isolation");return e.EntityId%2!=0;});Check(Ids(nested)==Ids(stockAround),"recursive outer query preserved");
  Parallel.For(0,128,_=>{if(Ids(around(fixture.World,pos,6,4,null))!=heldIds||EntityQueryScratchOptimizations.CurrentThreadRetainsEntities)throw new Exception("thread query leak");});Check(true,"concurrent query state isolated and cleared");
  int predicateCalls=0;Check(Error(()=>around(fixture.World,pos,6,4,e=>{predicateCalls++;throw new InvalidOperationException("query-sentinel");}))=="InvalidOperationException:query-sentinel"&&predicateCalls==1,"query exception preserved without retry");
  Check(!EntityQueryScratchOptimizations.CurrentThreadRetainsEntities,"query failure clears entity references");var count=EntityQueryScratchOptimizations.Counters(0).Calls;around(fixture.World,pos,6,4,null);Check(EntityQueryScratchOptimizations.Counters(0).Calls==count,"query failure disables only affected gate");EntityQueryScratchOptimizations.Configure(0,true);
  var big=World(5000);long oversize=EntityQueryScratchOptimizations.Counters(0).Oversized;Check(around(big.World,pos,6,4,null).Length==4999,"large query keeps every matching entity");Check(EntityQueryScratchOptimizations.Counters(0).Oversized==oversize+1&&!EntityQueryScratchOptimizations.CurrentThreadRetainsEntities,"oversized list discarded and references cleared");
  // Configuration changes during recursion cannot put stale buffers back in use.
  var changed=around(fixture.World,pos,6,4,e=>{EntityQueryScratchOptimizations.Configure(0,false);return true;});Check(Ids(changed)==heldIds,"mid-query disable preserves in-flight native work");EntityQueryScratchOptimizations.Configure(0,true);
  try{EntityQueryScratchOptimizations.Transpiler(new[]{new CodeInstruction(OpCodes.Ret)}).ToArray();throw new Exception("IL accepted");}catch(InvalidOperationException){Check(true,"unknown IL allocation shape rejected");}
  b.Hook=null;c.Hook=null;var measurements=new List<object>();
  for(int trial=0;trial<4;trial++)
  {
   object Trial(bool on){Mode(on);var target=new List<ItemStack>();var wildcardStacks=new List<ItemStack>();var wildcardIngredient=new[]{Ingredient("test:wood-*")};return new{merge=Measure(()=>{target.Clear();merge(recipe,slots,target);}),wildcard=Measure(()=>{wildcardStacks.Clear();wildcardStacks.Add(slots[0].Itemstack!);wild(recipe,wildcardStacks,wildcardIngredient);}),nearby=Measure(()=>around(fixture.World,pos,6,4,null)),cuboid=Measure(()=>cuboid(fixture.World,first,last,null))};}
   object stock,optimized;if(trial%2==0){stock=Trial(false);optimized=Trial(true);}else{optimized=Trial(true);stock=Trial(false);}measurements.Add(new{trial,stock,optimized});
   var left=JsonSerializer.SerializeToElement(stock);var right=JsonSerializer.SerializeToElement(optimized);
   foreach(var key in new[]{"merge","wildcard","nearby","cuboid"})Check(left.GetProperty(key).GetProperty("bytesPerCall").GetDouble()-right.GetProperty(key).GetProperty("bytesPerCall").GetDouble()>=100,"measured allocation reduction for "+key+" trial "+trial);
  }
  Mode(false);Console.WriteLine(JsonSerializer.Serialize(new{checks,measurements,scope="Official helper/query methods, synthetic item/chunk fixtures; not production TPS evidence."}));
  Console.WriteLine($"PASS {checks} recipe/query behavior, ownership, hooks, recursion, failure fallback and allocation checks.");
 }
}
