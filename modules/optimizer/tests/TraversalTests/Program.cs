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
using Vintagestory.GameContent;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var d in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(d,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
PublishedWalkableFixture.Run(true,Checks.Run);
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);
 public static T Make<T>(System.Func<MethodInfo,object?[]?,object?> h)where T:class{var p=Create<T,Proxy>();((Proxy)(object)p).Handler=h;return p;}
}
public class TestItem:Item
{
 public Action<ItemSlot>? Tick;public System.Func<ItemStack,bool>? Requires;
 public override bool RequiresTransitionableTicking(IWorldAccessor w,ItemStack s)=>Requires?.Invoke(s)??true;
 public override TransitionState[] UpdateAndGetTransitionStates(IWorldAccessor w,ItemSlot s){Tick?.Invoke(s);return [];}
}
public class ReverseInventory:InventoryGeneric
{
 public int Disposals;
 public ReverseInventory():base(3,"test-reverse-0",null!){}
 public override IEnumerator<ItemSlot> GetEnumerator(){try{for(int i=Count-1;i>=0;i--)yield return this[i];}finally{Disposals++;}}
}
public class NullInventory:InventoryGeneric
{
 public NullInventory():base(1,"test-null-0",null!){}
 public override IEnumerator<ItemSlot> GetEnumerator()=>null!;
}
public class FailingInventory:InventoryGeneric
{
 public int Disposals;public bool FailDispose;
 public FailingInventory():base(1,"test-fail-0",null!){}
 public override IEnumerator<ItemSlot> GetEnumerator()=>new Iterator(this);
 private sealed class Iterator(FailingInventory owner):IEnumerator<ItemSlot>
 {
  public ItemSlot Current=>throw new InvalidOperationException("current-sentinel");
  object System.Collections.IEnumerator.Current=>Current;
  public bool MoveNext()=>true;
  public void Reset()=>throw new NotSupportedException();
  public void Dispose(){owner.Disposals++;if(owner.FailDispose)throw new InvalidOperationException("dispose-sentinel");}
 }
}
public class Container:InWorldContainer
{
 public bool? Force;public int Cleared;
 public Container(InventoryBase inv,ICoreAPI api,Action sync):base(()=>inv,"fixture"){Api=api;positionProvider=()=>new BlockPos(0);onRequireSyncToClient=sync;roomReg=(RoomRegistry)RuntimeHelpers.GetUninitializedObject(typeof(RoomRegistry));}
 protected override bool HasTransitionables()=>Force??base.HasTransitionables();
 public bool Scan()=>base.HasTransitionables();
 protected override void OnInventoryClearedMidTick(){Cleared++;}
}
public class ColliderBlock:Block
{
 public Action<string>? Log;
 public override void OnEntityCollide(IWorldAccessor world,Entity entity,BlockPos pos,BlockFacing face,Vec3d motion,bool first){Log?.Invoke(face.Code+":"+motion.X+","+motion.Y+","+motion.Z+":"+first);}
}
public class Tester:CollisionTester
{
 public int Generated;public ColliderBlock Block=new();
 protected override void GenerateCollisionBoxList(IBlockAccessor a,double x,double y,double z,float step,float extra,int dim)
 {Generated++;CollisionBoxList.Clear();CollisionBoxList.Add(new Cuboidf(0,0,0,1,1,1),0,0,0,Block);CollisionBoxList.Add(new Cuboidf(1,1,0,2,3,1),0,0,0,Block);}
}
static class Checks
{
 static int checks;static readonly Harmony own=new("test.nos.traversals"),fixture=new("test.nos.traversals.fixture");
 static readonly IWorldAccessor world=Proxy.Make<IWorldAccessor>((m,a)=>null);
 static readonly ICoreAPI api=Proxy.Make<ICoreAPI>((m,a)=>m.Name=="get_World"?world:m.Name=="get_Side"?EnumAppSide.Server:null);
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 static bool Room(ref Room __result){__result=(Room)RuntimeHelpers.GetUninitializedObject(typeof(Room));return false;}
 static void Mode(bool on)
 {
  own.UnpatchAll(own.Id);string[] prefixes=[nameof(TraversalOptimizations.CollisionPrefix),nameof(TraversalOptimizations.ScanPrefix),nameof(TraversalOptimizations.TickPrefix)],finalizers=[nameof(TraversalOptimizations.CollisionFinalizer),nameof(TraversalOptimizations.ScanFinalizer),nameof(TraversalOptimizations.TickFinalizer)];
  for(int i=0;i<3;i++){TraversalOptimizations.Configure(i,on);if(on)own.Patch(TraversalOptimizations.Target(i),prefix:new HarmonyMethod(typeof(TraversalOptimizations),prefixes[i]),transpiler:new HarmonyMethod(typeof(TraversalOptimizations),nameof(TraversalOptimizations.Transpiler)),finalizer:new HarmonyMethod(typeof(TraversalOptimizations),finalizers[i]));}
 }
 static TestItem Item(int id)=>new(){ItemId=id,Code=new("test:item-"+id)};
 static void Fill(InventoryBase inv,TestItem item){for(int i=0;i<inv.Count;i++)inv[i].Itemstack=new(item,i+1);}
 static string InventoryState(InventoryBase inv)=>string.Join(',',Enumerable.Range(0,inv.Count).Select(i=>inv[i].Itemstack?.StackSize??0));
 static object Measure(Action call){for(int i=0;i<4000;i++)call();const int n=30000;long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();for(int i=0;i<n;i++)call();return new{bytesPerCall=(GC.GetAllocatedBytesForCurrentThread()-bytes)/(double)n,nsPerCall=Stopwatch.GetElapsedTime(start).TotalNanoseconds/n};}
 static string Collision(Vec3d motion,int dimension=0)
 {
  var events=new List<string>();var t=new Tester();t.Block.Log=events.Add;var e=new EntityAgent{World=world,CollisionBox=new(-.25f,0,-.25f,.25f,1.8f,.25f),CollidedHorizontally=true,CollidedVertically=true};var p=new EntityPos(.5,1,.5){Dimension=dimension};p.Motion.Set(motion);var output=new Vec3d();t.ApplyTerrainCollision(e,p,1,ref output);
  return $"{output.X},{output.Y},{output.Z}:{e.CollidedHorizontally}:{e.CollidedVertically}:{t.Generated}:{string.Join('|',events)}";
 }
 static (bool Scan,string Order,int Sync,int Cleared,string State,int Disposals) ContainerRun(bool custom,bool clear=false)
 {
  InventoryBase inv=custom?new ReverseInventory():new InventoryGeneric(3,"test-native-0",null!);var item=Item(1);Fill(inv,item);var events=new List<string>();int sync=0;var c=new Container(inv,api,()=>sync++);item.Requires=s=>{events.Add("r"+s.StackSize);return true;};item.Tick=s=>{events.Add("t"+s.Itemstack!.StackSize);if(clear)s.Itemstack=null;};bool scan=c.Scan();c.OnTick(1);return(scan,string.Join(',',events),sync,c.Cleared,InventoryState(inv),inv is ReverseInventory r?r.Disposals:0);
 }
 public static void Run()
 {
  for(int i=0;i<3;i++)Check(TraversalOptimizations.ValidateContract(i,out var why),why);
  fixture.Patch(AccessTools.Method(typeof(RoomRegistry),nameof(RoomRegistry.GetRoomForPosition)),prefix:new HarmonyMethod(typeof(Checks),nameof(Room)));
  Vec3d[] motions=[new(),new(0,-.1,0),new(0,.1,0),new(.7,0,0),new(.7,-.1,.5),new(-.2,-.1,-.2)];
  var expected=motions.Select(m=>Collision(m)).ToArray();var expectedDim=Collision(new(0,-.1,0),1);var stockNative=ContainerRun(false);var stockCustom=ContainerRun(true);var stockClear=ContainerRun(false,true);
  Mode(true);
  for(int i=0;i<motions.Length;i++)Check(Collision(motions[i])==expected[i],"native collision result/flags/hooks preserved for motion "+i);
  Check(Collision(new(0,-.1,0),1)==expectedDim,"collision dimension behavior preserved");Check(ContainerRun(false)==stockNative,"native container scan, update order and inventory unchanged");Check(ContainerRun(true)==stockCustom,"custom reverse enumerator and disposal preserved");Check(ContainerRun(false,true)==stockClear,"mid-tick clearing and sync callbacks preserved");
  var inv=new InventoryGeneric(3,"test-no-transition-0",null!);var item=Item(2);Fill(inv,item);int requires=0;item.Requires=s=>{requires++;return false;};var container=new Container(inv,api,()=>{});
  Check(!container.Scan()&&requires==3,"every nontransitionable slot tested in native order");
  item.Requires=s=>true;Check(container.Scan(),"early exit on first transitionable slot");
  item.Requires=s=>{var nested=TraversalOptimizations.ScanSlots(inv);try{Check(nested.MoveNext(),"nested iterator independent");}finally{nested.Dispose();}Check(!nested.RetainsInputs,"nested disposal clears references");return true;};Check(container.Scan(),"nested traversal does not reuse active iterator");
  int errors=0;item.Requires=s=>{errors++;throw new InvalidOperationException("scan-sentinel");};try{container.Scan();throw new Exception("error swallowed");}catch(InvalidOperationException e){Check(e.Message=="scan-sentinel"&&errors==1,"scan hook exception preserved without replay");}
  var count=TraversalOptimizations.Counters(1).Calls;item.Requires=s=>false;container.Scan();Check(TraversalOptimizations.Counters(1).Calls==count,"scan circuit breaker keeps native traversal until reapply");TraversalOptimizations.Configure(1,true);
  container.Force=true;item.Tick=s=>throw new InvalidOperationException("tick-sentinel");try{container.OnTick(1);throw new Exception("error swallowed");}catch(InvalidOperationException e){Check(e.Message=="tick-sentinel","transition error preserved without replay");}TraversalOptimizations.Configure(2,true);
  var boxes=new CachedCuboidListFaster();boxes.Add(new Cuboidf(0,0,0,1,1,1),0,0,0);var boxA=TraversalOptimizations.Boxes(boxes);var boxB=TraversalOptimizations.Boxes(boxes);Check(boxA.MoveNext()&&boxB.MoveNext(),"independent value box iterators");boxA.Dispose();boxB.Dispose();Check(!boxA.RetainsInputs&&!boxB.RetainsInputs,"box disposal clears references");
  var it=TraversalOptimizations.ScanSlots(inv);Check(it.MoveNext(),"iterator started");TraversalOptimizations.Configure(1,false);TraversalOptimizations.Configure(1,true);while(it.MoveNext()){}it.Dispose();Check(!it.RetainsInputs,"in-flight value iterator disposes after reconfiguration");
  var customInv=new ReverseInventory();long fallback=TraversalOptimizations.Counters(1).Fallbacks;var customIt=TraversalOptimizations.ScanSlots(customInv);while(customIt.MoveNext()){}customIt.Dispose();Check(TraversalOptimizations.Counters(1).Fallbacks==fallback+1&&customInv.Disposals==1,"custom fallback counted and disposed once");
  Parallel.For(0,128,_=>{var local=new InventoryGeneric(1,"thread-fixture-0",null!);var it=TraversalOptimizations.ScanSlots(local);if(!it.MoveNext()||!ReferenceEquals(it.Current,local[0]))throw new Exception("thread leakage");it.Dispose();if(it.RetainsInputs)throw new Exception("reference leak");});Check(true,"thread-local inventory traversal independent and cleared");
  try{TraversalOptimizations.Transpiler([new CodeInstruction(OpCodes.Ret)],TraversalOptimizations.Target(0),new DynamicMethod("bad",typeof(void),Type.EmptyTypes).GetILGenerator()).ToArray();throw new Exception("IL accepted");}catch(InvalidOperationException){Check(true,"unexpected consumer IL rejected");}
  var failing=new FailingInventory();var failContainer=new Container(failing,api,()=>{});
  try{failContainer.Scan();throw new Exception("custom Current error swallowed");}catch(InvalidOperationException e){Check(e.Message=="current-sentinel"&&failing.Disposals==1,"custom Current exception and exactly-once disposal preserved");}TraversalOptimizations.Configure(1,true);
  failing.FailDispose=true;try{failContainer.Scan();throw new Exception("Dispose error swallowed");}catch(InvalidOperationException e){Check(e.Message=="dispose-sentinel"&&failing.Disposals==2,"custom Dispose exception takes native precedence without replay");}TraversalOptimizations.Configure(1,true);
  var nullContainer=new Container(new NullInventory(),api,()=>{});try{nullContainer.Scan();throw new Exception("null iterator accepted");}catch(NullReferenceException){Check(true,"custom null iterator retains native error");}TraversalOptimizations.Configure(1,true);
  var measurements=new List<object>();
  for(int trial=0;trial<4;trial++)
  {
   object Trial(bool enabled){Mode(enabled);var slots=new InventoryGeneric(16,"measure-0",null!);var food=Item(3);food.Requires=_=>false;Fill(slots,food);var scan=new Container(slots,api,()=>{});var tick=new Container(slots,api,()=>{}){Force=true};var t=new Tester();var e=new EntityAgent{World=world,CollisionBox=new(-.25f,0,-.25f,.25f,1.8f,.25f)};var p=new EntityPos(.5,1,.5);var output=new Vec3d();return new{scan=Measure(()=>scan.Scan()),tick=Measure(()=>tick.OnTick(1)),collision=Measure(()=>t.ApplyTerrainCollision(e,p,1,ref output))};}
   object stock,optimized;if(trial%2==0){stock=Trial(false);optimized=Trial(true);}else{optimized=Trial(true);stock=Trial(false);}measurements.Add(new{trial,stock,optimized});var a=JsonSerializer.SerializeToElement(stock);var b=JsonSerializer.SerializeToElement(optimized);foreach(var key in new[]{"scan","tick","collision"}){
    double nativeBytes=a.GetProperty(key).GetProperty("bytesPerCall").GetDouble(),optimizedBytes=b.GetProperty(key).GetProperty("bytesPerCall").GetDouble();
    // Tiered JIT may already elide the native iterator allocation. Require the
    // observed allocation saving, without demanding bytes the native path never allocated.
    Check(nativeBytes-optimizedBytes+.001>=Math.Min(24,nativeBytes),"allocation reduction "+key+" trial "+trial);
   }
  }
  Mode(false);fixture.UnpatchAll(fixture.Id);Console.WriteLine(JsonSerializer.Serialize(new{checks,measurements,scope="Actual native loop bodies, synthetic geometry/inventories and intercepted room lookup; no player-workload claim."}));Console.WriteLine($"PASS {checks} traversal behavior, fallback, ownership and allocation checks.");
 }
}
