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
using Vintagestory.Common;
using Vintagestory.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var dir in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){string p=Path.Combine(dir,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);
 public static T Make<T>(System.Func<MethodInfo,object?[]?,object?> h)where T:class{var p=Create<T,Proxy>();((Proxy)(object)p).Handler=h;return p;}
}
public class FixturePlayer:ServerPlayer,IPlayer
{
 public FixturePlayer():base(null!,null!){}
 public EntityPlayer FixtureEntity=null!;
 public new EntityPlayer Entity=>FixtureEntity;
 public new string PlayerUID=>"testuser";
}
public class EqualRecipe:GridRecipe
{
 public static int EqualityCalls;
 public override bool Equals(object? other){EqualityCalls++;return other is GridRecipe;}
 public override int GetHashCode()=>throw new Exception("Custom hash must never be called");
}
public class CustomGrid:InventoryCraftingGrid{public CustomGrid():base("unused",null!) {}}
public class HookItem:Item
{
 public int Consumed,Generated;
 public override void OnConsumedByCrafting(ItemSlot[] all,ItemSlot slot,IRecipeBase recipe,IRecipeIngredient ingredient,IPlayer player,int quantity)
 {Consumed+=quantity;base.OnConsumedByCrafting(all,slot,recipe,ingredient,player,quantity);}
 public override void OnCreatedByCrafting(ItemSlot[] all,ItemSlot output,IRecipeBase recipe)
 {Generated++;base.OnCreatedByCrafting(all,output,recipe);}
}
public class Sink:ItemSlot
{
 public Sink():base(null){}
 public override bool CanTakeFrom(ItemSlot source,EnumMergePriority priority=EnumMergePriority.AutoMerge)=>true;
 public override int GetRemainingSlotSpace(ItemStack stack)=>128-StackSize;
 public override void OnItemSlotModified(ItemStack sinkStack){ }
}
static class Checks
{
 static int checks;static readonly Harmony own=new("test.nos.crafting-candidates");
 delegate void Find(InventoryCraftingGrid grid);
 static Find find=CraftingCandidateOptimizations.Target.CreateDelegate<Find>();
 static void Assert(bool yes,string why){if(!yes)throw new Exception(why);checks++;}
 static T New<T>()where T:class=>(T)RuntimeHelpers.GetUninitializedObject(typeof(T));
 static void Set(object o,string name,object? value)
 {var f=AccessTools.Field(o.GetType(),name);if(f!=null)f.SetValue(o,value);else (AccessTools.Property(o.GetType(),name)??throw new Exception("Missing fixture member "+name)).SetValue(o,value);}
 static void Mode(bool on)
 {
  own.UnpatchAll(own.Id);CraftingCandidateOptimizations.Configure(on);
  if(on)own.Patch(CraftingCandidateOptimizations.Target,prefix:new HarmonyMethod(typeof(CraftingCandidateOptimizations),nameof(CraftingCandidateOptimizations.Prefix)),transpiler:new HarmonyMethod(typeof(CraftingCandidateOptimizations),nameof(CraftingCandidateOptimizations.Transpiler)),finalizer:new HarmonyMethod(typeof(CraftingCandidateOptimizations),nameof(CraftingCandidateOptimizations.Finalizer)));
 }
 sealed class Fixture
 {
  public readonly InventoryCraftingGrid Grid;
  public readonly HookItem Ore=new(){ItemId=1,Code=new("test:ore"),MaxStackSize=64};
  public readonly HookItem Nugget=new(){ItemId=2,Code=new("test:nugget"),MaxStackSize=128};
  public readonly System.Collections.Generic.OrderedDictionary<IRecipeIngredientBase,List<IRecipeBase>> Index=new();
  public readonly List<GridRecipe> Recipes=new();
  public readonly List<int> Order=new();
  public Action<int>? Hook { get; set; }
  public int CraftedEvents;
  public readonly List<int> CraftedAmounts=new();
  public readonly IPlayer Player;
  public bool Trace=true;
  public readonly ICoreAPI Api;
  public Fixture(int count,int groups=3,bool winner=false, bool customGrid=false)
  {
   Grid=customGrid?New<CustomGrid>():New<InventoryCraftingGrid>();
   var events=Proxy.Make<IEventAPI>((m,a)=>{
    if(m.Name=="PushEvent" && (string?)a![0]=="onitemcrafted")
    {CraftedEvents++;var data=(Vintagestory.API.Datastructures.ITreeAttribute)a[1]!;CraftedAmounts.Add(data.GetItemstack("itemstack").StackSize);}
    return m.ReturnType==typeof(bool)?true:null;
   });
   var entity=new EntityPlayer();
   var player=New<FixturePlayer>();player.FixtureEntity=entity;Player=player;
   var world=Proxy.Make<IWorldAccessor>((m,a)=>m.Name=="get_FastSearchRecipesByIngredient"?Index:m.Name=="PlayerByUid"?player:m.Name=="get_Api"?Api:null);
   Api=Proxy.Make<ICoreAPI>((m,a)=>m.Name=="get_World"?world:m.Name=="get_Event"?events:m.Name=="get_Side"?EnumAppSide.Server:null);
   Set(entity,"Api",Api);Set(entity,"World",world);Set(Ore,"api",Api);Set(Nugget,"api",Api);
   Set(Grid,"Api",Api);Set(Grid,"playerUID","testuser");Set(Grid,"GridSize",3);Set(Grid,"GridSizeSq",9);Set(Grid,"dirtySlots",new HashSet<int>());
   var slots=Enumerable.Range(0,9).Select(_=>new ItemSlot(Grid)).ToArray();slots[0].Itemstack=new ItemStack(Ore,64);Set(Grid,"slots",slots);Set(Grid,"outputSlot",new ItemSlotCraftingOutput(Grid));
   for(int i=0;i<count;i++)Recipes.Add(new GridRecipe{Name=new("test:r"+i),Enabled=true});
   if(winner)Recipes[^1]=new GridRecipe{Name=new("test:winner"),Enabled=true,Width=1,Height=1,
    ResolvedIngredients=[new CraftingRecipeIngredient{Id="A",Type=EnumItemClass.Item,Code=Ore.Code,Quantity=1,MatchingType=EnumRecipeMatchType.Exact,ResolvedItemStack=new ItemStack(Ore)}],
    Output=new CraftingRecipeIngredient{Type=EnumItemClass.Item,Code=Nugget.Code,Quantity=4,ResolvedItemStack=new ItemStack(Nugget,4)}};
   for(int j=0;j<groups;j++)
   {
    int group=j;
    var key=Proxy.Make<IRecipeIngredientBase>((m,a)=>{if(m.Name=="SatisfiesAsIngredient"){if(Trace)Order.Add(group);Hook?.Invoke(group);return true;}return m.ReturnType==typeof(bool)?false:null;});
    Index.Add(key,Recipes.Cast<IRecipeBase>().ToList());
   }
  }
 }
 static string Error(Action a){try{a();return "none";}catch(Exception e){return e.GetType().Name+":"+e.Message;}}
 [MethodImpl(MethodImplOptions.NoInlining)]
 public static void Run()
 {
  Assert(CraftingCandidateOptimizations.ValidateContract(out var why),why);
  foreach(int n in new[]{0,1,15,16,17,128,1024})
  {
   var f=new Fixture(n);Mode(false);find(f.Grid);var order=f.Order.ToArray();f.Order.Clear();Mode(true);find(f.Grid);
   Assert(f.Grid.MatchingRecipe==null && f.Grid[9].Empty,"native no-match result "+n);
   Assert(f.Order.SequenceEqual(order),"all ingredient checks retain order "+n);
   Assert(!CraftingCandidateOptimizations.CurrentThreadRetainsRecipes,"all game references cleared "+n);
  }
  var valid=new Fixture(64,winner:true);Mode(false);find(valid.Grid);var expected=valid.Grid.MatchingRecipe;int output=valid.Grid[9].StackSize;var expectedOrder=valid.Order.ToArray();valid.Order.Clear();Mode(true);find(valid.Grid);
  Assert(ReferenceEquals(valid.Grid.MatchingRecipe,expected)&&output==4&&valid.Grid[9].StackSize==output,"real native recipe matches and produces the same output");
  Assert(valid.Order.SequenceEqual(expectedOrder),"valid recipe search keeps ingredient callbacks");
  // Actual native TryPutInto/CraftMany/ConsumeIngredients/GenerateOutputStack and event dispatch.
  object Craft(bool on,int quantity=4)
  {
   Mode(on);var f=new Fixture(128,winner:true){Trace=false};f.Recipes[^1].Output!.Quantity=quantity;f.Recipes[^1].Output!.ResolvedItemStack=new ItemStack(f.Nugget,quantity);int total=Batch(f,4);
   Assert(f.Grid[0].Empty&&f.Grid[9].Empty,"four full stacks consumed; no unearned preview output");
   Assert(total==256*quantity&&f.Ore.Consumed==256,"normal four-stack batch with native consumption hooks");
   Assert(!CraftingCandidateOptimizations.CurrentThreadRetainsRecipes,"bulk craft scratch cleared");
   return new{output=total,consumed=f.Ore.Consumed,generated=f.Nugget.Generated,craftedEvents=f.CraftedEvents,amounts=f.CraftedAmounts};
  }
  var stockCraft=Craft(false);var fastCraft=Craft(true);Assert(JsonSerializer.Serialize(stockCraft)==JsonSerializer.Serialize(fastCraft),"same whole-batch output/consumption/output/event hook counts");
  foreach(int quantity in new[]{6,8})Assert(JsonSerializer.Serialize(Craft(false,quantity))==JsonSerializer.Serialize(Craft(true,quantity)),"native partial-stack leftovers and full outputs preserved for yield "+quantity);
  Mode(true);
  // No search results are cached: changed recipes and stack attributes must be observed immediately.
  valid.Recipes[^1].Enabled=false;find(valid.Grid);Assert(valid.Grid.MatchingRecipe==null,"disabled recipe is observed on the next search");valid.Recipes[^1].Enabled=true;
  valid.Recipes[^1].CopyAttributesFrom="A";valid.Grid[0].Itemstack!.Attributes.SetInt("variant",42);find(valid.Grid);Assert(valid.Grid[9].Itemstack!.Attributes.GetInt("variant")==42,"current ingredient attributes reach native output generation");
  var shaped=valid.Recipes[^1];var shapeless=shaped.Clone();shapeless.Name=new("test:shapeless");shapeless.Shapeless=true;
  foreach(var list in valid.Index.Values)list.Insert(0,shapeless);find(valid.Grid);Assert(ReferenceEquals(valid.Grid.MatchingRecipe,shaped),"native shaped-before-shapeless priority retained");
  shaped.Enabled=false;find(valid.Grid);Assert(ReferenceEquals(valid.Grid.MatchingRecipe,shapeless),"native shapeless fallback retained");
  // Explicit membership comparison includes asymmetric custom equality and native equality-call count.
  var sequence=Enumerable.Range(0,40).Select(_=>new GridRecipe()).ToList();sequence.Insert(18,new EqualRecipe());sequence.AddRange(sequence.ToArray());
  EqualRecipe.EqualityCalls=0;var expectedList=new List<GridRecipe>();foreach(var r in sequence)Vintagestory.API.Util.ListExtensions.AddIfNotPresent(expectedList,r);int equality=EqualRecipe.EqualityCalls;
  EqualRecipe.EqualityCalls=0;var actualList=new List<GridRecipe>();CraftingCandidateOptimizations.Prefix(valid.Grid,out var scope);foreach(var r in sequence)CraftingCandidateOptimizations.AddCandidate(actualList,r);CraftingCandidateOptimizations.Finalizer(null,scope);
  Assert(actualList.Count==expectedList.Count&&actualList.Zip(expectedList).All(p=>ReferenceEquals(p.First,p.Second))&&EqualRecipe.EqualityCalls==equality,"custom equality behavior/order/call count remains native");
  var nested=new Fixture(64);var custom=new Fixture(32,customGrid:true);bool inside=false;int nesting=0;
  nested.Hook=_=>{if(inside)return;inside=true;find(custom.Grid);inside=false;nesting++;};find(nested.Grid);
  Assert(nesting==3&&nested.Grid.MatchingRecipe==null&&!CraftingCandidateOptimizations.CurrentThreadRetainsRecipes,"recursive custom-inventory searches suspend parent scratch and restore it");
  var inner=new Fixture(48);nested.Hook=_=>{if(inside)return;inside=true;find(inner.Grid);inside=false;};find(nested.Grid);
  Assert(!CraftingCandidateOptimizations.CurrentThreadRetainsRecipes,"nested native searches isolate and clear scratch");
  var deep=Enumerable.Range(0,7).Select(_=>new Fixture(24,winner:true){Trace=false}).ToArray();
  for(int i=0;i<deep.Length-1;i++){int next=i+1;deep[i].Hook=group=>{if(group==0)find(deep[next].Grid);};}
  find(deep[0].Grid);
  Assert(deep.All(f=>ReferenceEquals(f.Grid.MatchingRecipe,f.Recipes[^1])),"deep recursion never truncates native searches");
  Assert((int)AccessTools.Field(typeof(CraftingCandidateOptimizations),"idleCount").GetValue(null)!<=CraftingCandidateOptimizations.MaxRetainedContexts&&!CraftingCandidateOptimizations.CurrentThreadRetainsRecipes,"deep recursion honors retention cap with cleared references");
  var changing=new Fixture(64);changing.Hook=_=>CraftingCandidateOptimizations.Configure(false);find(changing.Grid);Assert(changing.Grid.MatchingRecipe==null&&!CraftingCandidateOptimizations.CurrentThreadRetainsRecipes,"mid-search disable clears references and preserves native work");
  CraftingCandidateOptimizations.Configure(true);int called=0;var broken=new Fixture(24);broken.Hook=_=>{called++;throw new InvalidOperationException("ingredient-sentinel");};
  long errors=CraftingCandidateOptimizations.Counters.Failures;Assert(Error(()=>find(broken.Grid))=="InvalidOperationException:ingredient-sentinel"&&called==1,"throwing ingredient callback runs once without replay");
  Assert(CraftingCandidateOptimizations.Counters.Failures==errors+1&&!CraftingCandidateOptimizations.CurrentThreadRetainsRecipes,"callback exception stands down module and clears scratch");
  broken.Hook=null;long before=CraftingCandidateOptimizations.Counters.Queries;find(broken.Grid);Assert(CraftingCandidateOptimizations.Counters.Queries==before,"failed module uses native work until reapplied");
  CraftingCandidateOptimizations.Configure(true);var huge=new Fixture(6000,winner:true);long released=CraftingCandidateOptimizations.Counters.Oversized;find(huge.Grid);
  Assert(ReferenceEquals(huge.Grid.MatchingRecipe,huge.Recipes[^1])&&CraftingCandidateOptimizations.Counters.Oversized==released+1,"retention cap never truncates a large recipe search");
  Assert(!CraftingCandidateOptimizations.CurrentThreadRetainsRecipes,"oversized search clears game references");
  Parallel.For(0,16,_=>{var f=new Fixture(96,winner:true);find(f.Grid);if(!ReferenceEquals(f.Grid.MatchingRecipe,f.Recipes[^1])||CraftingCandidateOptimizations.CurrentThreadRetainsRecipes)throw new Exception("parallel scratch/result mismatch");});Assert(true,"parallel searches retain thread-local ownership");
  Assert(Error(()=>CraftingCandidateOptimizations.Transpiler(new[]{new CodeInstruction(OpCodes.Ret)}).ToArray()).StartsWith("InvalidOperationException"),"unknown IL is rejected");
  var measurements=new List<object>();
  if(Environment.GetEnvironmentVariable("NOS_CRAFT_BENCHMARK")=="1")
  {
   object Trial(bool on)
   {
    Mode(on);var f=new Fixture(3000,8,winner:true){Trace=false};Batch(f,1);
    f.Ore.Consumed=f.Nugget.Generated=f.CraftedEvents=0;f.CraftedAmounts.Clear();
    long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();int output=Batch(f,4);double ms=Stopwatch.GetElapsedTime(start).TotalMilliseconds;long allocated=GC.GetAllocatedBytesForCurrentThread()-bytes;
    Assert(output==1024&&f.Ore.Consumed==256,"measured four-stack output and consumption unchanged");
    return new{milliseconds=ms,allocatedBytes=allocated,output,consumed=f.Ore.Consumed,generated=f.Nugget.Generated,craftedEvents=f.CraftedEvents};
   }
   for(int i=0;i<5;i++){object stock,fast;if(i%2==0){stock=Trial(false);fast=Trial(true);}else{fast=Trial(true);stock=Trial(false);}measurements.Add(new{trial=i,stock,fast});}
  }
  Mode(false);string json=JsonSerializer.Serialize(new{checks,stockCraft,fastCraft,measurements,tieredCompilation=Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")??"default",scope="Official native recipe search and full server-side 64-item crafting batches; synthetic 3000-recipe index and API fixtures, not the owner's xSkills/modpack or authenticated client workload."});Console.WriteLine(json);var outputPath=Environment.GetEnvironmentVariable("NOS_MEASUREMENTS");if(outputPath!=null)File.WriteAllText(outputPath,json+"\n");
 }
 static int Batch(Fixture f,int stacks)
 {
  Set(f.Grid,"isCrafting",true);int output=0;
  for(int i=0;i<stacks;i++)
  {
   f.Grid[0].Itemstack=new ItemStack(f.Ore,64);find(f.Grid);
   var slot=(ItemSlotCraftingOutput)f.Grid[9];var op=new ItemStackMoveOperation(f.Api.World,EnumMouseButton.Left,EnumModifierKey.SHIFT,EnumMergePriority.AutoMerge,9999){ActingPlayer=f.Player};
   var sinks=Enumerable.Range(0,5).Select(_=>new Sink()).ToArray();foreach(var sink in sinks)slot.TryPutInto(sink,ref op);output+=sinks.Sum(s=>s.StackSize);
  }
  Set(f.Grid,"isCrafting",false);find(f.Grid);return output;
 }
}
