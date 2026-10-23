using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{
    foreach(var dir in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}) {
        var path=Path.Combine(dir,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
Checks.Run();
public class Proxy:DispatchProxy
{
    public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>Handler(method!,args);
    public static T Make<T>(System.Func<MethodInfo,object?[]?,object?> handler)where T:class {var value=Create<T,Proxy>();((Proxy)(object)value).Handler=handler;return value;}
}
public class Manager:EventManager
{
    public override ILogger Logger=>null!;
    public override string CommandPrefix=>"fixture";
    public override long InWorldEllapsedMs=>0;
    public override bool HasPrivilege(string uid,string privilege)=>true;
}
static class Checks
{
    private static int checks,mapCalls,chunkCalls;
    private static bool record=true,failSpawn;
    private static readonly List<string> events=new();
    private static IWorldChunk[] chunks=[];
    private static ServerMapChunk? cachedMap;
    private static int missing=-1;
    private static T New<T>()where T:class=>(T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Set(object value,string field,object? data)=>AccessTools.Field(value.GetType(),field).SetValue(value,data);
    private static T Get<T>(object value,string field)=>(T)AccessTools.Field(value.GetType(),field).GetValue(value)!;
    private static void Check(bool value,string reason){if(!value)throw new Exception(reason);checks++;}
    private static IWorldAccessor World(){var profiler=New<FrameProfilerUtil>();return Proxy.Make<IWorldAccessor>((m,a)=>m.Name=="get_FrameProfiler"?profiler:null);}
    private static Dictionary<BlockPos,DelayedCallbackBlock> Pending(Manager manager)=>Get<Dictionary<BlockPos,DelayedCallbackBlock>>(manager,"SingleDelayedCallbacksBlock");
    private static Action<long,IWorldAccessor> Tick(Manager manager,int kind)=>CallbackSpawnOptimizations.Target(kind).CreateDelegate<Action<long,IWorldAccessor>>(manager);
    private static void Switch(Harmony h,int kind,bool value)
    {
        h.UnpatchAll(h.Id);CallbackSpawnOptimizations.Configure(kind,value);if(!value)return;
        string[] prefixes=[nameof(CallbackSpawnOptimizations.TickPrefix),nameof(CallbackSpawnOptimizations.DebugPrefix),nameof(CallbackSpawnOptimizations.SpawnPrefix),nameof(CallbackSpawnOptimizations.ColumnPrefix)];
        h.Patch(CallbackSpawnOptimizations.Target(kind),prefix:new HarmonyMethod(typeof(CallbackSpawnOptimizations),prefixes[kind]),transpiler:new HarmonyMethod(typeof(CallbackSpawnOptimizations),nameof(CallbackSpawnOptimizations.Transpiler)),finalizer:new HarmonyMethod(typeof(CallbackSpawnOptimizations),nameof(CallbackSpawnOptimizations.Finalizer)));
    }
    private static bool CanSpawn(EntityProperties type,BlockPos spawnPosition,ref Vec3d __result)
    {
        if(failSpawn)throw new InvalidOperationException("spawn-sentinel");
        __result=new Vec3d(spawnPosition.X,spawnPosition.Y,spawnPosition.Z);return false;
    }
    private static bool MapChunk(ref IMapChunk __result){mapCalls++;__result=cachedMap??=new ServerMapChunk{WorldGenTerrainHeightMap=Enumerable.Repeat((ushort)128,1024).ToArray()};return false;}
    private static bool Chunk(int chunkY,ref IWorldChunk __result){chunkCalls++;__result=chunkY==missing?null!:chunks[chunkY];return false;}
    private static ServerSystemEntitySpawner Spawner(int height=256)
    {
        var s=New<ServerMain>();s.WorldMap=New<ServerWorldMap>();Set(s.WorldMap,"mapsize",new Vec3i(32768,height,32768));Set(s.WorldMap,"chunkMapSizeY",height/32);var manager=New<CoreServerEventManager>();Set(manager,"modEventManager",New<ServerEventManager>());Set(s,"EventManager",manager);
        var o=New<ServerSystemEntitySpawner>();Set(o,"server",s);Set(o,"spawnPosition",new BlockPos(0));Set(o,"rand",new Random(1234));Set(o,"shuffledY",new[]{-3,-2,-1,0,1,2,3});Set(o,"heightMap",Enumerable.Repeat((ushort)128,1024).ToArray());Set(o,"SeaLevel",128);Set(o,"SkyHeight",128);Set(o,"surfaceMapTryID",1);Set(o,"readyToSpawn",new ConcurrentQueue<EntitySpawnerResult>());
        return o;
    }
    private static List<SpawnState> States()
    {
        var props=new EntityProperties{Code=new AssetLocation("game:fixture"),Server=New<EntityServerProperties>()};props.Server.SpawnConditions=new SpawnConditions{Runtime=new RuntimeSpawnConditions{TryOnlySurface=true,MinY=0,MaxY=3}};
        var state=new SpawnState();Set(state,"ForType",props);Set(state,"SpawnableAmountGlobal",1);Set(state,"SpawnCapScaledPerPlayer",100);Set(state,"NextGroupSize",1);Set(state,"SelfAndCompanionProps",new[]{props});
        return new(){state};
    }
    private delegate void SpawnCall(ServerSystemEntitySpawner owner,int x,int y,int z,Dictionary<AssetLocation,int> counts,IWorldChunk[] column,List<SpawnState> states);
    private delegate void ColumnCall(ServerSystemEntitySpawner owner,int x,int y,int z,Dictionary<AssetLocation,int> counts,List<SpawnState> states);
    private static readonly SpawnCall spawn=CallbackSpawnOptimizations.Target(2).CreateDelegate<SpawnCall>();
    private static readonly ColumnCall column=CallbackSpawnOptimizations.Target(3).CreateDelegate<ColumnCall>();
    private static string SpawnState(ServerSystemEntitySpawner owner,List<SpawnState> states)
    {
        var q=Get<ConcurrentQueue<EntitySpawnerResult>>(owner,"readyToSpawn");
        return string.Join('|',q.Select(result=>string.Join(';',Get<List<SpawnOppurtunity>>(result,"spawnPositions").Select(v=>$"{v.ForType.Code}:{v.Pos.X},{v.Pos.Y},{v.Pos.Z}"))))+":"+Get<Random>(owner,"rand").NextDouble()+":"+Get<int>(states[0],"SpawnableAmountGlobal")+":"+Get<int>(states[0],"NextGroupSize");
    }
    private static object MeasureCallback(Harmony h,bool on,int n)
    {
        Switch(h,0,on);var m=new Manager();var pending=Pending(m);for(int i=0;i<n;i++){var pos=new BlockPos(i,1,1);pending[pos]=new(i,long.MaxValue,pos,(_,_,_)=>{});}var tick=Tick(m,0);var world=World();
        return Measure(()=>tick(100,world),new{kind="callback",enabled=on,positions=n});
    }
    private static object MeasureSpawn(Harmony h,bool on)
    {
        Switch(h,2,on);var o=Spawner();var counts=new Dictionary<AssetLocation,int>();var states=new List<SpawnState>();var empty=Array.Empty<IWorldChunk>();
        return Measure(()=>spawn(o,2,4,2,counts,empty,states),new{kind="spawn-search",enabled=on,states=0});
    }
    private static object MeasureColumn(Harmony h,bool on)
    {
        Switch(h,3,on);var o=Spawner();var counts=new Dictionary<AssetLocation,int>();var states=new List<SpawnState>();
        chunks=Enumerable.Range(0,8).Select(_=>Proxy.Make<IWorldChunk>((m,a)=>m.ReturnType!=typeof(void)&&m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null)).ToArray();
        missing=-1;record=false;var result=Measure(()=>column(o,2,4,2,counts,states),new{kind="spawn-column",enabled=on,chunks=8});record=true;return result;
    }
    private static object Measure(Action call,object context)
    {
        for(int i=0;i<2000;i++)call();var sw=new Stopwatch();long before=GC.GetAllocatedBytesForCurrentThread();sw.Start();for(int i=0;i<10000;i++)call();sw.Stop();
        return new{context,bytesPerCall=(GC.GetAllocatedBytesForCurrentThread()-before)/10000d,nsPerCall=sw.Elapsed.TotalNanoseconds/10000};
    }
    public static void Run()
    {
        var own=new Harmony("test.nos.callback-spawn");var fixture=new Harmony("test.nos.callback-spawn.fixture");
        fixture.Patch(AccessTools.Method(typeof(ServerSystemEntitySpawner),"CanSpawnAt_offthread"),prefix:new HarmonyMethod(typeof(Checks),nameof(CanSpawn)));
        fixture.Patch(AccessTools.Method(typeof(ServerWorldMap),"GetMapChunk",[typeof(int),typeof(int)]),prefix:new HarmonyMethod(typeof(Checks),nameof(MapChunk)));
        fixture.Patch(AccessTools.Method(typeof(ServerWorldMap),"GetChunk",[typeof(int),typeof(int),typeof(int)]),prefix:new HarmonyMethod(typeof(Checks),nameof(Chunk)));
        try
        {
            for(int k=0;k<4;k++)Check(CallbackSpawnOptimizations.ValidateContract(k,out _),"official contract "+k);
            for(int kind=0;kind<2;kind++)
            {
                string Scenario(bool enabled)
                {
                    Switch(own,kind,enabled);events.Clear();var m=new Manager();var pending=Pending(m);var world=World();var p1=new BlockPos(1,2,3,4);var p2=new BlockPos(2,3,4);var p3=new BlockPos(3,3,4);
                    pending[p1]=new(1,0,p1,(w,p,dt)=>{Check(ReferenceEquals(p,p1)&&p.dimension==4&&dt==.1f,"original callback position/dimension/elapsed time");events.Add("one");pending[p3]=new(3,0,p3,(_,_,_)=>events.Add("new"));});
                    pending[p2]=new(2,1000,p2,(_,_,_)=>events.Add("future"));Tick(m,kind)(100,world);Check(events.SequenceEqual(new[]{"one"}),"newly scheduled single callback waits for next snapshot");Tick(m,kind)(200,world);Check(events.SequenceEqual(new[]{"one","new"}),"next tick runs new callback once");return string.Join('|',events)+":"+pending.Count;
                }
                string stock=Scenario(false);Check(Scenario(true)==stock,"normal/debug callback snapshot semantics equal stock "+kind);
                Switch(own,kind,true);var manager=new Manager();var p=new BlockPos(0);Pending(manager)[p]=new(1,0,p,(_,_,_)=>throw new InvalidOperationException("callback-sentinel"));try{Tick(manager,kind)(100,World());throw new Exception("missing failure");}catch(InvalidOperationException e){Check(e.Message=="callback-sentinel"&&Pending(manager).Count==0,"callback error retained without replay "+kind);}
                Check(CallbackSpawnOptimizations.Counters(kind).Failures>0,"callback failure counter "+kind);
                foreach(bool mode in new[]{false,true}) {
                    Switch(own,kind,mode);var mutate=new Manager();var a=new BlockPos(1,2,3);var b=new BlockPos(2,2,3);var entries=Pending(mutate);
                    entries[a]=new(1,0,a,(_,_,_)=>entries.Remove(b));entries[b]=new(2,0,b,(_,_,_)=>{});
                    bool nativeError=false;try{Tick(mutate,kind)(100,World());}catch(KeyNotFoundException){nativeError=true;}Check(nativeError,"removing a later snapshotted key preserves stock exception "+kind+"/"+mode);
                }

            }
            string SpawnScenario(bool enabled)
            {
                Switch(own,2,enabled);var o=Spawner();var states=States();spawn(o,2,4,2,new(),[],states);var queue=Get<ConcurrentQueue<EntitySpawnerResult>>(o,"readyToSpawn");Check(queue.Count==1,"one original group queued");var first=queue.First();var owned=Get<List<SpawnOppurtunity>>(first,"spawnPositions");var firstPos=owned[0].Pos.Clone();spawn(o,2,4,2,new(),[],new());Check(owned.Count==1&&owned[0].Pos.Equals(firstPos),"queued spawn list survives scratch reuse");return SpawnState(o,states);
            }
            string before=SpawnScenario(false);Check(SpawnScenario(true)==before,"spawn coordinates, RNG, caps, group counts and queue contents equal stock");
            failSpawn=true;try{spawn(Spawner(),2,4,2,new(),[],States());throw new Exception("missing spawn failure");}catch(InvalidOperationException e){Check(e.Message=="spawn-sentinel","spawn exception retained");}finally{failSpawn=false;}
            for(int mode=0;mode<2;mode++)
            {
                Switch(own,3,mode==1);events.Clear();var owner=Spawner();chunks=Enumerable.Range(0,8).Select(i=>Proxy.Make<IWorldChunk>((m,a)=>{if(record&&m.Name is "AcquireBlockReadLock" or "ReleaseBlockReadLock")events.Add(i+":"+m.Name);return m.ReturnType!=typeof(void)&&m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null;})).ToArray();missing=-1;
                column(owner,2,4,2,new(),new());Check(events.Count==16,"all acquired column locks released "+mode+": "+mapCalls+"/"+chunkCalls+" "+string.Join(",",events));
                events.Clear();missing=3;column(owner,2,4,2,new(),new());Check(events.Count==14&&!events.Any(s=>s.StartsWith("3:")),"missing chunk preserves lock/read/skip behavior "+mode);missing=-1;
                // The fixture intentionally lacks a game logger. Even when error
                // reporting throws, native finally must release every read lock.
                events.Clear();failSpawn=true;try{column(Spawner(),2,4,2,new(),States());}catch(Exception){}finally{failSpawn=false;}
                Check(events.Count==16,"read locks released after inner or logging failure "+mode);

            }
            CallbackSpawnOptimizations.Configure(0,true);CallbackSpawnOptimizations.TickPrefix(out var outer);var key=new BlockPos(0);var list=CallbackSpawnOptimizations.TickSnapshot(new[]{key});CallbackSpawnOptimizations.TickPrefix(out var inner);Check(!ReferenceEquals(list,CallbackSpawnOptimizations.TickSnapshot([])),"nested callback snapshots isolated");CallbackSpawnOptimizations.Finalizer(null,inner);Check(ReferenceEquals(list,CallbackSpawnOptimizations.TickSnapshot(new[]{key})),"outer snapshot restored");CallbackSpawnOptimizations.Finalizer(null,outer);Check(list.Count==0&&outer!.Parent==null,"snapshot references and parent cleared");
            CallbackSpawnOptimizations.Configure(3,true);CallbackSpawnOptimizations.ColumnPrefix(out outer);var array=CallbackSpawnOptimizations.Column(8);array[0]=chunks[0];CallbackSpawnOptimizations.Finalizer(null,outer);Check(array.All(c=>c==null),"chunk references cleared after return");CallbackSpawnOptimizations.ColumnPrefix(out outer);Check(ReferenceEquals(array,CallbackSpawnOptimizations.Column(8)),"same-size chunk array reused");Check(CallbackSpawnOptimizations.Column(4).Length==4,"column size change uses exact length");CallbackSpawnOptimizations.Finalizer(null,outer);
            CallbackSpawnOptimizations.ColumnPrefix(out outer);Check(CallbackSpawnOptimizations.Column(257).Length==257&&outer!.Column?.Length!=257,"oversized column uses unretained stock allocation");CallbackSpawnOptimizations.Finalizer(null,outer);
            CallbackSpawnOptimizations.Configure(0,true);CallbackSpawnOptimizations.TickPrefix(out outer);list=CallbackSpawnOptimizations.TickSnapshot([]);list.Capacity=8192;long released=CallbackSpawnOptimizations.Counters(0).Released;CallbackSpawnOptimizations.Finalizer(null,outer);Check(CallbackSpawnOptimizations.Counters(0).Released==released+1,"oversized snapshots discarded");
            CallbackSpawnOptimizations.Configure(0,true);CallbackSpawnOptimizations.TickPrefix(out outer);bool argument=false;try{CallbackSpawnOptimizations.TickSnapshot(null!);}catch(ArgumentNullException e){argument=e.ParamName=="collection";}CallbackSpawnOptimizations.Finalizer(null,outer);Check(argument,"null snapshot retains native argument name");
            var lists=new ConcurrentBag<object>();CallbackSpawnOptimizations.Configure(0,true);var workers=Enumerable.Range(0,4).Select(_=>new Thread(()=>{CallbackSpawnOptimizations.TickPrefix(out var f);lists.Add(CallbackSpawnOptimizations.TickSnapshot([]));CallbackSpawnOptimizations.Finalizer(null,f);})).ToArray();foreach(var thread in workers)thread.Start();foreach(var thread in workers)thread.Join();Check(lists.Distinct(ReferenceEqualityComparer.Instance).Count()==4,"callback snapshots isolated across threads");
            CallbackSpawnOptimizations.Configure(2,true);CallbackSpawnOptimizations.SpawnPrefix(out var spawnScope);var scratch=CallbackSpawnOptimizations.Opportunities();scratch.Add(new SpawnOppurtunity{Pos=new Vec3d(1,2,3)});var ownedCopy=new List<SpawnOppurtunity>(scratch);var sentinel=new InvalidOperationException("lease");Check(ReferenceEquals(sentinel,CallbackSpawnOptimizations.Finalizer(sentinel,spawnScope)),"spawn finalizer preserves original exception");Check(scratch.Count==0&&ownedCopy.Count==1&&spawnScope!.Parent==null,"spawn scratch clears without altering queued-style owned copy");
            bool rejected=false;try{CallbackSpawnOptimizations.Transpiler([],CallbackSpawnOptimizations.Target(2)).ToArray();}catch(InvalidOperationException){rejected=true;}Check(rejected,"unexpected spawn scratch/copy layout rejected");
            var trials=new List<object>();foreach(bool on in new[]{false,true,false,true})trials.Add(MeasureCallback(own,on,1342));foreach(bool on in new[]{false,true,false,true})trials.Add(MeasureSpawn(own,on));foreach(bool on in new[]{false,true,false,true})trials.Add(MeasureColumn(own,on));
            Console.WriteLine(JsonSerializer.Serialize(new{checks,trials,scope="Official methods and seeded synthetic fixtures; spawn-position/chunk access intercepted. No production TPS or player-capacity claim."}));
        }
        finally{for(int k=0;k<4;k++)CallbackSpawnOptimizations.Configure(k,false);own.UnpatchAll(own.Id);fixture.UnpatchAll(fixture.Id);}
    }
}
