using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using Vintagestory.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var d in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(d,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
Checks.Run();
public class Manager:EventManager { public override long InWorldEllapsedMs=>0; public override string CommandPrefix=>"/"; public override ILogger Logger=>null!; public override bool HasPrivilege(string uid,string privilege)=>true;
 public List<GameTickListener> GameTickListenersEntity=>(List<GameTickListener>)AccessTools.Field(typeof(EventManager),"GameTickListenersEntity").GetValue(this)!;
 public List<GameTickListenerBlock> GameTickListenersBlock=>(List<GameTickListenerBlock>)AccessTools.Field(typeof(EventManager),"GameTickListenersBlock").GetValue(this)!;
 public System.Collections.Concurrent.ConcurrentDictionary<long,int> GameTickListenersEntityIndices=>(System.Collections.Concurrent.ConcurrentDictionary<long,int>)AccessTools.Field(typeof(EventManager),"GameTickListenersEntityIndices").GetValue(this)!;
 public System.Collections.Concurrent.ConcurrentDictionary<long,int> GameTickListenersBlockIndices=>(System.Collections.Concurrent.ConcurrentDictionary<long,int>)AccessTools.Field(typeof(EventManager),"GameTickListenersBlockIndices").GetValue(this)!;
 }
static class Checks
{
 static int checks;static void Assert(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 static readonly Action<float> handler=_=>{};
 static long Add(Manager manager,int kind)=>kind==0?manager.AddGameTickListener(handler,null!,100,0):manager.AddGameTickListener(handler,new BlockPos(1,2,3),null!,100,0);
 static string Dump(Manager manager)=>string.Join('|',manager.GameTickListenersEntity.Select(x=>x?.ListenerId??0))+"/"+string.Join('|',manager.GameTickListenersBlock.Select(x=>x?.ListenerId??0));
 static Manager Setup(int kind){var m=new Manager();for(int i=0;i<100;i++)Add(m,kind);for(int i=0;i<100;i+=3)m.RemoveGameTickListener(i+1);return m;}
 [MethodImpl(MethodImplOptions.NoInlining)]static WeakReference Weak(){var m=new Manager();for(int i=0;i<20;i++)Add(m,0);return new WeakReference(m.GameTickListenersEntity);}
 public static void Run()
 {
  RuntimeEnv.ServerMainThreadId=Environment.CurrentManagedThreadId;
  var harmony=new Harmony("test.listener.registration");
  for(int kind=0;kind<2;kind++)
  {
   Assert(ListenerRegistrationOptimizations.ValidateContract(kind,out var why),why);
   harmony.Patch(ListenerRegistrationOptimizations.Target(kind),prefix:new HarmonyMethod(typeof(ListenerRegistrationOptimizations),kind==0?nameof(ListenerRegistrationOptimizations.EntityPrefix):nameof(ListenerRegistrationOptimizations.BlockPrefix)),transpiler:new HarmonyMethod(typeof(ListenerRegistrationOptimizations),nameof(ListenerRegistrationOptimizations.Transpiler)),finalizer:new HarmonyMethod(typeof(ListenerRegistrationOptimizations),nameof(ListenerRegistrationOptimizations.Finalizer)));
  }
  for(int kind=0;kind<2;kind++)
  {
   ListenerRegistrationOptimizations.Configure(kind,false);var stock=Setup(kind);for(int i=0;i<120;i++)Add(stock,kind);string expected=Dump(stock);
   ListenerRegistrationOptimizations.Configure(kind,true);var patched=Setup(kind);for(int i=0;i<120;i++)Add(patched,kind);Assert(Dump(patched)==expected,"native IDs, first-empty ordering and append preserved");
   for(int i=20;i<80;i++)patched.RemoveGameTickListener(i);for(int i=20;i<80;i++)stock.RemoveGameTickListener(i);
   for(int i=0;i<100;i++){ListenerRegistrationOptimizations.Configure(kind,false);Add(stock,kind);ListenerRegistrationOptimizations.Configure(kind,true);Add(patched,kind);}
   Assert(Dump(patched)==Dump(stock),"removal/re-registration preserves lists and IDs");
   if(kind==0){patched.GameTickListenersEntity[0]=null!;stock.GameTickListenersEntity[0]=null!;}
   else{patched.GameTickListenersBlock[0]=null!;stock.GameTickListenersBlock[0]=null!;}
   ListenerRegistrationOptimizations.Configure(kind,false);Add(stock,kind);ListenerRegistrationOptimizations.Configure(kind,true);Add(patched,kind);Assert(Dump(stock)==Dump(patched),"external list mutation invalidates free-slot cache");
   var index=kind==0?patched.GameTickListenersEntityIndices:patched.GameTickListenersBlockIndices;
   var expectedIndex=kind==0?stock.GameTickListenersEntityIndices:stock.GameTickListenersBlockIndices;
   Assert(index.OrderBy(x=>x.Key).SequenceEqual(expectedIndex.OrderBy(x=>x.Key)),"authoritative ID dictionaries match stock including external mutation behavior");
  }
  ListenerRegistrationOptimizations.Configure(0,true);var offThread=new Manager();Task.Run(()=>{for(int i=0;i<200;i++)Add(offThread,0);}).GetAwaiter().GetResult();Assert(offThread.GameTickListenersEntity.Count==200,"off-thread calls retain stock search");
  var weak=Weak();for(int i=0;i<3&&weak.IsAlive;i++){GC.Collect();GC.WaitForPendingFinalizers();}Assert(!weak.IsAlive,"index does not retain old listener lists");
  var rows=new List<object>();
  for(int trial=0;trial<3;trial++)foreach(int kind in new[]{0,1})foreach(bool enabled in trial%2==0?new[]{false,true}:new[]{true,false})
  {
   ListenerRegistrationOptimizations.Configure(kind,enabled);var m=new Manager();long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();for(int i=0;i<6000;i++)Add(m,kind);rows.Add(new{trial,kind,enabled,listeners=6000,ms=Stopwatch.GetElapsedTime(start).TotalMilliseconds,allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-bytes});
   Assert((kind==0?m.GameTickListenersEntity.Count:m.GameTickListenersBlock.Count)==6000,"all 6000 callbacks registered");
  }
  harmony.UnpatchAll(harmony.Id);ListenerRegistrationOptimizations.Configure(0,false);ListenerRegistrationOptimizations.Configure(1,false);
  string json=JsonSerializer.Serialize(new{checks,rows,counters=ListenerRegistrationOptimizations.Counters.ToString(),scope="Actual official native listener registration and removal, controlled timestamps; not whole-server tick latency."});Console.WriteLine(json);if(Environment.GetEnvironmentVariable("NOS_MEASUREMENTS") is {} file)File.WriteAllText(file,json+"\n");Console.WriteLine($"PASS {checks} listener registration checks");
 }
}
