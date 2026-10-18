using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var path=Path.Combine(folder,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));}return null;};
Checks.Run();
static class Checks
{
 static int checks;
 delegate bool Ensure(ChunkServerThread thread,long key,int x,int z,int pass,long time);
 static void Assert(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
 static void Set(object owner,string field,object? value)=>AccessTools.Field(owner.GetType(),field).SetValue(owner,value);
 static T Get<T>(object owner,string field)=>(T)AccessTools.Field(owner.GetType(),field).GetValue(owner)!;
 static object Empty(Type type)=>RuntimeHelpers.GetUninitializedObject(type);
 static (ChunkServerThread Thread,ServerMain Server,object Queue,ConcurrentDictionary<long,ChunkColumnLoadRequest> Index) Fixture()
 {
  var server=(ServerMain)Empty(typeof(ServerMain));Set(server,"loadedMapChunks",new ConcurrentDictionary<long,ServerMapChunk>());Set(server,"serverConsoleId",-1);
  var thread=(ChunkServerThread)Empty(typeof(ChunkServerThread));Set(thread,"server",server);
  var qt=AccessTools.Field(typeof(ChunkServerThread),"requestedChunkColumns").FieldType;
  var queue=Activator.CreateInstance(qt,[1024,1])!;Set(thread,"requestedChunkColumns",queue);
  return(thread,server,queue,Get<ConcurrentDictionary<long,ChunkColumnLoadRequest>>(queue,"elementsByIndex"));
 }
 static string State(object queue,ConcurrentDictionary<long,ChunkColumnLoadRequest> index)=>JsonSerializer.Serialize(new {
  Count=AccessTools.Property(queue.GetType(),"Count").GetValue(queue),
  Entries=index.OrderBy(x=>x.Key).Select(x=>new{Key=x.Key,x.Value.ChunkX,x.Value.ChunkZ,Until=Get<int>(x.Value,"untilPass"),Time=Get<long>(x.Value,"creationTime")}),
  Order=((System.Collections.IEnumerable)queue).Cast<ChunkColumnLoadRequest>().Select(x=>x.Index).ToArray()});
 static string Scenario(Ensure invoke)
 {
  var f=Fixture();
  Assert(!invoke(f.Thread,42,6,7,2,1000),"new request waits");
  Assert(!invoke(f.Thread,42,6,7,2,2000),"existing request waits");
  Assert(Get<long>(f.Index[42],"creationTime")==1000,"same pass retains creation priority");
  Assert(!invoke(f.Thread,42,6,7,4,3000),"higher pass escalates");
  Assert(Get<long>(f.Index[42],"creationTime")==3000,"escalation updates priority");
  Assert(!invoke(f.Thread,99,-3,9,1,4000),"second request waits");
  var map=(ServerMapChunk)Empty(typeof(ServerMapChunk));Set(map,"currentpass",5);Set(f.Index[42],"MapChunk",map);
  Assert(invoke(f.Thread,42,6,7,3,5000),"completed intermediate pass returns ready");
  Set(map,"currentpass",(int)EnumWorldGenPass.Done);Get<ConcurrentDictionary<long,ServerMapChunk>>(f.Server,"loadedMapChunks")[100]=map;
  Assert(invoke(f.Thread,100,1,0,3,6000)&&!f.Index.ContainsKey(100),"fully generated map avoids queue");
  return State(f.Queue,f.Index);
 }
 static (double Ns,double Bytes) Measure(Action action,int count=200000)
 {
  for(int i=0;i<20000;i++)action();long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
  for(int i=0;i<count;i++)action();return(Stopwatch.GetElapsedTime(start).TotalNanoseconds/count,(GC.GetAllocatedBytesForCurrentThread()-bytes)/(double)count);
 }
 static bool FailSave()=>throw new InvalidOperationException("save-failure-sentinel");
 [MethodImpl(MethodImplOptions.NoInlining)]
 static WeakReference ReleaseFixture()
 {
  var chunk=(ServerChunk)Empty(typeof(ServerChunk));var weak=new WeakReference(chunk);var snapshot=new Dictionary<long,ServerChunk>{{1,chunk}};
  SaveSnapshotOptimizations.Release(snapshot);GC.KeepAlive(snapshot);return weak;
 }
 [MethodImpl(MethodImplOptions.NoInlining)]
 public static void Run()
 {
  Assert(WorldgenRequestOptimizations.ValidateContract(out var why),why);Assert(SaveSnapshotOptimizations.ValidateContract(out why),why);
  var invoke=WorldgenRequestOptimizations.Target.CreateDelegate<Ensure>();string stock=Scenario(invoke);
  var f=Fixture();invoke(f.Thread,1,1,1,2,1000);Action hot=()=>invoke(f.Thread,1,1,1,2,1000);
  var before=Measure(hot);var h=new Harmony("test.nos.worldstorage");
  h.Patch(WorldgenRequestOptimizations.Target,transpiler:new HarmonyMethod(WorldgenRequestOptimizations.TranspilerMethod));WorldgenRequestOptimizations.Configure(true);
  Assert(Scenario(invoke)==stock,"official and optimized request fields/queue order match");var after=Measure(hot);
  Assert(after.Bytes==0&&before.Bytes>0,"existing requests eliminate closure allocations");
  var trials=new List<object>();for(int i=0;i<5;i++){h.Unpatch(WorldgenRequestOptimizations.Target,HarmonyPatchType.All,h.Id);WorldgenRequestOptimizations.Configure(false);var a=Measure(hot);h.Patch(WorldgenRequestOptimizations.Target,transpiler:new HarmonyMethod(WorldgenRequestOptimizations.TranspilerMethod));WorldgenRequestOptimizations.Configure(true);var b=Measure(hot);trials.Add(new{stockNs=a.Ns,optimizedNs=b.Ns,stockBytes=a.Bytes,optimizedBytes=b.Bytes});}
  WorldgenRequestOptimizations.Configure(false);Assert(Scenario(invoke)==stock,"disabled path retains exact stock behavior");
  var system=Empty(SaveSnapshotOptimizations.SystemType);var thread=(ChunkServerThread)Empty(typeof(ChunkServerThread));var snapshot=new Dictionary<long,ServerChunk>{{1,(ServerChunk)Empty(typeof(ServerChunk))}};var saveLock=new object();
  Set(system,"chunkthread",thread);Set(system,"savingLock",saveLock);Set(system,"chunksCopy",snapshot);
  var tick=SaveSnapshotOptimizations.Target.CreateDelegate(typeof(Action<>).MakeGenericType(SaveSnapshotOptimizations.SystemType));
  h.Patch(SaveSnapshotOptimizations.Target,transpiler:new HarmonyMethod(SaveSnapshotOptimizations.TranspilerMethod));SaveSnapshotOptimizations.Configure(true);
  tick.DynamicInvoke(system);Assert(snapshot.Count==1,"no pending save leaves snapshot untouched");
  h.Patch(AccessTools.Method(SaveSnapshotOptimizations.SystemType,"SaveAllDirtyLoadedChunks"),prefix:new HarmonyMethod(AccessTools.Method(typeof(Checks),nameof(FailSave))));
  Set(thread,"runOffThreadSaveNow",true);
  for(int retry=0;retry<2;retry++){
   try{tick.DynamicInvoke(system);throw new Exception("Expected failure");}catch(TargetInvocationException e){Assert(e.InnerException is InvalidOperationException {Message:"save-failure-sentinel"},"save exception propagated");}
   Assert(snapshot.Count==1&&Get<bool>(thread,"runOffThreadSaveNow"),"failed save/retry retains snapshot and pending flag");
   Assert(Monitor.TryEnter(saveLock),"stock lock released after exception");Monitor.Exit(saveLock);
  }
  SaveSnapshotOptimizations.Configure(false);SaveSnapshotOptimizations.Release(snapshot);Assert(snapshot.Count==1,"disabled release preserves references");
  SaveSnapshotOptimizations.Configure(true);var prior=SaveSnapshotOptimizations.Counters;SaveSnapshotOptimizations.Release(snapshot);Assert(snapshot.Count==0&&SaveSnapshotOptimizations.Counters.ReferencesReleased==prior.ReferencesReleased+1,"successful release clears references and counts");
  var weak=ReleaseFixture();for(int i=0;i<3;i++){GC.Collect();GC.WaitForPendingFinalizers();}Assert(!weak.IsAlive,"released snapshot no longer pins unloaded chunk");
  var il=PatchProcessor.GetOriginalInstructions(SaveSnapshotOptimizations.Target).ToList();var patched=SaveSnapshotOptimizations.Transpiler(il).ToList();int release=patched.FindIndex(x=>x.operand is MethodInfo m&&m.Name==nameof(SaveSnapshotOptimizations.Release));
  Assert(release>0&&patched.Take(release).Any(x=>x.operand is MethodInfo m&&m.Name=="StoreSaveGame")&&patched.Skip(release+1).Any(x=>x.operand is MethodInfo m&&m.Name=="Exit"&&m.DeclaringType==typeof(Monitor)),"release follows metadata save and precedes stock lock exit");
  try{SaveSnapshotOptimizations.Transpiler([new CodeInstruction(OpCodes.Ret)]).ToList();throw new Exception("Expected mismatch rejection");}catch(InvalidOperationException){Assert(true,"unexpected save IL rejected");}
  h.UnpatchAll(h.Id);WorldgenRequestOptimizations.Configure(false);SaveSnapshotOptimizations.Configure(false);
  var result=new{checks,iterationsPerTrial=200000,trials,scope="Existing-request lookup, actual official unpatched target versus enabled Harmony patch via delegate; not worldgen throughput or production TPS."};
  var output=Environment.GetEnvironmentVariable("NOS_MEASUREMENTS");if(output!=null)File.WriteAllText(output,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true})+"\n");
  Console.WriteLine(JsonSerializer.Serialize(result));
 }
}
