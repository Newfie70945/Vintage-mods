using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.MathTools;
using Vintagestory.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var d in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(d,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
var defaults=new NewfiesOptimizer.OptimizerConfig();
if(defaults.EngineOptimizations.BlockUpdateDiagnostics||defaults.EngineOptimizations.BlockUpdateDiagnosticsMs!=50)throw new Exception("Block update diagnostics must be opt-in at 50 ms.");
Checks.Run();
static class Checks
{
 static int checks;static void Assert(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 public static int SleepMs, HandlerCalls;public static bool Throw;
 public static bool SlowHandle(){HandlerCalls++;Thread.Sleep(SleepMs);if(Throw)throw new InvalidOperationException("block-update-sentinel");return false;}
 public static bool SkipSend(){Thread.Sleep(5);return false;}
 public static void Run()
 {
  Assert(BlockUpdateDiagnostics.ValidateContract(out var why),why);
  var server=(ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(ServerMain));
  void Set(string field,object value)=>AccessTools.Field(typeof(ServerMain),field).SetValue(server,value);
  var updated=new Queue<BlockPos>();var dirty=new ConcurrentQueue<Vec4i>();var modified=new ConcurrentQueue<BlockPos>();var noRelight=new ConcurrentQueue<BlockPos>();
  var minimal=new List<BlockPos>();var decors=new ConcurrentQueue<BlockPos>();var entities=new ConcurrentQueue<BlockPos>();
  Set("UpdatedBlocks",updated);Set("DirtyBlocks",dirty);Set("ModifiedBlocks",modified);Set("ModifiedBlocksNoRelight",noRelight);Set("ModifiedBlocksMinimal",minimal);Set("ModifiedDecors",decors);Set("DirtyBlockEntities",entities);
  var system=(ServerSystemBlockSimulation)RuntimeHelpers.GetUninitializedObject(typeof(ServerSystemBlockSimulation));
  AccessTools.Field(typeof(ServerSystem),"server").SetValue(system,server);
  var update=BlockUpdateDiagnostics.Target.CreateDelegate<Action<ServerSystemBlockSimulation,float>>();
  var lines=new List<string>();BlockUpdateDiagnostics.Sink=lines.Add;
  var own=new Harmony("test.nos.blockupdates");
  own.Patch(BlockUpdateDiagnostics.Target,prefix:new HarmonyMethod(typeof(BlockUpdateDiagnostics),nameof(BlockUpdateDiagnostics.Prefix)),finalizer:new HarmonyMethod(typeof(BlockUpdateDiagnostics),nameof(BlockUpdateDiagnostics.Finalizer)));
  own.Patch(BlockUpdateDiagnostics.HandleTarget,prefix:new HarmonyMethod(typeof(BlockUpdateDiagnostics),nameof(BlockUpdateDiagnostics.HandlePrefix)),finalizer:new HarmonyMethod(typeof(BlockUpdateDiagnostics),nameof(BlockUpdateDiagnostics.HandleFinalizer)));
  own.Patch(BlockUpdateDiagnostics.SyncTarget,prefix:new HarmonyMethod(typeof(BlockUpdateDiagnostics),nameof(BlockUpdateDiagnostics.SyncPrefix)),finalizer:new HarmonyMethod(typeof(BlockUpdateDiagnostics),nameof(BlockUpdateDiagnostics.SyncFinalizer)));
  try
  {
   // Native empty pass runs unchanged with or without observation.
   BlockUpdateDiagnostics.Configure(false);update(system,.1f);Assert(BlockUpdateDiagnostics.Counters.Passes==0,"disabled diagnostics observe nothing");
   BlockUpdateDiagnostics.SetThreshold(10);BlockUpdateDiagnostics.ConfigureHandle(true);BlockUpdateDiagnostics.ConfigureSync(true);BlockUpdateDiagnostics.Configure(true);update(system,.1f);
   Assert(BlockUpdateDiagnostics.Counters.Passes==1&&BlockUpdateDiagnostics.Counters.Slow==0&&lines.Count==0,"fast native pass counted, never logged");

   // Simulate a slow changed-block pass with queued work; the stub skips native processing so queues stay countable.
   var fixture=new Harmony("test.nos.blockupdates.fixture");
   fixture.Patch(BlockUpdateDiagnostics.HandleTarget,prefix:new HarmonyMethod(typeof(Checks),nameof(SlowHandle)));
   fixture.Patch(AccessTools.Method(typeof(ServerSystemBlockSimulation),"SendDirtyBlockEntities"),prefix:new HarmonyMethod(typeof(Checks),nameof(SkipSend)));
   for(int i=0;i<3;i++)updated.Enqueue(new BlockPos(i,1,1));for(int i=0;i<1200;i++)modified.Enqueue(new BlockPos(i,2,2));
   for(int i=0;i<40;i++)minimal.Add(new BlockPos(i,3,3));entities.Enqueue(new BlockPos(5,5,5));
   SleepMs=30;update(system,.1f);
   Assert(BlockUpdateDiagnostics.Counters.Slow==1&&lines.Count==1,"slow pass logged once");
   var line=lines[0];
   Assert(line.Contains("neighbour updates 3")&&line.Contains("modified 1200")&&line.Contains("minimal 40")&&line.Contains("dirty block entities 1"),"queued counts reported: "+line);
   double handle=double.Parse(line.Split("changed-block handling ")[1].Split(' ')[0],System.Globalization.CultureInfo.InvariantCulture);
   Assert(handle>=25,"changed-block handling time attributed: "+handle);
   double sync=double.Parse(line.Split("block-entity sync ")[1].Split(' ')[0],System.Globalization.CultureInfo.InvariantCulture);
   Assert(sync>=4&&sync<handle,"sync duration is measured directly, not inferred from residual wall time");
   Assert(line.Contains("remaining/unmeasured")&&line.Contains("include pauses/waits"),"elapsed attribution limits are explicit");
   Assert(updated.Count==3&&modified.Count==1200,"diagnostics never consume queued work");

   // Rate limit: another slow pass within 10 s is counted but not logged.
   update(system,.1f);Assert(BlockUpdateDiagnostics.Counters.Slow==2&&lines.Count==1,"repeat slow pass counted, log rate-limited");

   // Exceptions propagate unchanged and are never swallowed.
   Throw=true;string error;try{update(system,.1f);error="none";}catch(Exception e){error=e.GetType().Name+":"+e.Message;}
   Throw=false;Assert(error=="InvalidOperationException:block-update-sentinel","native exception propagated");
   // A blocked/disabled child observer must remain unavailable, never labelled zero or assigned to sync.
   BlockUpdateDiagnostics.ConfigureHandle(false);BlockUpdateDiagnostics.Configure(true);lines.Clear();update(system,.1f);
   Assert(lines.Single().Contains("changed-block handling unavailable"),"missing child timing stays unavailable");
   BlockUpdateDiagnostics.ConfigureHandle(true);
   // Observer logging failures cannot replace an original native exception.
   long failures=BlockUpdateDiagnostics.Counters.Failures;
   BlockUpdateDiagnostics.Configure(true);BlockUpdateDiagnostics.Sink=_=>throw new IOException("observer-sink-failure");Throw=true;
   try{update(system,.1f);error="none";}catch(Exception e){error=e.GetType().Name+":"+e.Message;}Throw=false;
   Assert(error=="InvalidOperationException:block-update-sentinel","sink error does not replace native exception");
   Assert(BlockUpdateDiagnostics.Counters.Failures==failures+1,"observer logging failure recorded");
   long recorded=BlockUpdateDiagnostics.Counters.Passes;update(system,.1f);
   Assert(BlockUpdateDiagnostics.Counters.Passes==recorded,"failed observer stands down until reapply");
   // A queue-read failure does not prevent the native target from running.
   BlockUpdateDiagnostics.Sink=lines.Add;BlockUpdateDiagnostics.Configure(true);Set("UpdatedBlocks",null!);int calls=HandlerCalls;
   update(system,.1f);Set("UpdatedBlocks",updated);
   Assert(HandlerCalls==calls+1,"observer prefix failure preserves native execution");
   Assert(BlockUpdateDiagnostics.Counters.Failures==failures+2,"prefix failure recorded without propagation");
   fixture.UnpatchAll(fixture.Id);
  }
  finally{BlockUpdateDiagnostics.Configure(false);BlockUpdateDiagnostics.Sink=null;own.UnpatchAll(own.Id);}
  Console.WriteLine($"PASS {checks} block-update diagnostic observation, attribution, rate-limit and exception checks.");
 }
}
