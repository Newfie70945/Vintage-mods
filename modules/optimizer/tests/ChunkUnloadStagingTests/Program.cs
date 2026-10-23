using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using Vintagestory.API.Common;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using Vintagestory.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var d in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(d,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
Checks.Run();
static class Checks
{
 static int checks;static void Assert(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 static T New<T>()where T:class=>(T)RuntimeHelpers.GetUninitializedObject(typeof(T));
 static void Set(object value,string name,object? data)=>AccessTools.Field(value.GetType(),name).SetValue(value,data);
 static T Get<T>(object value,string name)=>(T)AccessTools.Field(value.GetType(),name).GetValue(value)!;
 static readonly List<string> callbacks=new();static bool failCallback;
 static bool Neighbours()=>false;
 static bool Unloaded(Vec3i __0){callbacks.Add(__0.ToString());if(failCallback)throw new InvalidOperationException("unload-sentinel");return false;}
 static object Owner(bool populated)
 {
  var s=New<ServerMain>();s.WorldMap=New<ServerWorldMap>();Set(s.WorldMap,"mapsize",new Vec3i(32768,0,32768));Set(s.WorldMap,"index3dMulX",1024);Set(s.WorldMap,"index3dMulZ",1024);
  Set(s,"forceLoadedChunkColumns",new HashSet<long>{7});Set(s,"loadedMapChunks",new ConcurrentDictionary<long,ServerMapChunk>());
  var api=New<ServerCoreAPI>();Set(s,"api",api);Set(api,"eventapi",RuntimeHelpers.GetUninitializedObject(typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerEventAPI",true)!));
  var owner=RuntimeHelpers.GetUninitializedObject(ChunkUnloadStagingOptimizations.Target.DeclaringType!);Set(owner,"server",s);Set(owner,"mapChunkUnloadCandidates",new HashSet<long>(populated?[7,8]:[]));
  Set(owner,"dirtyChunksLock",new object());Set(owner,"dirtyUnloadedChunks",new List<ServerChunkWithCoord>());Set(owner,"dirtyUnloadedMapChunks",new List<ServerMapChunkWithCoord>());
  if(populated){var map=New<ServerMapChunk>();map.DirtyForSaving=true;Get<ConcurrentDictionary<long,ServerMapChunk>>(s,"loadedMapChunks")[8]=map;}
  return owner;
 }
 static Action Invoke(object owner)=>ChunkUnloadStagingOptimizations.Target.CreateDelegate<Action>(owner);
 public static void Run()
 {
  Assert(ChunkUnloadStagingOptimizations.ValidateContract(out var why),why);
  AccessTools.Field(typeof(ServerMain),"FrameProfiler").SetValue(null,new FrameProfilerUtil((Action<string>)(_=>{})));
  var own=new Harmony("test.nos.unload-staging");var transport=new Harmony("test.nos.unload-staging.callbacks");
  var supply=BlockChunkScratchOptimizations.SupplyOwner;
  transport.Patch(AccessTools.Method(supply,"UpdateLoadedNeighboursFlags"),prefix:new HarmonyMethod(typeof(Checks),nameof(Neighbours)));
  transport.Patch(AccessTools.Method(typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerEventAPI",true)!,"TriggerChunkColumnUnloaded"),prefix:new HarmonyMethod(typeof(Checks),nameof(Unloaded)));
  var native=Owner(true);callbacks.Clear();Invoke(native)();string expected=string.Join('|',callbacks);var queuedNative=Get<List<ServerMapChunkWithCoord>>(native,"dirtyUnloadedMapChunks");Assert(queuedNative.Count==1,"native dirty map staging fixture");
  own.Patch(ChunkUnloadStagingOptimizations.Target,prefix:new HarmonyMethod(typeof(ChunkUnloadStagingOptimizations),nameof(ChunkUnloadStagingOptimizations.Prefix)),transpiler:new HarmonyMethod(typeof(ChunkUnloadStagingOptimizations),nameof(ChunkUnloadStagingOptimizations.Transpiler)),finalizer:new HarmonyMethod(typeof(ChunkUnloadStagingOptimizations),nameof(ChunkUnloadStagingOptimizations.Finalizer)));
  ChunkUnloadStagingOptimizations.Configure(true);var optimized=Owner(true);callbacks.Clear();Invoke(optimized)();
  var queued=Get<List<ServerMapChunkWithCoord>>(optimized,"dirtyUnloadedMapChunks");Assert(queued.Count==1&&string.Join('|',callbacks)==expected,"native forced-load filtering, callback order and dirty queue preserved");
  Assert(queued[0].mapchunk!=null&&queued[0].index2d==8&&!queued[0].mapchunk.DirtyForSaving,"queued record and dirty state preserved after scope cleanup");
  var retained=queued[0];Invoke(Owner(false))();Assert(ReferenceEquals(queued[0].mapchunk,retained.mapchunk)&&retained.mapchunk!=null,"later reuse cannot mutate independently owned dirty records");
  ChunkUnloadStagingOptimizations.Prefix(out var outer);var list=ChunkUnloadStagingOptimizations.Chunks();list.Add(new ServerChunkWithCoord());
  ChunkUnloadStagingOptimizations.Prefix(out var inner);Assert(!ReferenceEquals(list,ChunkUnloadStagingOptimizations.Chunks()),"nested lists isolated");ChunkUnloadStagingOptimizations.Finalizer(null,inner);
  Assert(ReferenceEquals(list,ChunkUnloadStagingOptimizations.Chunks()),"outer scope restored");ChunkUnloadStagingOptimizations.Configure(false);ChunkUnloadStagingOptimizations.Prefix(out inner);Assert(!ReferenceEquals(list,ChunkUnloadStagingOptimizations.Chunks()),"disable during nesting cannot borrow outer list");ChunkUnloadStagingOptimizations.Finalizer(null,inner);ChunkUnloadStagingOptimizations.Finalizer(null,outer);Assert(!outer!.RetainsRecords&&list.Count==0,"all staging references released");
  ChunkUnloadStagingOptimizations.Configure(true);ChunkUnloadStagingOptimizations.Prefix(out var huge);ChunkUnloadStagingOptimizations.Chunks().Capacity=8192;long releases=ChunkUnloadStagingOptimizations.Counters.Released;ChunkUnloadStagingOptimizations.Finalizer(null,huge);Assert(ChunkUnloadStagingOptimizations.Counters.Released==releases+1&&!huge!.RetainsRecords,"oversized buffers dropped and cleared");
  failCallback=true;int before=callbacks.Count;try{Invoke(Owner(true))();throw new Exception("swallowed");}catch(InvalidOperationException e){Assert(e.Message=="unload-sentinel"&&callbacks.Count==before+1,"callback failure not swallowed or replayed");}failCallback=false;
  long passes=ChunkUnloadStagingOptimizations.Counters.Calls;Invoke(Owner(false))();Assert(ChunkUnloadStagingOptimizations.Counters.Calls==passes,"failed gate uses stock staging until reapply");
  ChunkUnloadStagingOptimizations.Configure(true);ChunkUnloadStagingOptimizations.Prefix(out var a);var buffer=ChunkUnloadStagingOptimizations.Chunks();bool separate=false;
  Task.Run(()=>{ChunkUnloadStagingOptimizations.Prefix(out var b);separate=!ReferenceEquals(buffer,ChunkUnloadStagingOptimizations.Chunks());ChunkUnloadStagingOptimizations.Finalizer(null,b);}).GetAwaiter().GetResult();ChunkUnloadStagingOptimizations.Finalizer(null,a);Assert(separate,"thread-local list ownership");
  var measurements=new List<object>();
  for(int trial=0;trial<3;trial++)foreach(bool on in new[]{false,true}){ChunkUnloadStagingOptimizations.Configure(on);var action=Invoke(Owner(false));for(int i=0;i<10000;i++)action();long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();for(int i=0;i<50000;i++)action();measurements.Add(new{trial,enabled=on,bytesPerPass=(GC.GetAllocatedBytesForCurrentThread()-bytes)/50000d,nsPerPass=Stopwatch.GetElapsedTime(start).TotalNanoseconds/50000});}
  var stagingMeasurements=new List<object>();
  for(int trial=0;trial<3;trial++)foreach(bool on in new[]{false,true})
  {
   ChunkUnloadStagingOptimizations.Configure(on);
   void Work(){ChunkUnloadStagingOptimizations.Prefix(out var frame);var chunks=ChunkUnloadStagingOptimizations.Chunks();var maps=ChunkUnloadStagingOptimizations.MapChunks();for(int i=0;i<256;i++){chunks.Add(default);maps.Add(default);}ChunkUnloadStagingOptimizations.Finalizer(null,frame);}
   for(int i=0;i<1000;i++)Work();long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();for(int i=0;i<10000;i++)Work();stagingMeasurements.Add(new{trial,enabled=on,recordsPerList=256,bytesPerPass=(GC.GetAllocatedBytesForCurrentThread()-bytes)/10000d,nsPerPass=Stopwatch.GetElapsedTime(start).TotalNanoseconds/10000});
  }
  ChunkUnloadStagingOptimizations.Configure(false);own.UnpatchAll(own.Id);transport.UnpatchAll(transport.Id);
  var result=new{checks,measurements,stagingMeasurements,tieredCompilation=Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")??"default",stagingScope="Private constructor-helper components with 256 records per list, not native disk or chunk disposal work.",scope="Actual official unload-staging body with isolated map chunks and intercepted neighbor/event callbacks; no disk-I/O or player performance claim."};string json=JsonSerializer.Serialize(result);Console.WriteLine(json);var file=Environment.GetEnvironmentVariable("NOS_MEASUREMENTS");if(file!=null)File.WriteAllText(file,json+"\n");Console.WriteLine($"PASS {checks} native unload staging, queue ownership, nesting, failure and thread checks.");
 }
}
