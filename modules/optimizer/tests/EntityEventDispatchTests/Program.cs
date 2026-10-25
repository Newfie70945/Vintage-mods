using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var d in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(d,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
Checks.Run();
public class LogProxy:DispatchProxy
{
 public int Errors;
 protected override object? Invoke(MethodInfo? method,object?[]? args){if(method?.Name=="Error")Errors++;return method?.ReturnType==typeof(bool)?false:null;}
}
public class Manager:ServerEventManager
{
 public ILogger Log=null!;
 public Manager():base(null!){}
 public override ILogger Logger=>Log;
}
public class TestEntity:Entity { public override bool IsCreature=>true; }
static class Checks
{
 static int checks, count; static void Assert(bool value,string message){if(!value)throw new Exception(message);checks++;}
 static Manager Create(){var m=new Manager();m.Log=DispatchProxy.Create<ILogger,LogProxy>();return m;}
 static Entity Entity()=> (Entity)RuntimeHelpers.GetUninitializedObject(typeof(TestEntity));
 static void Subscribe(Manager m,int kind,Action callback){if(kind==0)m.OnEntityLoaded+=_=>callback();else m.OnEntityDespawn+=(_,_)=>callback();}
 static Action Invoke(Manager m,int kind){var e=Entity();if(kind==0)return EntityEventDispatchOptimizations.Target(0).CreateDelegate<Action<Entity>>(m).Bind(e);var action=EntityEventDispatchOptimizations.Target(1).CreateDelegate<Action<Entity,EntityDespawnData>>(m);var data=new EntityDespawnData{Reason=EnumDespawnReason.Unload};return ()=>action(e,data);}
 static Action Bind(this Action<Entity> action,Entity e)=>()=>action(e);
 public static void Run()
 {
  var h=new Harmony("test.nos.entity-event");
  for(int kind=0;kind<2;kind++)
  {
   Assert(EntityEventDispatchOptimizations.ValidateContract(kind,out var why),why);
   h.Patch(EntityEventDispatchOptimizations.Target(kind),prefix:new HarmonyMethod(typeof(EntityEventDispatchOptimizations),kind==0?nameof(EntityEventDispatchOptimizations.LoadedPrefix):nameof(EntityEventDispatchOptimizations.DespawnPrefix)),finalizer:new HarmonyMethod(typeof(EntityEventDispatchOptimizations),kind==0?nameof(EntityEventDispatchOptimizations.LoadedFinalizer):nameof(EntityEventDispatchOptimizations.DespawnFinalizer)));
   foreach(bool enabled in new[]{false,true})
   {
    EntityEventDispatchOptimizations.Configure(kind,enabled);var m=Create();var order=new List<int>();
    Subscribe(m,kind,()=>order.Add(1));Subscribe(m,kind,()=>throw new InvalidOperationException("subscriber-sentinel"));Subscribe(m,kind,()=>order.Add(3));
    Invoke(m,kind)();Assert(order.SequenceEqual(new[]{1,3}),"all subscribers retain native order despite exception");Assert(((LogProxy)m.Log).Errors==2,"native two error logging calls retained");
    var mutation=Create();int added=0;Action late=()=>added++;
    Subscribe(mutation,kind,()=>Subscribe(mutation,kind,late));var action=Invoke(mutation,kind);action();Assert(added==0,"subscription additions do not enter current snapshot");action();Assert(added==1,"next event sees added subscriber");
    var removal=Create();int removed=0;
    if(kind==0){EntityDelegate second=_=>removed++;removal.OnEntityLoaded+=_=>removal.OnEntityLoaded-=second;removal.OnEntityLoaded+=second;}
    else{EntityDespawnDelegate second=(_,_)=>removed++;removal.OnEntityDespawn+=(_,_)=>removal.OnEntityDespawn-=second;removal.OnEntityDespawn+=second;}
    action=Invoke(removal,kind);action();action();Assert(removed==1,"removed subscriber finishes captured event only");
    var nested=Create();bool nesting=false;int nestedCalls=0;Action? again=null;
    Subscribe(nested,kind,()=>{if(!nesting){nesting=true;again!();nesting=false;}});Subscribe(nested,kind,()=>nestedCalls++);again=Invoke(nested,kind);again();Assert(nestedCalls==2,"recursive dispatch isolated");
    Invoke(Create(),kind)();Assert(true,"empty dispatch accepted");
   }
   Assert(EntityEventDispatchOptimizations.Counters(kind).Failures==0,"subscriber failures do not trip infrastructure safety");
  }
  // The native profiler remains untouched off-thread, even if globally enabled.
  ServerMain.FrameProfiler=new FrameProfilerUtil(_=>{});RuntimeEnv.ServerMainThreadId=Environment.CurrentManagedThreadId;
  ServerMain.FrameProfiler.Enabled=true;
  var profiled=Create();Subscribe(profiled,0,()=>count++);ServerMain.FrameProfiler.Enter("test-root");Invoke(profiled,0)();Task.Run(Invoke(profiled,0)).GetAwaiter().GetResult();ServerMain.FrameProfiler.Leave();ServerMain.FrameProfiler.Enabled=false;Assert(true,"profiled and off-thread dispatch complete");
  var results=new List<object>();
  for(int kind=0;kind<2;kind++)foreach(int subscribers in new[]{0,1,8})
  {
   var m=Create();for(int i=0;i<subscribers;i++)Subscribe(m,kind,()=>count++);var action=Invoke(m,kind);
   for(int trial=0;trial<3;trial++)foreach(bool enabled in trial%2==0?new[]{false,true}:new[]{true,false})
   {
    EntityEventDispatchOptimizations.Configure(kind,enabled);for(int i=0;i<10000;i++)action();long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();for(int i=0;i<100000;i++)action();results.Add(new{kind,subscribers,trial,enabled,bytesPerEvent=(GC.GetAllocatedBytesForCurrentThread()-bytes)/100000d,nsPerEvent=Stopwatch.GetElapsedTime(start).TotalNanoseconds/100000d});
   }
  }
  h.UnpatchAll(h.Id);for(int i=0;i<2;i++)EntityEventDispatchOptimizations.Configure(i,false);
  string json=JsonSerializer.Serialize(new{checks,results,scope="Actual official 1.22.7 event dispatch, controlled subscribers; not native chunk I/O or production TPS."});Console.WriteLine(json);var file=Environment.GetEnvironmentVariable("NOS_MEASUREMENTS");if(file!=null)File.WriteAllText(file,json+"\n");Console.WriteLine($"PASS {checks} lifecycle dispatch checks");
 }
}
