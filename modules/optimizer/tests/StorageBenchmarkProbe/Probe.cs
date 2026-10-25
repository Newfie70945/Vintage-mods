using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
[assembly:ModInfo("NOS disposable storage benchmark fixture", "nosstoragebenchmarkprobe",Version="0.0.0",Side="Server")]
// TEST ONLY: never ship this DLL in production mods. Explicit full GCs and forced
// unloads are laboratory controls, not optimizer features or recommended tuning.
public sealed class Probe:ModSystem
{
 private readonly List<WeakReference> tracked=[];
 private int startX,startZ;
 private static int Side=>int.TryParse(Environment.GetEnvironmentVariable("NOS_LAB_CHUNK_SIDE"),out int n)?Math.Clamp(n,8,16):8;
 public override bool ShouldLoad(EnumAppSide side)=>side==EnumAppSide.Server;
 public override void StartServerSide(ICoreServerAPI api)
 {
  object Field(object owner,string name){for(var t=owner.GetType();t!=null;t=t.BaseType){var f=t.GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(f!=null)return f.GetValue(owner)!;}throw new MissingFieldException(name);}
  void Emit(string tag,object value)=>api.Logger.Notification("{0}","NOSBENCH_"+tag+" "+JsonSerializer.Serialize(value));
  object State(){using var p=Process.GetCurrentProcess();p.Refresh();var gc=GC.GetGCMemoryInfo();var thread=Field(api.World,"chunkThread");return new {
   allocatedBytes=GC.GetTotalAllocatedBytes(true),heapLiveEstimateBytes=GC.GetTotalMemory(false),gcHeapSizeBytes=gc.HeapSizeBytes,gcCommittedBytes=gc.TotalCommittedBytes,gcFragmentedBytes=gc.FragmentedBytes,
   cpuMs=p.TotalProcessorTime.TotalMilliseconds,workingSetBytes=p.WorkingSet64,privateBytes=p.PrivateMemorySize64,gcPauseMs=GC.GetTotalPauseDuration().TotalMilliseconds,
   gc0=GC.CollectionCount(0),gc1=GC.CollectionCount(1),gc2=GC.CollectionCount(2),
   snapshotCount=((System.Collections.IDictionary)Field(Field(thread,"loadsavegame"),"chunksCopy")).Count,
   loadedChunks=((System.Collections.IDictionary)Field(api.World,"loadedChunks")).Count,
   savePending=(bool)Field(thread,"runOffThreadSaveNow"),trackedChunks=tracked.Count,trackedAlive=tracked.Count(x=>x.IsAlive)};}
  void Add(string name,Func<TextCommandResult> handler)=>api.ChatCommands.Create(name).RequiresPrivilege(Privilege.controlserver).HandleWith(_=>{try{return handler();}catch(Exception e){api.Logger.Error("NOSBENCH_FAIL "+e);return TextCommandResult.Error(e.Message);}});
  void Generate(int offset,int size,string marker){var spawn=api.World.DefaultSpawnPosition;int x=(int)(spawn.X/32)+offset,z=(int)(spawn.Z/32)+offset;if(size==Side){startX=x;startZ=z;}int done=0;long started=Stopwatch.GetTimestamp();
   for(int dx=0;dx<size;dx++)for(int dz=0;dz<size;dz++)api.WorldManager.LoadChunkColumnPriority(x+dx,z+dz,new ChunkLoadOptions{KeepLoaded=true,OnLoaded=()=>{
    if(Interlocked.Increment(ref done)==size*size)Emit(marker,new{columns=size*size,verticalChunks=api.World.BlockAccessor.MapSizeY/32,wallMs=Stopwatch.GetElapsedTime(started).TotalMilliseconds});
   }});
  }
  Add("nosbenchwarm",()=>{Generate(80,2,"WARM");return TextCommandResult.Success("Warmup queued");});
  Add("nosbenchgenerate",()=>{Generate(160,Side,"GENERATED");return TextCommandResult.Success("Generation queued");});
  Add("nosbenchstate",()=>{Emit("STATE",State());return TextCommandResult.Success();});
  Add("nosbenchdirty",()=>{int count=0;for(int dx=0;dx<Side;dx++)for(int dz=0;dz<Side;dz++)for(int y=0;y<api.World.BlockAccessor.MapSizeY/32;y++){
   var chunk=api.WorldManager.GetChunk(startX+dx,y,startZ+dz);if(chunk!=null){chunk.MarkModified();count++;}}
   Emit("DIRTY",new{chunks=count});return TextCommandResult.Success();});
  Add("nosbenchunload",()=>{tracked.Clear();for(int dx=0;dx<Side;dx++)for(int dz=0;dz<Side;dz++){
   for(int y=0;y<api.World.BlockAccessor.MapSizeY/32;y++){var chunk=api.WorldManager.GetChunk(startX+dx,y,startZ+dz);if(chunk!=null)tracked.Add(new WeakReference(chunk));}
   api.WorldManager.UnloadChunkColumn(startX+dx,startZ+dz);}
   Emit("UNLOADED",State());return TextCommandResult.Success();});
  Add("nosbenchgc",()=>{long started=Stopwatch.GetTimestamp();GC.Collect(2,GCCollectionMode.Forced,true,true);GC.WaitForPendingFinalizers();GC.Collect(2,GCCollectionMode.Forced,true,true);Emit("LAB_GC",new{wallMs=Stopwatch.GetElapsedTime(started).TotalMilliseconds,state=State()});return TextCommandResult.Success();});
 }
}
