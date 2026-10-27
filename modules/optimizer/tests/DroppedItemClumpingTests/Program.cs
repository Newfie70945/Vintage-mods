using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using NewfiesOptimizer;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(string folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){string path=Path.Combine(folder,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);}return null;};
Checks.Run();
public class Proxy:DispatchProxy {
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? method,object?[]? args)=>Handler(method!,args);
 public static T Create<T>(System.Func<MethodInfo,object?[]?,object?> handler)where T:class{var p=Create<T,Proxy>();((Proxy)(object)p).Handler=handler;return p;}
}
public class CustomBehavior(Entity entity):EntityBehavior(entity){public override string PropertyName()=>"despawn";}
static class Checks {
 static int checks;static long nextId;static IServerWorldAccessor world=null!;static ConcurrentDictionary<long,Entity> entities=new();
 static void Check(bool value,string text){if(!value)throw new Exception(text);checks++;}
 static EntityItem Drop(Item item,int count=1,double x=0.2,string? owner=null){var e=new EntityItem{EntityId=++nextId,World=world,OnGround=true,itemSpawnedMilliseconds=0};e.Alive=true;e.Pos.SetPos(x,1.2,0.2);typeof(Entity).GetProperty(nameof(Entity.Properties))!.SetValue(e,new EntityProperties{Server=new EntityServerProperties([],new()){Behaviors=new()}});e.Itemstack=new ItemStack(item,count);e.ByPlayerUid=owner!;return e;}
 static bool Foreign()=>true;
 public static void Run(){
  world=Proxy.Create<IServerWorldAccessor>((m,a)=>m.Name=="get_Side"?EnumAppSide.Server:m.Name=="get_ElapsedMilliseconds"?10000L:m.Name=="get_LoadedEntities"?entities:m.ReturnType==typeof(void)?null:m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null);
  var item=new Item(1){Code=new AssetLocation("game:stone"),MaxStackSize=64};
  var itemApi=Proxy.Create<ICoreAPI>((m,args)=>m.Name=="get_World"?world:null);AccessTools.Field(typeof(CollectibleObject),"api").SetValue(item,itemApi);
  var a=Drop(item,2);var b=Drop(item,3);int moved=DroppedItemClumping.Merge(world,a,b,10000);Check(moved==3&&a.Slot.StackSize==5&&b.Slot.Empty&&!b.Alive,"native merge preserves quantity and removes only empty source");Check(b.DespawnReason.Reason==EnumDespawnReason.Removed,"merge removal is not a death/drop event");Check(ReferenceEquals(a.Itemstack,a.Slot.Itemstack)&&b.Itemstack==null,"watched and slot stack identities stay synchronized");
  a=Drop(item,60);b=Drop(item,10);Check(DroppedItemClumping.Merge(world,a,b,10000)==4&&a.Slot.StackSize==64&&b.Slot.StackSize==6&&b.Alive,"native stack cap and partial source preserved");
  a=Drop(item);b=Drop(item);b.Itemstack.Attributes.SetInt("durability",4);Check(DroppedItemClumping.Merge(world,a,b,10000)==0,"different attributes never merged");
  a=Drop(item,owner:"one");b=Drop(item,owner:"two");Check(DroppedItemClumping.Merge(world,a,b,10000)==0,"different owners excluded");
  a=Drop(item);b=Drop(item,x:1.1);Check(DroppedItemClumping.Merge(world,a,b,10000)==0,"other cells excluded");
  b=Drop(item);b.Pos.Dimension=1;Check(DroppedItemClumping.Merge(world,a,b,10000)==0,"other dimensions excluded");
  b=Drop(item);b.itemSpawnedMilliseconds=9500;Check(DroppedItemClumping.Merge(world,a,b,10000)==0,"pickup grace retained");
  b=Drop(item);b.Pos.Motion.X=.1;Check(!DroppedItemClumping.Eligible(b,10000),"moving drops excluded");
  b=Drop(item);b.Pos.X=double.NaN;Check(!DroppedItemClumping.Eligible(b,10000),"invalid coordinates excluded");
  Check(!DroppedItemClumping.Eligible(Drop(new Item(2){Code=new AssetLocation("game:tool"),MaxStackSize=1}),10000),"nonstackable items excluded");
  b=Drop(item);b.Pos.X=double.MaxValue;Check(!DroppedItemClumping.Eligible(b,10000),"out-of-range finite positions excluded before cell conversion");
  a=Drop(item);b=Drop(item);a.Itemstack.TempAttributes.SetInt("custom",1);Check(DroppedItemClumping.Merge(world,a,b,10000)==0,"different transient attributes excluded");
  a=Drop(item);b=Drop(item);a.Itemstack.Attributes.SetDouble("freshHours",10);b.Itemstack.Attributes.SetDouble("freshHours",2);Check(DroppedItemClumping.Merge(world,a,b,10000)==0,"different freshness metadata excluded");
  a=Drop(item,2);b=Drop(item,2);b.itemSpawnedMilliseconds=1000;Check(DroppedItemClumping.Merge(world,a,b,10000)==2&&a.itemSpawnedMilliseconds==0&&a.Alive,"surviving drop age is not reset");
  b=Drop(item);b.SidedProperties.Behaviors.Add(new CustomBehavior(b));Check(!DroppedItemClumping.Eligible(b,10000),"custom behavior cannot qualify by borrowing a native behavior code");
  var ticks=new List<Action<float>>();int unregisters=0;
  var events=Proxy.Create<IServerEventAPI>((m,args)=>{if(m.Name=="RegisterGameTickListener"){ticks.Add((Action<float>)args![0]!);return (long)ticks.Count;}if(m.Name=="UnregisterGameTickListener"){unregisters++;return null;}return m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null;});
  var api=Proxy.Create<ICoreServerAPI>((m,args)=>m.Name=="get_Event"?events:m.Name=="get_World"?world:m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null);
  using(var disabled=new DroppedItemClumping(api,new(),()=>null))Check(disabled.Status.Status=="disabled"&&ticks.Count==0,"default off has no timer or scanning");
  var config=new OptimizerConfig();config.EngineOptimizations.DroppedItemClumping=true;
  using(var blocked=new DroppedItemClumping(api,config,()=>"unsupported"))Check(blocked.Status.Status=="blocked"&&ticks.Count==0,"engine guard blocks clumping");
  var foreign=new Harmony("test.clump.foreign");foreign.Patch(DroppedItemClumping.Dependencies[0],prefix:new HarmonyMethod(typeof(Checks),nameof(Foreign)));
  using(var conflict=new DroppedItemClumping(api,config,()=>null))Check(conflict.Status.Status=="conflict"&&ticks.Count==0,"foreign slot patch blocks clumping");foreign.UnpatchAll(foreign.Id);
  entities.Clear();for(int i=0;i<3000;i++){var drop=Drop(item);entities[drop.EntityId]=drop;}
  string? guard=null;
  using(var clumper=new DroppedItemClumping(api,config,()=>guard)){
   Check(clumper.Status.Status=="active","supported clumper active");clumper.Tick();
   Check(clumper.Counters["droppedItemsScanned"]<=DroppedItemClumping.MaxScan&&clumper.Counters["droppedItemMerges"]<=DroppedItemClumping.MaxMerges,"per-pass scanning and merging bounded");
   for(int i=0;i<1000;i++)clumper.Tick();
   Check(entities.Values.Cast<EntityItem>().Sum(e=>e.Slot.StackSize)==3000,"3000-drop stress conserves all item quantities");
   int alive=entities.Values.Count(e=>e.Alive);Check(alive<=48,"3000 compatible drops consolidate near native stack limit: "+alive);
   guard="late incompatible runtime";clumper.Tick();Check(clumper.Status.Status=="blocked"&&clumper.Counters["droppedItemCells"]==0,"late guard stops work and clears state");
  }
  Check(unregisters==1,"clumper listener unregistered exactly once");
  using(var failure=new DroppedItemClumping(api,config,()=>throw new InvalidOperationException("guard")))Check(failure.Status.Status=="failed","initialization failures stand down");
  bool throwLater=false;using(var failure=new DroppedItemClumping(api,config,()=>throwLater?throw new InvalidOperationException("guard"):null)){throwLater=true;failure.Tick();Check(failure.Status.Status=="failed"&&failure.Counters["droppedItemClumpingFailures"]==1,"worker failure stands down without replay");long scans=failure.Counters["droppedItemsScanned"];failure.Tick();Check(scans==failure.Counters["droppedItemsScanned"],"failed worker remains inert");}
  Console.WriteLine($"PASS {checks} native dropped-item merge, attribute/owner/dimension/grace, conflict, bounded-work, 3000-drop quantity-conservation and lifecycle checks.");
 }
}
