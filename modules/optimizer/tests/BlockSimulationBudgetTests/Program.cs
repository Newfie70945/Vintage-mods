using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
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
static class Checks
{
 static int checks,logs;static IServerWorldAccessor world=null!;static readonly List<string> seen=new();static int delayMs;static Action<int>? hook;static bool throws;
 static void Assert(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 static bool World(ref IServerWorldAccessor __result){__result=world;return false;}
 static bool Error(){logs++;return false;}
 public class TestBlock:Block
 {
  public override void OnServerGameTick(IWorldAccessor w,BlockPos p,object extra){Record(p,extra);}
 }
 static void Record(BlockPos p,object? extra)
 {
  seen.Add(p.X+":"+(extra??"plain"));var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<delayMs)Thread.SpinWait(64);
  hook?.Invoke(p.X);if(throws&&p.X==1)throw new InvalidOperationException("callback-sentinel");
 }
 static (object Owner,ConcurrentQueue<object> Queue,ServerMain Server) Owner(int count,int cap=1000)
 {
  var server=(ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(ServerMain));
  var phase=AccessTools.Field(typeof(ServerMain),"RunPhase");phase.SetValue(server,Enum.Parse(phase.FieldType,"RunGame"));
  server.Config=(ServerConfig)RuntimeHelpers.GetUninitializedObject(typeof(ServerConfig));server.Config.MaxMainThreadBlockTicks=cap;
  AccessTools.Field(typeof(ServerMain),"api").SetValue(server,RuntimeHelpers.GetUninitializedObject(typeof(ServerCoreAPI)));
  var owner=RuntimeHelpers.GetUninitializedObject(typeof(ServerSystemBlockSimulation));AccessTools.Field(typeof(ServerSystemBlockSimulation),"server").SetValue(owner,server);
  var queue=new ConcurrentQueue<object>();for(int i=0;i<count;i++)queue.Enqueue(new BlockPos(i,0,0));AccessTools.Field(typeof(ServerSystemBlockSimulation),"queuedTicks").SetValue(owner,queue);
  return(owner,queue,server);
 }
 static void Tick(object owner)=>BlockSimulationBudget.Target.CreateDelegate<Action<float>>(owner)(.05f);
 static bool Foreign()=>true;
 public static void Run()
 {
  Assert(BlockSimulationBudget.ValidateContract(out var why),why);
  var transport=new Harmony("test.blockbudget.fixtures");var own=new Harmony("test.blockbudget");
  var block=new TestBlock{Code=new AssetLocation("game:test")};
  var access=Proxy.Make<IBlockAccessor>((m,a)=>m.Name=="GetBlock"?block:null);
  world=Proxy.Make<IServerWorldAccessor>((m,a)=>m.Name=="get_BlockAccessor"?access:null);
  transport.Patch(AccessTools.PropertyGetter(typeof(ServerCoreAPI),"World"),prefix:new HarmonyMethod(typeof(Checks),nameof(World)));
  var logger=AccessTools.Field(typeof(ServerMain),"Logger");logger.SetValue(null,RuntimeHelpers.GetUninitializedObject(typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerLogger",true)!));
  foreach(var error in logger.FieldType.GetMethods().Where(x=>x.Name=="Error"&&x.ReturnType==typeof(void)&&!x.IsAbstract))transport.Patch(AccessTools.DeclaredMethod(error.DeclaringType,error.Name,error.GetParameters().Select(x=>x.ParameterType).ToArray()),prefix:new HarmonyMethod(typeof(Checks),nameof(Error)));
  var baseline=Owner(60);delayMs=2;var timer=Stopwatch.StartNew();Tick(baseline.Owner);double stockMs=timer.Elapsed.TotalMilliseconds;string expected=string.Join('|',seen);Assert(seen.Count==60&&baseline.Queue.IsEmpty,"native fixture drains FIFO");
  own.Patch(BlockSimulationBudget.Target,prefix:new HarmonyMethod(typeof(BlockSimulationBudget),nameof(BlockSimulationBudget.Prefix)),transpiler:new HarmonyMethod(typeof(BlockSimulationBudget),nameof(BlockSimulationBudget.Transpiler)),finalizer:new HarmonyMethod(typeof(BlockSimulationBudget),nameof(BlockSimulationBudget.Finalizer)));
  BlockSimulationBudget.Configure(true);BlockSimulationBudget.SetBudget(5);delayMs=0;for(int warm=0;warm<20;warm++){var fixture=Owner(3);Tick(fixture.Owner);}delayMs=2;var bounded=Owner(60);seen.Clear();var slices=new List<double>();
  timer.Restart();Tick(bounded.Owner);slices.Add(timer.Elapsed.TotalMilliseconds);Assert(seen.Count>0&&seen.Count<60&&bounded.Queue.Count+seen.Count==60,"budget retains pending callbacks and progresses");
  int ticks=1;while(!bounded.Queue.IsEmpty&&ticks<100){timer.Restart();Tick(bounded.Owner);slices.Add(timer.Elapsed.TotalMilliseconds);ticks++;}
  Assert(bounded.Queue.IsEmpty&&string.Join('|',seen)==expected,"later ticks preserve all FIFO callbacks exactly once");
  seen.Clear();delayMs=20;BlockSimulationBudget.SetBudget(1);var expensive=Owner(2);timer.Restart();Tick(expensive.Owner);Assert(seen.Count==1&&expensive.Queue.Count==1&&timer.ElapsedMilliseconds>=20,"one expensive callback may exceed soft budget without interruption");
  delayMs=0;seen.Clear();var capped=Owner(5,2);BlockSimulationBudget.SetBudget(20);Tick(capped.Owner);Assert(seen.Count==2&&capped.Queue.Count==3,"native count cap retained");
  BlockSimulationBudget.Configure(false);seen.Clear();var off=Owner(20);Tick(off.Owner);Assert(seen.Count==20&&off.Queue.IsEmpty,"disabled retains stock");
  BlockSimulationBudget.Configure(true);seen.Clear();var paused=Owner(3);var phase=AccessTools.Field(typeof(ServerMain),"RunPhase");phase.SetValue(paused.Server,Enum.Parse(phase.FieldType,"Standby"));Tick(paused.Owner);Assert(paused.Queue.Count==3&&seen.Count==0,"non-running phase leaves queue untouched");
  seen.Clear();var mixed=Owner(0);mixed.Queue.Enqueue(new FluidBlockPos().Set(10,0,0));mixed.Queue.Enqueue(new BlockPos(11,0,0));var extraType=typeof(ServerSystemBlockSimulation).GetNestedType("BlockPosWithExtraObject",BindingFlags.NonPublic)!;mixed.Queue.Enqueue(Activator.CreateInstance(extraType,new BlockPos(12,0,0),"extra")!);Tick(mixed.Owner);Assert(string.Join('|',seen)=="10:plain|11:plain|12:extra","fluid solid and extra dispatch retained");
  seen.Clear();throws=true;logs=0;var broken=Owner(3);Tick(broken.Owner);throws=false;Assert(seen.Count==3&&logs==2,"native exception catch/log continues once");
  seen.Clear();var producer=Owner(2);hook=id=>{if(id==0)producer.Queue.Enqueue(new BlockPos(2,0,0));};Tick(producer.Owner);hook=null;Assert(string.Join('|',seen)=="0:plain|1:plain|2:plain","callback enqueues preserve order");
  seen.Clear();var nested=Owner(2);var inner=Owner(0);inner.Queue.Enqueue(new BlockPos(99,0,0));hook=id=>{if(id==0)Tick(inner.Owner);};Tick(nested.Owner);hook=null;Assert(string.Join('|',seen)=="0:plain|99:plain|1:plain","nested scopes restore outer processing");
  seen.Clear();var disabledDuring=Owner(3);hook=_=>BlockSimulationBudget.Configure(false);Tick(disabledDuring.Owner);hook=null;Assert(seen.Count==3,"disable during callback resumes stock");
  BlockSimulationBudget.Configure(true);BlockSimulationBudget.SetBudget(1);var threadQueue=new ConcurrentQueue<object>();threadQueue.Enqueue(new object());BlockSimulationBudget.Prefix(out var outerState);Assert(!BlockSimulationBudget.ShouldStop(threadQueue),"outer scope progresses");
  bool threadFirst=false;var thread=new Thread(()=>{BlockSimulationBudget.Prefix(out var threadState);threadFirst=!BlockSimulationBudget.ShouldStop(threadQueue);BlockSimulationBudget.Finalizer(null,threadState);});thread.Start();thread.Join();var elapsed=Stopwatch.StartNew();while(elapsed.ElapsedMilliseconds<2)Thread.SpinWait(64);Assert(threadFirst&&BlockSimulationBudget.ShouldStop(threadQueue),"thread scopes isolated");BlockSimulationBudget.Finalizer(null,outerState);
  var bad=Owner(0);AccessTools.Field(typeof(ServerSystemBlockSimulation),"queuedTicks").SetValue(bad.Owner,null);long beforeFailure=BlockSimulationBudget.Counters.Failures;bool propagated=false;try{Tick(bad.Owner);}catch(NullReferenceException){propagated=true;}Assert(propagated&&BlockSimulationBudget.Counters.Failures==beforeFailure+1,"unexpected failure preserved and policy disabled without replay");
  BlockSimulationBudget.Configure(true);seen.Clear();long failures=BlockSimulationBudget.Counters.Failures;var pressure=Owner(BlockSimulationBudget.HighWater+1);Tick(pressure.Owner);Assert(BlockSimulationBudget.Counters.Failures==failures+1&&seen.Count==1000&&pressure.Queue.Count==BlockSimulationBudget.HighWater+1-1000,"high water fails back to stock without dropping queue");
  Console.WriteLine(JsonSerializer.Serialize(new{checks,stockMs,passes=ticks,maxBudgetedSliceMs=slices.Max(),totalBudgetedMs=slices.Sum(),scope="Actual official native loop; stubbed block callbacks/transport, artificial per-callback CPU work. Scheduling comparison, not less total work or production TPS."}));
  own.UnpatchAll(own.Id);transport.UnpatchAll(transport.Id);Console.WriteLine($"PASS {checks} native queue scheduling checks.");
 }
}
