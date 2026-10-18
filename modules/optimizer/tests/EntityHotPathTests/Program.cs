using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Diagnostics;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var path=Path.Combine(folder,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));}return null;};
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? args)=>Handler(m!,args);
 public static T Create<T>(System.Func<MethodInfo,object?[]?,object?> fn)where T:class{var value=Create<T,Proxy>();((Proxy)(object)value).Handler=fn;return value;}
}
public class TestEntity:Entity { public override bool IsCreature=>true; }
public class CustomRepulse(Entity e):EntityBehaviorRepulseAgents(e),ICustomRepulseBehavior { public int Calls; public bool Repulse(Entity owner,Vec3d vector){Calls++;vector.Add(.1,.2,.3);return false;} }
static class Checks
{
 static int checks;
 static void Assert(bool x,string why){if(!x)throw new Exception(why);checks++;}
 delegate bool AiCheck(AiTaskManager owner,IAiTask task);
 static object sizeX=2048,sizeY=256,elapsed=5000L;
 [MethodImpl(MethodImplOptions.NoInlining)]
 public static void Run()
 {
  Assert(AiFilterOptimizations.ValidateContract(out var why),why);Assert(RepulseDelegateOptimizations.ValidateContract(out why),why);
  var harmony=new Harmony("test.nos.entity-hotpaths");
  var ai=AiFilterOptimizations.Target.CreateDelegate<AiCheck>();
  var manager=new AiTaskManager(null!);int called=0;
  ActionBoolReturn<IAiTask> first=_=>{called++;return false;},second=_=>{called++;return true;};
  manager.OnShouldExecuteTask+=first;manager.OnShouldExecuteTask+=second;
  Assert(!ai(manager,null!)&&called==2,"stock veto calls every filter");
  var before=Measure(()=>ai(manager,null!),200000);
  harmony.Patch(AiFilterOptimizations.Target,transpiler:new HarmonyMethod(AiFilterOptimizations.TranspilerMethod));AiFilterOptimizations.Configure(true);
  called=0;Assert(!ai(manager,null!)&&called==2,"optimized veto preserves non-short-circuit callback order");
  var after=Measure(()=>ai(manager,null!),200000);
  var order=new List<int>();var changing=new AiTaskManager(null!);ActionBoolReturn<IAiTask> later=_=>{order.Add(2);return true;};
  changing.OnShouldExecuteTask+=_=>{order.Add(1);changing.OnShouldExecuteTask-=later;return true;};changing.OnShouldExecuteTask+=later;
  ai(changing,null!);Assert(order.SequenceEqual(new[]{1,2}),"subscription mutations preserve current invocation snapshot");order.Clear();ai(changing,null!);Assert(order.SequenceEqual(new[]{1}),"next call sees removed subscription");
  Assert(ai(new AiTaskManager(null!),null!),"no handlers permits execution");
  var throwing=new AiTaskManager(null!);throwing.OnShouldExecuteTask+=_=>throw new InvalidOperationException("sentinel");
  try{ai(throwing,null!);throw new Exception("Missing callback exception");}catch(InvalidOperationException e){Assert(e.Message=="sentinel","caller exception preserved without retry");}
  var block=Proxy.Create<IBlockAccessor>((m,a)=>m.Name=="get_MapSizeY"?sizeY:sizeX);
  var world=Proxy.Create<IServerWorldAccessor>((m,a)=>m.Name=="get_BlockAccessor"?block:m.Name=="get_ElapsedMilliseconds"?elapsed:null);
  var api=Proxy.Create<ICoreServerAPI>((m,a)=>m.Name=="get_World"?world:m.Name=="get_Side"?EnumAppSide.Server:null);
  var partition=new EntityPartitioning();AccessTools.Field(typeof(EntityPartitioning),"api").SetValue(partition,api);
  var entity=(TestEntity)RuntimeHelpers.GetUninitializedObject(typeof(TestEntity));entity.World=world;AccessTools.Property(typeof(Entity),"Pos").SetValue(entity,new EntityPos(64,64,64));entity.State=EnumEntityState.Active;entity.SelectionBox=new Cuboidf(0,0,0,1,1,1);
  var repulse=new EntityBehaviorRepulseAgents(entity);AccessTools.Field(typeof(EntityBehaviorRepulseAgents),"partitionUtil").SetValue(repulse,partition);repulse.ownPosRepulseX=64;repulse.ownPosRepulseY=64;repulse.ownPosRepulseZ=64;
  entity.touchDistance=1;entity.touchDistanceSq=1;
  AccessTools.Field(typeof(EntityBehaviorRepulseAgents),"touchdist").SetValue(repulse,1d);
  var neighbor=(TestEntity)RuntimeHelpers.GetUninitializedObject(typeof(TestEntity));
  AccessTools.Property(typeof(Entity),"Pos").SetValue(neighbor,new EntityPos(64.5,64,64));neighbor.SelectionBox=new Cuboidf(0,0,0,1,1,1);neighbor.touchDistanceSq=1;
  neighbor.BHRepulseAgents=new EntityBehaviorRepulseAgents(neighbor){ownPosRepulseX=64.5,ownPosRepulseY=64,ownPosRepulseZ=64};
  var chunk=new EntityPartitionChunk();chunk.Add(neighbor,0);partition.Partitions[MapUtil.Index3dL(2,2,2,64,64)]=chunk;partition.LargestTouchDistance=1;
  for(int n=0;n<15;n++){
   var other=(TestEntity)RuntimeHelpers.GetUninitializedObject(typeof(TestEntity));AccessTools.Property(typeof(Entity),"Pos").SetValue(other,new EntityPos(64.5+n*.01,64,64));other.SelectionBox=new Cuboidf(0,0,0,1,1,1);other.touchDistanceSq=1;
   other.BHRepulseAgents=new EntityBehaviorRepulseAgents(other){ownPosRepulseX=64.5+n*.01,ownPosRepulseY=64,ownPosRepulseZ=64};chunk.Add(other,0);
  }
  var tick=RepulseDelegateOptimizations.Target.CreateDelegate<Action<EntityBehaviorRepulseAgents,float>>();
  tick(repulse,.033f);double stockPush=entity.Pos.Motion.X;entity.Pos.Motion.Set(0,0,0);
  var repBefore=Measure(()=>tick(repulse,.033f),200000);
  harmony.Patch(RepulseDelegateOptimizations.Target,transpiler:new HarmonyMethod(RepulseDelegateOptimizations.TranspilerMethod));RepulseDelegateOptimizations.Configure(true);
  entity.Pos.Motion.Set(0,0,0);tick(repulse,.033f);Assert(entity.Pos.Motion.X==stockPush&&stockPush!=0,"real collision push is bit-identical to stock");
  var repAfter=Measure(()=>tick(repulse,.033f),200000);
  var originalNeighbors=chunk.Entities[0].ToArray();
  chunk.Entities[0].RemoveRange(1,chunk.Entities[0].Count-1);
  var custom=new CustomRepulse(neighbor);neighbor.BHRepulseAgents=custom;entity.Pos.Motion.Set(0,0,0);tick(repulse,.033f);
  Assert(custom.Calls==1&&entity.Pos.Motion.X>0,"custom mod repulsion callback remains active and is not replayed");
  neighbor.BHRepulseAgents=custom;neighbor.Pos.SetPos(64.5,64,64);neighbor.Pos.Dimension=1;entity.Pos.Motion.Set(0,0,0);tick(repulse,.033f);
  Assert(custom.Calls==1&&entity.Pos.Motion.X==0,"other dimensions remain excluded");neighbor.Pos.Dimension=0;
  entity.State=EnumEntityState.Inactive;tick(repulse,.033f);Assert(custom.Calls==1,"inactive entities retain stock early return");entity.State=EnumEntityState.Active;
  neighbor.BHRepulseAgents=new EntityBehaviorRepulseAgents(neighbor){ownPosRepulseX=64.5,ownPosRepulseY=64,ownPosRepulseZ=64};
  Assert(ReferenceEquals(RepulseDelegateOptimizations.GetWalk(repulse),RepulseDelegateOptimizations.GetWalk(repulse)),"same behavior reuses callback");
  RepulseDelegateOptimizations.Configure(false);RepulseDelegateOptimizations.Configure(true);
  var refs=WeakCallbacks(entity);GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Assert(refs.All(x=>!x.IsAlive),"weak callback cache does not retain despawned behavior or its delegates");
  RepulseDelegateOptimizations.Configure(false);AiFilterOptimizations.Configure(false);harmony.UnpatchAll(harmony.Id);
  Assert(after.Bytes<before.Bytes,"AI dispatch reduces measured allocation");Assert(repAfter.Bytes<repBefore.Bytes,"stock repulsion tick allocation reduced");
  Console.WriteLine(JsonSerializer.Serialize(new{aiStock=before,aiOptimized=after,repulseStock=repBefore,repulseOptimized=repAfter}));
  chunk.Entities[0].Clear();chunk.Entities[0].AddRange(originalNeighbors);
  var trials=new List<object>();bool patched=false;var empty=new AiTaskManager(null!);
  void Mode(bool value){if(value&&!patched){harmony.Patch(AiFilterOptimizations.Target,transpiler:new HarmonyMethod(AiFilterOptimizations.TranspilerMethod));harmony.Patch(RepulseDelegateOptimizations.Target,transpiler:new HarmonyMethod(RepulseDelegateOptimizations.TranspilerMethod));}else if(!value&&patched)harmony.UnpatchAll(harmony.Id);patched=value;AiFilterOptimizations.Configure(value);RepulseDelegateOptimizations.Configure(value);}
  object Trial(bool value){Mode(value);return new{filteredAi=Measure(()=>ai(manager,null!),300000),unfilteredAi=Measure(()=>ai(empty,null!),300000),repulsion=Measure(()=>tick(repulse,.033f),300000)};}
  for(int i=0;i<5;i++){object stock,optimized;if(i%2==0){stock=Trial(false);optimized=Trial(true);}else{optimized=Trial(true);stock=Trial(false);}trials.Add(new{trial=i,stock,optimized});}
  Mode(false);Console.WriteLine(JsonSerializer.Serialize(trials));
  Console.WriteLine($"{checks} entity/AI correctness and allocation checks passed against actual stock methods; synthetic fixtures, not player TPS.");
 }
 [MethodImpl(MethodImplOptions.NoInlining)]
 static WeakReference[] WeakCallbacks(Entity entity){var owner=new EntityBehaviorRepulseAgents(entity);var a=RepulseDelegateOptimizations.GetWalk(owner);return [new(owner),new(a)];}
 public record Measurement(double Nanoseconds,double Bytes);
 static Measurement Measure(Action action,int count){for(int i=0;i<30000;i++)action();long start=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();for(int i=0;i<count;i++)action();double ns=watch.Elapsed.TotalNanoseconds/count;long bytes=GC.GetAllocatedBytesForCurrentThread()-start;return new(ns,bytes/(double)count);}
}
