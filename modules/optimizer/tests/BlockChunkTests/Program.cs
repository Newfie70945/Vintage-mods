using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

string game = Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving += (_, name) => {
    foreach (var dir in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") }) {
        string path = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
Checks.Run();

public class Proxy : DispatchProxy
{
    public System.Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    public static T Make<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class {
        var value = Create<T, Proxy>(); ((Proxy)(object)value).Handler = handler; return value;
    }
}
public class FixtureBlock : Block
{
    public Action<BlockPos>? Callback;
    public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neighbor) => Callback?.Invoke(pos);
}
static class Checks
{
    private static int checks, sends, failSend;
    private static bool record = true;
    private static Action? onSend;
    private static readonly List<string> events = new();
    private static readonly List<Packet_Server> packets = new();
    private static readonly List<(long Key, int X, int Z, int Pass)> requests = new();
    private static readonly List<BlockPos> retainedPositions = new();
    private static T New<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Set(object o, string name, object? value) => AccessTools.Field(o.GetType(), name).SetValue(o, value);
    private static T Get<T>(object o, string name) => (T)AccessTools.Field(o.GetType(), name).GetValue(o)!;
    private static void Check(bool result, string message) { if (!result) throw new Exception(message); checks++; }
    private static void Send() { if (++sends == failSend) throw new InvalidOperationException("send-sentinel"); onSend?.Invoke(); }
    private static bool CapturePacket(int clientId, Packet_Server packet) {
        Send(); if (record) { packets.Add(packet); events.Add($"{clientId}:{string.Join(',',packet.UnloadChunk.X)}:{string.Join(',',packet.UnloadChunk.Y)}:{string.Join(',',packet.UnloadChunk.Z)}"); } return false;
    }
    private static bool CaptureBlocks(List<BlockPos> positions, int packetId) {
        Send(); if (record) events.Add(packetId + ":" + string.Join(';', positions.Select(p => $"{p.X},{p.InternalY},{p.Z}"))); return false;
    }
    private static bool CaptureDecors(List<BlockPos> positions) {
        Send(); if (record) events.Add("decor:" + string.Join(';', positions.Select(p => p.ToString()))); return false;
    }
    private static bool CaptureRequest(long index2d, int chunkX, int chunkZ, int clientid) {
        if (record) requests.Add((index2d, chunkX, chunkZ, clientid)); return false;
    }
    private static ServerMain Server(int clients = 0) {
        var s = New<ServerMain>(); s.WorldMap = New<ServerWorldMap>();
        Set(s.WorldMap,"index3dMulX",1024); Set(s.WorldMap,"index3dMulZ",1024); Set(s.WorldMap,"mapsize",new Vec3i(32768,256,32768));
        s.Clients = new CachingConcurrentDictionary<int, ConnectedClient>();
        s.UpdatedBlocks = new(); s.DirtyBlocks = new(); s.ModifiedBlocks = new(); s.ModifiedBlocksNoRelight = new(); s.ModifiedBlocksMinimal = new(); s.ModifiedDecors = new();
        Set(s,"unloadedChunks",new ConcurrentQueue<long>()); Set(s,"requestedChunkColumnsLock",new object()); Set(s,"requestedChunkColumns",new UniqueQueue<long>());
        Set(s,"loadedMapChunks",new ConcurrentDictionary<long,ServerMapChunk>()); Set(s,"forceLoadedChunkColumns",new HashSet<long>());
        for (int i=0;i<clients;i++) { var c=New<ConnectedClient>(); c.Id=i; Set(c,"ChunkSent",new HashSet<long>()); Set(c,"MapChunkSent",new HashSet<long>()); s.Clients.TryAdd(i,c); }
        _ = s.Clients.Values; return s;
    }
    private static object Owner(int kind, ServerMain server) {
        var value = RuntimeHelpers.GetUninitializedObject(BlockChunkScratchOptimizations.Target(kind).DeclaringType!); Set(value,"server",server);
        if (kind==2) { Set(value,"mapChunkIndicesLock",new object()); Set(value,"mapChunkIndices",new List<long>()); Set(value,"mapChunkUnloadCandidates",new HashSet<long>()); }
        if (kind==1) {
            var t=New<ChunkServerThread>(); Set(t,"server",server);
            var type=AccessTools.Field(typeof(ChunkServerThread),"requestedChunkColumns").FieldType;
            Set(t,"requestedChunkColumns",Activator.CreateInstance(type,[1024,1])); Set(value,"chunkthread",t);
        }
        return value;
    }
    private static Action Invoke(object owner,int kind) => BlockChunkScratchOptimizations.Target(kind).CreateDelegate<Action>(owner);
    private static void Switch(Harmony harmony,int kind,bool value) {
        harmony.UnpatchAll(harmony.Id); BlockChunkScratchOptimizations.Configure(kind,value);
        if (!value) return;
        string[] prefixes=[nameof(BlockChunkScratchOptimizations.BlockPrefix),nameof(BlockChunkScratchOptimizations.LoadPrefix),nameof(BlockChunkScratchOptimizations.CandidatesPrefix),nameof(BlockChunkScratchOptimizations.NotifyPrefix)];
        harmony.Patch(BlockChunkScratchOptimizations.Target(kind),prefix:new HarmonyMethod(typeof(BlockChunkScratchOptimizations),prefixes[kind]),transpiler:new HarmonyMethod(typeof(BlockChunkScratchOptimizations),nameof(BlockChunkScratchOptimizations.Transpiler)),finalizer:new HarmonyMethod(typeof(BlockChunkScratchOptimizations),nameof(BlockChunkScratchOptimizations.Finalizer)));
    }
    private static void Populate(ServerMain server,int n) {
        for(int j=0;j<n;j++) { long index=(long)(j%1024)+1024L*(j/1024+1024L*(j%20)); Get<ConcurrentQueue<long>>(server,"unloadedChunks").Enqueue(index); foreach(var c in server.Clients.Values) { Get<HashSet<long>>(c,"ChunkSent").Add(index); Get<HashSet<long>>(c,"MapChunkSent").Add(server.WorldMap.ChunkIndex3dToIndex2d(index)); } }
    }
    private static object Measure(Harmony harmony,int kind,bool value,int entries) {
        Switch(harmony,kind,value); var server=Server(kind==3?4:0); var invoke=Invoke(Owner(kind,server),kind);
        var positions=Enumerable.Range(0,entries).Select(i=>new BlockPos(i,20,30)).ToArray();
        void Work() { if(kind==0)foreach(var pos in positions)server.ModifiedDecors.Enqueue(pos); if(kind==3)Populate(server,entries); if(kind==1) { var q=Get<UniqueQueue<long>>(server,"requestedChunkColumns");for(int i=0;i<entries;i++)q.Enqueue(i+1024); } invoke(); }
        record=false; for(int i=0;i<500;i++)Work(); var watch=new Stopwatch(); long before=GC.GetAllocatedBytesForCurrentThread(); watch.Start();
        for(int i=0;i<3000;i++)Work(); watch.Stop(); record=true;
        return new {kind,enabled=value,entries,bytesPerCall=(GC.GetAllocatedBytesForCurrentThread()-before)/3000d,nsPerCall=watch.Elapsed.TotalNanoseconds/3000};
    }
    public static void Run() {
        var own=new Harmony("test.nos.block-chunk"); var transport=new Harmony("test.nos.block-chunk.transport");
        transport.Patch(AccessTools.Method(typeof(ServerMain),"SendPacket",[typeof(int),typeof(Packet_Server)]),prefix:new HarmonyMethod(typeof(Checks),nameof(CapturePacket)));
        transport.Patch(AccessTools.Method(typeof(ServerMain),"SendSetBlocksPacket"),prefix:new HarmonyMethod(typeof(Checks),nameof(CaptureBlocks)));
        transport.Patch(AccessTools.Method(typeof(ServerMain),"SendSetDecorsPackets"),prefix:new HarmonyMethod(typeof(Checks),nameof(CaptureDecors)));
        transport.Patch(AccessTools.Method(typeof(ChunkServerThread),"addChunkColumnRequest",[typeof(long),typeof(int),typeof(int),typeof(int),typeof(EnumWorldGenPass),typeof(ITreeAttribute)]),prefix:new HarmonyMethod(typeof(Checks),nameof(CaptureRequest)));
        try {
            for(int k=0;k<4;k++)Check(BlockChunkScratchOptimizations.ValidateContract(k,out _),"official contract "+k);
            string BlockScenario(bool enabled) {
                Switch(own,0,enabled); events.Clear(); retainedPositions.Clear(); var s=Server();
                var b=new FixtureBlock { BlockId=1, Callback=p=>{ events.Add("neighbor:"+p.X); retainedPositions.Add(p); } };
                s.WorldMap.RelaxedBlockAccess=Proxy.Make<IBlockAccessor>((m,a)=>m.Name=="GetBlock"?b:null);
                var pos=new BlockPos(12,20,30,2); s.ModifiedBlocks.Enqueue(pos); s.ModifiedBlocksNoRelight.Enqueue(pos.Copy()); s.ModifiedDecors.Enqueue(pos.Copy());
                Invoke(Owner(0,s),0)();
                Check(s.ModifiedBlocks.IsEmpty&&s.ModifiedBlocksNoRelight.IsEmpty&&s.ModifiedDecors.IsEmpty,"block queues drained");
                Check(retainedPositions.Count==3&&ReferenceEquals(pos,retainedPositions[0])&&pos.X==12&&pos.dimension==2,"callbacks retain original position objects and dimensions");
                return string.Join('|',events);
            }
            var stock=BlockScenario(false); Check(BlockScenario(true)==stock,"block callback/send order and solid/fluid/no-relight/decor batches preserved");
            string LoadScenario(bool enabled,int entries=3) {
                Switch(own,1,enabled); requests.Clear(); var s=Server(); var q=Get<UniqueQueue<long>>(s,"requestedChunkColumns"); for(int i=0;i<entries;i++)q.Enqueue(1025+i*3);
                Invoke(Owner(1,s),1)(); Check(entries<1000?q.Count==0:q.Count>0,"request queue capacity/backpressure retained"); return q.Count+":"+string.Join(';',requests);
            }
            stock=LoadScenario(false);Check(LoadScenario(true)==stock,"chunk requests preserve original order, coordinates and pass");
            stock=LoadScenario(false,1000);Check(LoadScenario(true,1000)==stock,"full generation queue keeps original staging limit and pending requests");
            string CandidateScenario(bool enabled) {
                Switch(own,2,enabled);var s=Server();var owner=Owner(2,s);var indices=Get<List<long>>(owner,"mapChunkIndices");var maps=Get<ConcurrentDictionary<long,ServerMapChunk>>(s,"loadedMapChunks");
                for(int i=0;i<6;i++){var map=New<ServerMapChunk>();Set(map,"currentpass",(int)(i==3?EnumWorldGenPass.Terrain:EnumWorldGenPass.Done));map.UnloadGeneration=(byte)(i==1?4:1);maps[i]=map;indices.Add(i);}
                Get<HashSet<long>>(s,"forceLoadedChunkColumns").Add(2);Invoke(owner,2)();
                var candidates=Get<HashSet<long>>(owner,"mapChunkUnloadCandidates");Check(candidates.SetEquals(new long[]{0,4,5})&&maps[1].UnloadGeneration==3,"unload age, forced-load and generation-completion rules retained");
                return string.Join(',',candidates.OrderBy(x=>x));
            }
            stock=CandidateScenario(false);Check(CandidateScenario(true)==stock,"same unload candidates and age updates");
            string NotifyScenario(bool enabled) {
                Switch(own,3,enabled); events.Clear();packets.Clear();var s=Server(3);Populate(s,12);Get<HashSet<long>>(s.Clients[1],"ChunkSent").Clear();
                Invoke(Owner(3,s),3)();Check(Get<ConcurrentQueue<long>>(s,"unloadedChunks").IsEmpty,"unload queue drained");Check(Get<HashSet<long>>(s.Clients[0],"ChunkSent").Count==0,"client tracking removed");Check(packets.Count==2,"only recipients tracking chunks receive unloads");return string.Join('|',events);
            }
            stock=NotifyScenario(false);Check(NotifyScenario(true)==stock,"per-client unload packet coordinates and order equal stock");
            var owned=packets[0].UnloadChunk.X;var copy=owned.ToArray();NotifyScenario(true);Check(owned.SequenceEqual(copy),"outgoing packet arrays remain independently owned after reuse");
            var fixture=Server(2);var run=Invoke(Owner(3,fixture),3);Populate(fixture,8);bool nested=false;
            onSend=()=>{if(nested)return;nested=true;var s=Server(1);Populate(s,2);Invoke(Owner(3,s),3)();};run();onSend=null;Check(nested,"recursive notification uses independent scope");
            Populate(fixture,8);sends=0;failSend=2;try{run();throw new Exception("missing send failure");}catch(InvalidOperationException e){Check(e.Message=="send-sentinel"&&sends==2,"partial sends preserve exception without replay");}finally{failSend=0;}
            Check(BlockChunkScratchOptimizations.Counters(3).Failures>0,"failure diagnostics recorded");
            BlockChunkScratchOptimizations.Configure(0,true);BlockChunkScratchOptimizations.BlockPrefix(out var outer);var list=BlockChunkScratchOptimizations.Blocks();list.Add(new BlockPos(1,2,3));
            BlockChunkScratchOptimizations.BlockPrefix(out var inner);Check(!ReferenceEquals(list,BlockChunkScratchOptimizations.Blocks()),"nested block scopes isolated");BlockChunkScratchOptimizations.Finalizer(null,inner);Check(ReferenceEquals(list,BlockChunkScratchOptimizations.Blocks()),"outer block scope restored");
            BlockChunkScratchOptimizations.Configure(0,false);BlockChunkScratchOptimizations.BlockPrefix(out inner);Check(!ReferenceEquals(list,BlockChunkScratchOptimizations.Blocks()),"disable during nesting retains isolation");BlockChunkScratchOptimizations.Finalizer(null,inner);BlockChunkScratchOptimizations.Finalizer(null,outer);Check(list.Count==0&&outer!.Parent==null,"references and parent cleared");
            BlockChunkScratchOptimizations.Configure(0,true);BlockChunkScratchOptimizations.BlockPrefix(out outer);list=BlockChunkScratchOptimizations.Blocks();list.Capacity=8192;long released=BlockChunkScratchOptimizations.Counters(0).Released;BlockChunkScratchOptimizations.Finalizer(null,outer);Check(BlockChunkScratchOptimizations.Counters(0).Released==released+1,"oversized list not retained");
            bool rejected=false;try{BlockChunkScratchOptimizations.Transpiler([],BlockChunkScratchOptimizations.Target(0)).ToArray();}catch(InvalidOperationException){rejected=true;}Check(rejected,"unexpected IL rejected");
            BlockChunkScratchOptimizations.Configure(1,true);BlockChunkScratchOptimizations.LoadPrefix(out var requestScope);var map=Server().WorldMap;
            foreach(long index in new[]{long.MinValue,-1L,0L,1025L,long.MaxValue}) {var expected=map.MapChunkPosFromChunkIndex2D(index);var actual=BlockChunkScratchOptimizations.RequestCoordinate(map,index);Check(expected.X==actual.X&&expected.Y==actual.Y,"request coordinate math preserves index "+index);}
            BlockChunkScratchOptimizations.Finalizer(null,requestScope);
            BlockChunkScratchOptimizations.Configure(3,true);BlockChunkScratchOptimizations.NotifyPrefix(out var bigScope);var coords=BlockChunkScratchOptimizations.Coordinates();
            for(int i=0;i<4097;i++)coords.Add(BlockChunkScratchOptimizations.Coordinate(i,0,0));
            Check(bigScope!.CoordinatePool.Count==4096,"coordinate retention cap enforced");BlockChunkScratchOptimizations.Finalizer(null,bigScope);Check(coords.Count==0,"oversized coordinate list cleared");
            var parallel=new ConcurrentBag<object>();var threads=Enumerable.Range(0,4).Select(_=>new Thread(()=>{BlockChunkScratchOptimizations.Configure(1,true);BlockChunkScratchOptimizations.LoadPrefix(out var f);parallel.Add(BlockChunkScratchOptimizations.LoadKeys());BlockChunkScratchOptimizations.Finalizer(null,f);})).ToArray();foreach(var thread in threads)thread.Start();foreach(var thread in threads)thread.Join();Check(parallel.Distinct(ReferenceEqualityComparer.Instance).Count()==4,"thread-local buffers remain isolated");
            var trials=new List<object>();foreach(int k in new[]{0,1,2,3})foreach(bool mode in new[]{false,true,false,true})trials.Add(Measure(own,k,mode,k==2?0:64));
            Console.WriteLine(JsonSerializer.Serialize(new {checks,trials,scope="Official 1.22.7 methods, synthetic fixtures with intercepted sends/requests. Includes fixture queue work; no player/TPS claim."}));
        }
        finally {for(int k=0;k<4;k++)BlockChunkScratchOptimizations.Configure(k,false);own.UnpatchAll(own.Id);transport.UnpatchAll(transport.Id);}
    }
}
