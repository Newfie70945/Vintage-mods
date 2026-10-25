using System.Reflection;
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
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Fn=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? a)=>Fn(m!,a);
 public static T Create<T>(System.Func<MethodInfo,object?[]?,object?> fn)where T:class{var p=Create<T,Proxy>();((Proxy)(object)p).Fn=fn;return p;}
}
public class TestEntity:Entity { public override bool IsCreature=>true; public void Setup(EntityProperties p)=>Properties=p; }
public class Behavior(Entity e,Action action):EntityBehavior(e){public override string PropertyName()=>"test";public override void OnEntityDespawn(EntityDespawnData d)=>action();}
static class Checks
{
 static int checks;static void Assert(bool v,string why){if(!v)throw new Exception(why);checks++;}
 static TestEntity Create(List<string> order,bool fail)
 {
  var e=new TestEntity();e.World=Proxy.Create<IWorldAccessor>((m,a)=>m.Name=="get_Side"?EnumAppSide.Server:m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null);
  var props=new EntityProperties{Server=new EntityServerProperties([],new())};e.Setup(props);
  props.Server.Behaviors.Add(new Behavior(e,()=>{order.Add("first");if(fail)throw new InvalidOperationException("native-behavior-failure");}));props.Server.Behaviors.Add(new Behavior(e,()=>order.Add("second")));
  e.AnimManager=Proxy.Create<IAnimationManager>((m,a)=>{if(m.Name=="Dispose")order.Add("dispose");return m.ReturnType==typeof(void)?null:m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null;});
  return e;
 }
 public static void Run()
 {
  Assert(DespawnCleanupTimings.ValidateContract(out var why),why);RuntimeEnv.ServerMainThreadId=Environment.CurrentManagedThreadId;ServerMain.FrameProfiler=new FrameProfilerUtil(_=>{});
  var h=new Harmony("test.despawn-cleanup");h.Patch(DespawnCleanupTimings.Target,transpiler:new HarmonyMethod(typeof(DespawnCleanupTimings),nameof(DespawnCleanupTimings.Transpiler)),finalizer:new HarmonyMethod(typeof(DespawnCleanupTimings),nameof(DespawnCleanupTimings.Finalizer)));
  foreach(bool enabled in new[]{false,true})foreach(bool profiling in new[]{false,true})
  {
   DespawnCleanupTimings.Configure(enabled);ServerMain.FrameProfiler.Enabled=profiling;var order=new List<string>();var e=Create(order,false);if(profiling)ServerMain.FrameProfiler.Enter("root");e.OnEntityDespawn(new EntityDespawnData{Reason=EnumDespawnReason.Unload});if(profiling)ServerMain.FrameProfiler.Leave();Assert(order.SequenceEqual(new[]{"first","second","dispose"}),"cleanup order and exactly-once calls preserved");
   order.Clear();e=Create(order,true);try{e.OnEntityDespawn(new EntityDespawnData());throw new Exception("swallowed");}catch(InvalidOperationException ex){Assert(ex.Message=="native-behavior-failure"&&order.SequenceEqual(new[]{"first"}),"original exception propagates; later cleanup never replayed");}
  }
  ServerMain.FrameProfiler.Enabled=true;DespawnCleanupTimings.Configure(true);var offThreadOrder=new List<string>();var offThread=Create(offThreadOrder,false);long before=DespawnCleanupTimings.Counters.Calls;Task.Run(()=>offThread.OnEntityDespawn(new EntityDespawnData())).GetAwaiter().GetResult();Assert(DespawnCleanupTimings.Counters.Calls==before&&offThreadOrder.Count==3,"off-thread cleanup avoids main-thread profiler");
  ServerMain.FrameProfiler.Enabled=false;h.UnpatchAll(h.Id);DespawnCleanupTimings.Configure(false);Console.WriteLine($"PASS {checks} native despawn cleanup order, exception and profiling checks");
 }
}
