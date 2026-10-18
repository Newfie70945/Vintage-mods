using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using NewfiesOptimizer;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY");
AssemblyLoadContext.Default.Resolving+=(_,name)=>{
 foreach(string folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){
  string path=Path.Combine(folder,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
 }return null;
};
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? method,object?[]? args)=>Handler(method!,args);
 public static T Create<T>(System.Func<MethodInfo,object?[]?,object?> handler) where T:class {var value=DispatchProxy.Create<T,Proxy>();((Proxy)(object)value).Handler=handler;return value;}
}
static class Checks
{
 static int checks;
 static bool OtherPrefix()=>true;
 static void Assert(bool result,string message){if(!result)throw new Exception(message);checks++;}
 static (ICoreServerAPI Api,List<Action<float>> Ticks) Api(bool failListener=false, bool failUnregister=false)
 {
  var ticks=new List<Action<float>>();
  var logger=Proxy.Create<ILogger>((m,a)=>null);
  var events=Proxy.Create<IServerEventAPI>((m,a)=>{
   if(m.Name=="UnregisterGameTickListener" && failUnregister)throw new InvalidOperationException("test unregister failure");
   if(m.Name=="RegisterGameTickListener") {if(failListener)throw new InvalidOperationException("test listener failure");ticks.Add((Action<float>)a![0]!);return 1L;}
   return m.ReturnType==typeof(void)?null:m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null;
  });
  return (Proxy.Create<ICoreServerAPI>((m,a)=>m.Name=="get_Event"?events:m.Name=="get_Logger"?logger:m.ReturnType==typeof(void)?null:m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null),ticks);
 }
 static OptimizerConfig Config() => new() { EngineOptimizations = new() { NearbyPlayerQueries = true } };
 [MethodImpl(MethodImplOptions.NoInlining)]
 public static void Run()
 {
  Assert(!EngineOptimizations.IsKnownRuntime("StratumLoadTester"),"load-test tools are not mistaken for replacement runtimes");
  Assert(EngineOptimizations.IsKnownRuntime("StratumServer"),"actual runtime recognized");
  var api=Api();
  var masterOff=Config();masterOff.Enabled=false;
  using(var module=new EngineOptimizations(api.Api,masterOff))Assert(module.Modules().All(x=>x.Status=="blocked"),"master off");
  var safe=Config();safe.Compatibility.SafeMode=true;
  using(var module=new EngineOptimizations(api.Api,safe))Assert(module.Modules().All(x=>x.Status=="blocked"),"safe mode");
  var off=Config();off.EngineOptimizations.AiFilterDispatch=off.EngineOptimizations.RepulseDelegateReuse=off.EngineOptimizations.OnlinePlayerSnapshots=off.EngineOptimizations.NearbyPlayerQueries=off.EngineOptimizations.BulkStreamReads=off.EngineOptimizations.AssetCompressionCache=false;
  using(var module=new EngineOptimizations(api.Api,off))Assert(module.Modules().All(x=>x.Status=="disabled"),"independent switches");
  int population=0; var adaptive=Api();
  using(var module=new EngineOptimizations(adaptive.Api,Config(),()=>population))
  {
   Assert(module.Modules().Single(x=>x.Name=="nearbyPlayerQueries").Status=="standby","low population keeps original query unpatched");
   population=200; foreach(var tick in adaptive.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="nearbyPlayerQueries").Status=="active","query activates above threshold");
   population=110; foreach(var tick in adaptive.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="nearbyPlayerQueries").Status=="active","hysteresis avoids threshold churn");
   population=50; foreach(var tick in adaptive.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="nearbyPlayerQueries").Status=="standby","low population unpatches query again");
  }
  var test=Api();var foreign=new Harmony("test.nos.foreign");var prefix=new HarmonyMethod(typeof(Checks).GetMethod(nameof(OtherPrefix),BindingFlags.Static|BindingFlags.NonPublic)!);
  using(var module=new EngineOptimizations(test.Api,Config(),()=>200))
  {
   Assert(module.Modules().All(x=>x.Status=="active"),"verified official targets activate");
   Assert(PlayerQueryOptimizations.SnapshotEnabled&&PlayerQueryOptimizations.AroundEnabled&&StreamCopyOptimizations.ReadEnabled,"runtime gates enabled");
   foreign.Patch(PlayerQueryOptimizations.SnapshotTarget,prefix:prefix);
   foreach(var tick in test.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="onlinePlayerSnapshots").Status=="conflict","late conflict detected");
   Assert(!PlayerQueryOptimizations.SnapshotEnabled,"late conflict disables gate");
   Assert(Harmony.GetPatchInfo(PlayerQueryOptimizations.SnapshotTarget)!.Owners.Contains(foreign.Id),"foreign patch untouched");
   Assert(module.Modules().Where(x=>x.Name!="onlinePlayerSnapshots").All(x=>x.Status=="active"),"unrelated modules remain active");
  }
  foreign.UnpatchAll(foreign.Id);
  Assert(!PlayerQueryOptimizations.AroundEnabled&&!StreamCopyOptimizations.ReadEnabled,"dispose disables gates");
  Assert(!Harmony.GetAllPatchedMethods().Any(m=>Harmony.GetPatchInfo(m)!.Owners.Contains(EngineOptimizations.HarmonyId)),"dispose removes own patches");
  foreign.Patch(StreamCopyOptimizations.ReadConflictTargets[1],prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config()))Assert(module.Modules().Single(x=>x.Name=="bulkStreamReads").Status=="conflict","dependent accessor conflict blocks copy");
  foreign.UnpatchAll(foreign.Id);
  foreign.Patch(AiFilterOptimizations.Target,prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
   Assert(module.Modules().Single(x=>x.Name=="aiFilterDispatch").Status=="conflict","AI foreign ownership blocks only its module");
  foreign.UnpatchAll(foreign.Id);
  var rangeConflict=Api();
  using(var module=new EngineOptimizations(rangeConflict.Api,Config(),()=>200))
  {
   foreign.Patch(AccessTools.Method(typeof(Vintagestory.GameContent.EntityBehaviorRepulseAgents),"IsInRangePartition"),prefix:prefix);
   foreach(var tick in rangeConflict.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="repulseDelegateReuse").Status=="conflict","late range-predicate patch disables repulsion optimization");
   Assert(module.Modules().Where(x=>x.Name!="repulseDelegateReuse").All(x=>x.Status=="active"),"unrelated modules survive repulsion conflict");
  }
  foreign.UnpatchAll(foreign.Id);
  var lost=Api();
  using(var module=new EngineOptimizations(lost.Api,Config(),()=>200))
  {
   new Harmony(EngineOptimizations.HarmonyId).Unpatch(AssetCompressionOptimizations.Target,HarmonyPatchType.All,EngineOptimizations.HarmonyId);
   foreach(var tick in lost.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="assetCompressionCache").Status=="failed","removed own patch is diagnosed");
   Assert(module.Modules().Where(x=>x.Name!="assetCompressionCache").All(x=>x.Status=="active"),"removed patch does not disable unrelated modules");
  }
  var broken=Api(true);
  using(var module=new EngineOptimizations(broken.Api,Config(),()=>200))Assert(module.Modules().All(x=>x.Status=="failed"),"monitor startup failure rolls back all installed modules");
  Assert(!Harmony.GetAllPatchedMethods().Any(m=>Harmony.GetPatchInfo(m)!.Owners.Contains(EngineOptimizations.HarmonyId)),"no orphan patches after monitor failure");
  var idleBroken=Api(true);
  using(var module=new EngineOptimizations(idleBroken.Api,Config(),()=>0))
   Assert(module.Modules().All(x=>x.Status=="failed"),"monitor failure cannot leave a stranded standby module");
  var teardown=Api(failUnregister:true);
  var teardownModule=new EngineOptimizations(teardown.Api,Config(),()=>200);teardownModule.Dispose();
  foreach(var tick in teardown.Ticks)tick(1);
  Assert(!Harmony.GetAllPatchedMethods().Any(m=>Harmony.GetPatchInfo(m)!.Owners.Contains(EngineOptimizations.HarmonyId)),"unregister failure cannot orphan patches; disposed callbacks are inert");
  var failureField=typeof(AssetCompressionOptimizations).GetField("failures",BindingFlags.NonPublic|BindingFlags.Static)!;
  failureField.SetValue(null,10L);
  var retry=Api();
  using(var module=new EngineOptimizations(retry.Api,Config(),()=>200))
  {
   foreach(var tick in retry.Ticks)tick(1);
   Assert(module.Modules().All(x=>x.Status=="active"),"historical failure counts do not poison new activations");
   failureField.SetValue(null,11L);foreach(var tick in retry.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="assetCompressionCache").Status=="failed","new failures still stand module down");
  }
  AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("StratumServer.TestMarker"),AssemblyBuilderAccess.Run);
  using(var module=new EngineOptimizations(api.Api,Config()))Assert(module.Modules().All(x=>x.Status=="blocked"),"alternative runtime blocked");
  Console.WriteLine($"{checks} engine module lifecycle/conflict checks passed against actual Harmony targets.");
 }
}
