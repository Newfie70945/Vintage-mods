using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Essentials;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var path=Path.Combine(folder,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));}return null;};
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? args)=>Handler(m!,args);
 public static T Create<T>(System.Func<MethodInfo,object?[]?,object?> fn)where T:class{var value=Create<T,Proxy>();((Proxy)(object)value).Handler=fn;return value;}
}
static class Checks
{
 static int checks,solved;static bool fail;static int seen;
 static void Assert(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 static bool Solve(EnumAICreatureType creatureType,ref List<Vec3d> __result){if(fail)throw new InvalidOperationException("fixture");seen=(int)creatureType;solved++;__result=new();return false;}
 [MethodImpl(MethodImplOptions.NoInlining)]public static void Run()
 {
  Assert(PathQueueOptimizations.ValidateContract(out var reason),reason);
  var logger=Proxy.Create<ILogger>((m,a)=>null);var profiler=new FrameProfilerUtil((Action<string>)(_=>{}));
  var world=Proxy.Create<IServerWorldAccessor>((m,a)=>m.Name=="get_FrameProfiler"?profiler:m.Name=="get_Logger"?logger:null);
  var api=Proxy.Create<ICoreServerAPI>((m,a)=>m.Name=="get_World"?world:null);
  var owner=new PathfindingAsync();AccessTools.Field(typeof(PathfindingAsync),"api").SetValue(owner,api);
  var astar=(AStar)RuntimeHelpers.GetUninitializedObject(typeof(AStar));AccessTools.Field(typeof(PathfindingAsync),"astar_mainthread").SetValue(owner,astar);
  var harmony=new Harmony("test.nos.path-queue");
  var solve=AccessTools.GetDeclaredMethods(typeof(AStar)).Single(m=>m.Name=="FindPathAsWaypoints"&&m.GetParameters().Length==9);
  harmony.Patch(solve,prefix:new HarmonyMethod(typeof(Checks),nameof(Solve)));
  PathfinderTask[] Seed(int count){owner.PathfinderTasks.Clear();var tasks=Enumerable.Range(0,count).Select(_=>new PathfinderTask(new BlockPos(64,64,64),new BlockPos(66,64,64),0,3,1,new Cuboidf(0,0,0,.5f,1,.5f),64,0,Enum.GetValues<EnumAICreatureType>()[1])).ToArray();foreach(var t in tasks)owner.PathfinderTasks.Enqueue(t);return tasks;}
  var baseline=Seed(101);owner.ProcessQueue(astar,100);Assert(baseline.Count(x=>x.Finished)==100&&owner.PathfinderTasks.IsEmpty&&!baseline[^1].Finished,"stock cap drops the extra queued task in fixture");
  harmony.Patch(PathQueueOptimizations.WorkerTarget,transpiler:new HarmonyMethod(PathQueueOptimizations.WorkerTranspilerMethod));PathQueueOptimizations.ConfigureLimit(true);
  var fixedTasks=Seed(101);owner.ProcessQueue(astar,100);Assert(fixedTasks.Count(x=>x.Finished)==100&&owner.PathfinderTasks.Count==1,"worker cap retains next task");owner.ProcessQueue(astar,100);Assert(fixedTasks.All(x=>x.Finished),"all requests eventually complete");
  var zero=Seed(1);owner.ProcessQueue(astar,0);Assert(owner.PathfinderTasks.Count==1&&!zero[0].Finished,"zero cap does not dequeue");
  harmony.Patch(PathQueueOptimizations.MainTarget,prefix:new HarmonyMethod(PathQueueOptimizations.MainPrefixMethod));
  var tick=PathQueueOptimizations.MainTarget.CreateDelegate<Action<PathfindingAsync,float>>();
  PathQueueOptimizations.SetBudget(2,1,5000);PathQueueOptimizations.ConfigureBudget(true);var waiting=Seed(3);tick(owner,.02f);Assert(waiting.All(x=>!x.Finished)&&owner.PathfinderTasks.Count==3,"main thread defers while head grace is unexpired");
  PathQueueOptimizations.SetBudget(2,1,0);tick(owner,.02f);Assert(waiting.Count(x=>x.Finished)==1&&owner.PathfinderTasks.Count==2,"main assistance count bounded");Assert(seen==(int)waiting[0].CreatureType,"main assistance preserves creature type");
  fail=true;tick(owner,.02f);fail=false;Assert(waiting.Count(x=>x.Finished)==2,"failed path is completed once without losing next request");Assert(PathQueueOptimizations.MainFailures==1,"failed assistance trips a visible failure counter");
  AccessTools.Field(typeof(PathfindingAsync),"isShuttingDown").SetValue(owner,true);tick(owner,.02f);Assert(owner.PathfinderTasks.Count==1,"shutdown does not begin extra work");
  PathQueueOptimizations.ConfigureBudget(false);PathQueueOptimizations.ConfigureLimit(false);harmony.UnpatchAll(harmony.Id);
  Console.WriteLine($"PASS {checks} path queue limit, completion, grace, bounded assistance, failure and shutdown checks. Search solver stubbed; no terrain performance claim.");
 }
}
