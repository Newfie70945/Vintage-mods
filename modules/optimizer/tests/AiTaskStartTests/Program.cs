using System.Reflection.Emit;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var path=Path.Combine(folder,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);}return null;};
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);
 public static T Make<T>(System.Func<MethodInfo,object?[]?,object?> fn)where T:class{var x=Create<T,Proxy>();((Proxy)(object)x).Handler=fn;return x;}
}
public class TestEntity:Entity {}
static class Checks
{
 static int checks;
 static void Assert(bool x,string s){if(!x)throw new Exception(s);checks++;}
 delegate void Execute(AiTaskManager manager,IAiTask task,int slot);
 static (double Ns,double Bytes) Measure(Action action){for(int i=0;i<30000;i++)action();long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();for(int i=0;i<300000;i++)action();return(Stopwatch.GetElapsedTime(start).TotalNanoseconds/300000,(GC.GetAllocatedBytesForCurrentThread()-bytes)/300000d);}
 static object Scenario(Execute execute)
 {
  var events=new List<string>();var profiler=(FrameProfilerUtil)RuntimeHelpers.GetUninitializedObject(typeof(FrameProfilerUtil));
  var world=Proxy.Make<IWorldAccessor>((m,a)=>m.Name=="get_FrameProfiler"?profiler:null);
  var entity=(TestEntity)RuntimeHelpers.GetUninitializedObject(typeof(TestEntity));entity.World=world;
  IAiTask Task(string name)=>Proxy.Make<IAiTask>((m,a)=>{if(m.Name is "StartExecute" or "FinishExecute")events.Add(name+":"+m.Name);return m.ReturnType==typeof(void)?null:m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):name;});
  var next=Task("next");var old=Task("old");var manager=new AiTaskManager(entity);manager.ActiveTasksBySlot[2]=old;
  manager.OnTaskStopped+=_=>events.Add("stop");manager.OnTaskStarted+=_=>events.Add("start");
  AiTaskManager.OnExecuteTaskDelegate veto=(a,b)=>{Assert(ReferenceEquals(a,next)&&ReferenceEquals(b,old),"handler arguments");events.Add("veto");return false;};
  AiTaskManager.OnExecuteTaskDelegate second=(a,b)=>{events.Add("second");return true;};
  manager.OnExecuteTask+=veto;manager.OnExecuteTask+=second;execute(manager,next,2);
  Assert(events.SequenceEqual(new[]{"veto","second"})&&ReferenceEquals(manager.ActiveTasksBySlot[2],old),"all vetoes run, state unchanged");
  manager.OnExecuteTask-=veto;events.Clear();execute(manager,next,2);
  Assert(events.SequenceEqual(new[]{"second","old:FinishExecute","stop","next:StartExecute","start"})&&ReferenceEquals(manager.ActiveTasksBySlot[2],next),"stock transitions preserved");
  var order=new List<int>();var mutating=new AiTaskManager(entity);AiTaskManager.OnExecuteTaskDelegate later=(a,b)=>{order.Add(2);return false;};
  mutating.OnExecuteTask+=(a,b)=>{order.Add(1);mutating.OnExecuteTask-=later;return false;};mutating.OnExecuteTask+=later;
  execute(mutating,next,0);Assert(order.SequenceEqual(new[]{1,2}),"current subscription snapshot preserved");order.Clear();execute(mutating,next,0);Assert(order.SequenceEqual(new[]{1}),"next call sees mutation");
  var throwing=new AiTaskManager(entity);throwing.OnExecuteTask+=(a,b)=>throw new InvalidOperationException("sentinel");
  try{execute(throwing,next,0);throw new Exception("Missing throw");}catch(InvalidOperationException e){Assert(e.Message=="sentinel"&&throwing.ActiveTasksBySlot[0]==null,"exception propagated once before state mutation");}
  execute(new AiTaskManager(entity),next,0);Assert(events.Last()=="next:StartExecute","no subscribers retains stock path");
  try{execute(manager,next,999);throw new Exception("Missing slot error");}catch(IndexOutOfRangeException){Assert(true,"invalid slot preserves stock exception");}
  return events.ToArray();
 }
 [MethodImpl(MethodImplOptions.NoInlining)]
 public static void Run()
 {
  Assert(AiTaskStartOptimizations.ValidateContract(out var why),why);
  var execute=AiTaskStartOptimizations.Target.CreateDelegate<Execute>();
  string stock=JsonSerializer.Serialize(Scenario(execute));
  var manager=new AiTaskManager(null!);int calls=0;manager.OnExecuteTask+=(a,b)=>{calls++;return false;};manager.OnExecuteTask+=(a,b)=>{calls++;return true;};Action hot=()=>execute(manager,null!,0);
  var single=new AiTaskManager(null!);single.OnExecuteTask+=(a,b)=>false;
  var harmony=new Harmony("test.nos.ai-task-start");var trials=new List<object>();
  for(int i=0;i<5;i++){
   var singleBefore=Measure(()=>execute(single,null!,0));var before=Measure(hot);harmony.Patch(AiTaskStartOptimizations.Target,transpiler:new HarmonyMethod(AiTaskStartOptimizations.TranspilerMethod));AiTaskStartOptimizations.Configure(true);
   Assert(JsonSerializer.Serialize(Scenario(execute))==stock,"official and patched transition output identical");
   var singleAfter=Measure(()=>execute(single,null!,0));var after=Measure(hot);Assert(after.Bytes==0&&before.Bytes>0,"zero allocation task-start callback enumeration");trials.Add(new{singleStockNs=singleBefore.Ns,singleOptimizedNs=singleAfter.Ns,stockNs=before.Ns,optimizedNs=after.Ns,stockBytes=before.Bytes,optimizedBytes=after.Bytes});
   AiTaskStartOptimizations.Configure(false);Assert(JsonSerializer.Serialize(Scenario(execute))==stock,"disabled helper matches stock");harmony.UnpatchAll(harmony.Id);
  }
  try{AiTaskStartOptimizations.Transpiler([new(OpCodes.Ret)]).ToList();throw new Exception("Missing IL rejection");}catch(InvalidOperationException){Assert(true,"changed body rejected");}
  var result=new{checks,trials,scope="Actual official ExecuteTask target through delegate, synthetic tasks and veto subscribers; not production TPS."};
  var output=Environment.GetEnvironmentVariable("NOS_MEASUREMENTS");if(output!=null)File.WriteAllText(output,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true})+"\n");
  Console.WriteLine(JsonSerializer.Serialize(result));
 }
}
