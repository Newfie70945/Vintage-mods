using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY."));
string modPath = Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? args.FirstOrDefault() ?? throw new Exception("Set NSS_ASSEMBLY."));
string output = Path.GetDirectoryName(modPath)!;
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (string root in new[] { output, game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") }) {
        string file = Path.Combine(root, name.Name + ".dll");
        if (File.Exists(file)) return context.LoadFromAssemblyPath(file);
    }
    return null;
};
var library = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryLib.dll"));
var apiAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryAPI.dll"));
var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);
Type T(string name) => library.GetType(name) ?? apiAssembly.GetType(name) ?? mod.GetType(name) ?? throw new Exception(name);
FieldInfo Field(Type type, string name) {
    for (Type? t = type; t != null; t = t.BaseType) {
        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (f != null) return f;
    }
    throw new Exception("Missing field " + name);
}
void Set(object target, string name, object? value) => Field(target.GetType(), name).SetValue(target, value);
object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, args);
object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler) {
    var result = DispatchProxy.Create(type, typeof(ApiProxy)); ((ApiProxy)result).Handler = handler; return result;
}
object? Default(Type t) => t == typeof(void) ? null : t.IsValueType ? Activator.CreateInstance(t) : null;
var serverType = T("Vintagestory.Server.ServerMain");
object server = RuntimeHelpers.GetUninitializedObject(serverType);
var profiler = Field(serverType, "FrameProfiler");
profiler.SetValue(null, Activator.CreateInstance(profiler.FieldType, "ore-fixture"));
var poolType = T("Vintagestory.Common.ChunkDataPool");
object pool = Activator.CreateInstance(poolType, 32, server)!;
var chunkType = T("Vintagestory.Server.ServerChunk");
object chunk = chunkType.GetMethod("CreateNew")!.Invoke(null, new[] { pool })!;
object data = chunkType.GetProperty("Data")!.GetValue(chunk)!;
var setBlock = data.GetType().GetMethod("SetBlockUnsafe")!;
int Index(int x, int y, int z) => (y * 32 + z) * 32 + x;
int buried = Index(10, 10, 10), exposed = Index(20, 10, 10), placed = Index(15, 10, 10);
for (int i = 0; i < 32768; i++) setBlock.Invoke(data, new object[] { i, 1 });
foreach (int i in new[] { buried, exposed, placed }) setBlock.Invoke(data, new object[] { i, 2 });
setBlock.Invoke(data, new object[] { exposed + 1, 0 });
var accessorType = T("Vintagestory.API.Common.IBlockAccessor");
object[] fixtureBlocks = Enumerable.Range(0, 5).Select(id => { var block = Activator.CreateInstance(T("Vintagestory.API.Common.Block"))!; Set(block, "BlockId", id); return block; }).ToArray();
int worldReads = 0;
object accessor = Proxy(accessorType, (method, args) => {
    if (method.Name == "GetChunk") worldReads++;
    if (method.Name == "GetBlock" && args?[0]?.GetType() == T("Vintagestory.API.MathTools.BlockPos")) {
        object p = args[0]!;
        int x = (int)Field(p.GetType(), "X").GetValue(p)!, y = (int)Field(p.GetType(), "Y").GetValue(p)!, z = (int)Field(p.GetType(), "Z").GetValue(p)!;
        int id = (int)data.GetType().GetMethod("GetBlockIdUnsafe", new[] { typeof(int) })!.Invoke(data, new object[] { Index(x & 31, y & 31, z & 31) })!;
        return fixtureBlocks[id];
    }
    return method.Name == "GetChunk" && args?.Length == 3 && args.All(x => Convert.ToInt32(x) == 0) ? chunk : Default(method.ReturnType);
});
object worldMap = RuntimeHelpers.GetUninitializedObject(T("Vintagestory.Server.ServerWorldMap"));
Set(worldMap, "RelaxedBlockAccess", accessor); Set(server, "WorldMap", worldMap);
var virtualizerType = T("BlockLogger.AntiCheat.OreShieldBlockVirtualizer");
var configType = T("BlockLogger.AntiCheat.AntiCheatConfig");
object config = Activator.CreateInstance(configType)!;
var paletteType = T("BlockLogger.AntiCheat.OreShieldPalette");
object palette = RuntimeHelpers.GetUninitializedObject(paletteType);
Set(palette, "<Opaque>k__BackingField", new[] { false, true, true, true, true });
Set(palette, "<ProtectedOre>k__BackingField", new[] { false, false, true, true, true });
Set(palette, "<HostByBlock>k__BackingField", new[] { 0, 0, 1, 1, 1 });
var online = new List<object>();
Type playerType = T("Vintagestory.API.Server.IServerPlayer");
Type clientType = T("Vintagestory.Server.ConnectedClient");
object clients = Activator.CreateInstance(Field(serverType, "Clients").FieldType)!;
Set(server, "Clients", clients);
for (int i = 0; i < 21; i++)
{
    int clientId = i;
    string uid = "sync-player-" + i;
    Type nativePlayerType=T("Vintagestory.Server.ServerPlayer");
    object player=RuntimeHelpers.GetUninitializedObject(nativePlayerType);
    var dataField=Field(nativePlayerType,"worlddata");
    object playerData=RuntimeHelpers.GetUninitializedObject(dataField.FieldType);
    Set(playerData,"PlayerUID",uid);dataField.SetValue(player,playerData);Set(player,"server",server);
    object client = RuntimeHelpers.GetUninitializedObject(clientType);
    Set(client,"Id",i);clientType.GetProperty("State")!.SetValue(client,Enum.Parse(clientType.GetProperty("State")!.PropertyType,"Playing"));
    Set(player,"client",client);
    online.Add(player);
    Set(client,"ChunkSent",i==0 ? new HashSet<long>{0L} : new HashSet<long>());
    clients.GetType().GetProperty("Item")!.SetValue(clients,client,new object[]{i});
}
var callbacks = new List<Delegate>();
var debugLines = new List<string>();
object OnlineArray(Type type) { var array=Array.CreateInstance(type.GetElementType()!,online.Count);for(int i=0;i<online.Count;i++)array.SetValue(online[i],i);return array; }
object api = Proxy(Field(virtualizerType,"api").FieldType,(m,a)=>m.Name switch {
    "get_Event"=>Proxy(m.ReturnType,(em,ea)=>{
        if(em.Name=="RegisterCallback"){callbacks.Add((Delegate)ea![0]!);return 1L;}
        return em.Name=="RegisterGameTickListener" ? 1L : Default(em.ReturnType);
    }),
    "get_Logger"=>Proxy(m.ReturnType,(lm,la)=>{if(lm.Name=="Debug")debugLines.Add((string)la![0]!);return Default(lm.ReturnType);}),
    "get_WorldManager"=>Proxy(m.ReturnType,(wm,wa)=>wm.Name=="get_SaveGame" ? Proxy(wm.ReturnType,(sm,sa)=>Default(sm.ReturnType)) : Default(wm.ReturnType)),
    "get_World"=>Proxy(m.ReturnType,(wm,wa)=>wm.Name=="get_AllOnlinePlayers" ? OnlineArray(wm.ReturnType) : wm.Name=="PlayerByUid" ? online.FirstOrDefault(p=>(string)playerType.GetProperty("PlayerUID")!.GetValue(p)! ==(string)wa![0]!) : Default(wm.ReturnType)),
    _=>Default(m.ReturnType)
});
SetMember(config,"OreShieldEnabled",true);
var sentPackets = new List<object>();
object? virtualizer=null;
bool syncAck=false;
void Ack(object packet,object player)
{
    object ack=Activator.CreateInstance(T("NewfiesAntiCheat.Shared.Packets.OreShieldSyncAckPacket"))!;
    foreach(string key in new[]{"Sequence","ChunkX","ChunkY","ChunkZ","Revision"}) SetMember(ack,key,Get(packet,key));
    SetMember(ack,"Applied",((int[])Get(packet,"LocalIndices")!).Length);
    Call(virtualizer!,"HandleClientSyncAck",player,ack);
}
object channel=Proxy(Field(virtualizerType,"oreShieldSyncChannel").FieldType,(m,a)=>{
    if(m.Name=="SendPacket") {sentPackets.Add(a![0]!);if(syncAck)Ack(a[0]!,((Array)a[1]!).GetValue(0)!);}
    return Default(m.ReturnType);
});
Type capability=virtualizerType.GetConstructors().Single().GetParameters()[5].ParameterType;
var parameter=System.Linq.Expressions.Expression.Parameter(playerType,"player");
var supports=System.Linq.Expressions.Expression.Lambda(capability,System.Linq.Expressions.Expression.Constant(true),parameter).Compile();
virtualizer=Activator.CreateInstance(virtualizerType,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new[]{api,server,config,palette,channel,supports},null)!;
var posType=T("Vintagestory.API.MathTools.BlockPos");
object Position(int index)=>Activator.CreateInstance(posType,index&31,index>>10,(index>>5)&31,0)!;
object Positions(params int[] indices){var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(posType))!;foreach(int i in indices)list.Add(Position(i));return list;}
object? Get(object o,string name)=>o.GetType().GetProperty(name)!.GetValue(o);
void SetMember(object o,string name,object? value)=>o.GetType().GetProperty(name)!.SetValue(o,value);
int CountField(string name)=>(int)Get(Field(virtualizerType,name).GetValue(virtualizer!)!,"Count")!;
long LongField(string name)=>(long)Field(virtualizerType,name).GetValue(virtualizer!)!;
void Check(bool condition,string message){if(!condition)throw new Exception(message);}
void Pump(){Set(virtualizer!,"clientSyncLastPumpMs",Environment.TickCount64-100);Call(virtualizer!,"PumpClientSync");}
for(int i=0;i<24;i++)Call(virtualizer,"SyncAuthoritativeWorldCellToAllClients",Position(i));
Check(callbacks.Count==1,"24 mining events scheduled multiple flush callbacks");
callbacks[0].DynamicInvoke(0f);
Check((int)Field(virtualizerType,"clientSyncQueuedCells").GetValue(virtualizer)! ==24,"Distant clients received mined-cell cancellations");
for(int i=0;i<5 && sentPackets.Count==0;i++)Pump();
Check(sentPackets.Count==1 && ((int[])Get(sentPackets[0],"LocalIndices")!).Length==24,"24 mined blocks were not coalesced into one chunk packet");
Check(callbacks.Count==1,"Sync sender registered per-packet timeout callbacks");
Ack(sentPackets[0],online[0]);
Check(CountField("pendingClientSync")==0,"Valid final ACK did not close batch");
Console.WriteLine("PASS: 24 mined blocks x 21 players produce one 24-cell packet for the nearby recipient; no per-sequence timeout callbacks.");
// Authorize a discovery before the initial chunk is marked sent; it must still be delivered.
object key=Activator.CreateInstance(virtualizerType.GetNestedType("WorldBlockKey",BindingFlags.NonPublic)!,10,10,10,0)!;
Call(virtualizer,"MarkDiscoveredWithoutLosCounter","sync-player-1",key);
Call(virtualizer,"TrySendClientSync",online[1],Positions(Index(10,10,10)),0,true);
int before=sentPackets.Count;for(int i=0;i<5 && sentPackets.Count==before;i++)Pump();
Check(sentPackets.Count==before+1,"Authorized reveal before chunk installation was dropped");
Ack(sentPackets[^1],online[1]);
Console.WriteLine("PASS: authorized pre-install reveals retain client deferral and acknowledgements.");
// Capacity provides backpressure: unsent cells remain queued while 32 batches await this client.
Call(virtualizer,"TrySendClientSync",online[0],Positions(Enumerable.Range(0,5120).ToArray()),0,false);
for(int i=0;i<120 && CountField("pendingClientSync")<32;i++)Pump();
Check(CountField("pendingClientSync")==32,"Per-player in-flight batch limit not reached");
Check((int)Field(virtualizerType,"clientSyncQueuedCells").GetValue(virtualizer)! ==1024,"Unsent cells were lost at in-flight capacity");
long timeouts=LongField("clientSyncTimeouts"),clock=LongField("clientSyncClockMs");
Set(virtualizer,"clientSyncLastPumpMs",Environment.TickCount64-10000);Call(virtualizer,"PumpClientSync");
Check(LongField("clientSyncClockMs")-clock==500 && LongField("clientSyncTimeouts")==timeouts,"Server pause expired all outstanding ACKs");
Console.WriteLine("PASS: in-flight limit retains queued corrections; a 10-second server pause adds 500ms of timeout age.");
// Reproduce the user's 510 simultaneous one-cell expirations across 21 recipients.
foreach(string field in new[]{"pendingClientSync","clientSyncPendingByPlayer","clientSyncPendingByChunk","clientSyncDeadlines","clientSyncOutbox","clientSyncWorldQueue","clientSyncRevealQueue"})Call(Field(virtualizerType,field).GetValue(virtualizer)!,"Clear");
Set(virtualizer,"clientSyncQueuedCells",0);
var pending=(IDictionary)Field(virtualizerType,"pendingClientSync").GetValue(virtualizer)!;
Type pendingKeyType=pending.GetType().GetGenericArguments()[0],pendingType=pending.GetType().GetGenericArguments()[1];
object deadlines=Field(virtualizerType,"clientSyncDeadlines").GetValue(virtualizer)!;
for(int i=0;i<510;i++)
{
    string uid="sync-player-"+(i%21);long sequence=900000L+i;
    object entry=Activator.CreateInstance(pendingType,true)!;
    SetMember(entry,"PlayerUid",uid);SetMember(entry,"Sequence",sequence);SetMember(entry,"Chunk",Activator.CreateInstance(virtualizerType.GetNestedType("ChunkKey",BindingFlags.NonPublic)!,0,0,0));
    var array=Array.CreateInstance(posType,1);array.SetValue(Position(i%24),0);SetMember(entry,"Positions",array);SetMember(entry,"Stabilize",false);
    object entryKey=Activator.CreateInstance(pendingKeyType,uid,sequence)!;Call(virtualizer,"TrackPendingClientSyncNoLock",entry);
    Call(deadlines,"Enqueue",entryKey,0L);
}
timeouts=LongField("clientSyncTimeouts");before=sentPackets.Count;int logs=debugLines.Count;
Pump();
Check(LongField("clientSyncTimeouts")-timeouts is >0 and <=16,"Timeout burst was not bounded per pump");
Check(sentPackets.Count-before<=16 && debugLines.Count-logs<=1,"Timeout burst produced a retry/log storm");
Check(callbacks.Count==1,"Timeout recovery created callback fan-out");
for(int i=0;i<120 && LongField("clientSyncTimeouts")-timeouts<510;i++)Pump();
Check(LongField("clientSyncTimeouts")-timeouts==510,"Bounded timeout queue did not eventually process backlog");
Console.WriteLine("PASS: 510 simultaneous expirations drain over bounded pumps with at most one aggregate log line, rather than 510 immediate retries/logs.");
for(int i=0;i<21;i++)Call(virtualizer,"RemovePlayerConnectionState","sync-player-"+i);
Check(CountField("pendingClientSync")==0 && CountField("clientSyncOutbox")==0,"Disconnect retained sync payload state");
syncAck=true;Call(virtualizer,"TrySendClientSync",online[0],Positions(1,2,3),0,false);for(int i=0;i<5 && CountField("clientSyncOutbox")>0;i++)Pump();
Check(CountField("pendingClientSync")==0,"Synchronous ACK raced pending bookkeeping");
Console.WriteLine("PASS: disconnect clears queued payloads and synchronous ACKs cannot race pending registration.");
// Newly queued world state supersedes a reveal; a mined ore never becomes stabilized air.
syncAck=false;int cell=Index(10,10,10);setBlock.Invoke(data,new object[]{cell,2});
Call(virtualizer,"TrySendClientSync",online[0],Positions(cell),0,true);
Call(virtualizer,"TrySendClientSync",online[0],Positions(cell),0,false);
before=sentPackets.Count;for(int i=0;i<5 && sentPackets.Count==before;i++)Pump();
Check(sentPackets.Count==before+1 && (int)Get(sentPackets[^1],"ApplyMode")! ==1,"World cancellation did not supersede queued stabilization");
Ack(sentPackets[^1],online[1]);Check(CountField("pendingClientSync")==1,"A different player acknowledged another player's pending batch");
Ack(sentPackets[^1],online[0]);
Call(virtualizer,"TrySendClientSync",online[0],Positions(cell),0,true);setBlock.Invoke(data,new object[]{cell,0});
before=sentPackets.Count;for(int i=0;i<5 && CountField("clientSyncOutbox")>0;i++)Pump();
Check(sentPackets.Count==before && CountField("pendingClientSync")==0,"Stale reveal pinned the mined cell's air value");setBlock.Invoke(data,new object[]{cell,2});
object syncRequest=Activator.CreateInstance(T("NewfiesAntiCheat.Shared.Packets.OreShieldChunkSyncRequestPacket"))!;
int reads=worldReads;Call(virtualizer,"HandleClientSyncRequest",online[1],syncRequest);
Check(worldReads==reads && CountField("pendingDiscoveryResyncs")==1,"Client resync request expanded retained discoveries synchronously");
Console.WriteLine("PASS: queued cancellation supersedes stabilization, mined air is not pinned, foreign ACKs cannot retire a batch, and chunk resync requests defer world work.");
// Compare real production DTO projections with a large but valid scan inventory.
Type serviceType=T("BlockLogger.AntiCheat.AntiCheatService");
object service=RuntimeHelpers.GetUninitializedObject(serviceType);
Set(service,"config",config);SetMember(config,"ClientModDbVerificationEnabled",false);SetMember(config,"CanonicalServerModIntegrityEnabled",false);
foreach(string field in new[]{"serverLoadedModIds","approvedClientModFingerprints","evidenceRecords","evidenceTransfers"})Set(service,field,Activator.CreateInstance(Field(serviceType,field).FieldType));
Set(service,"evidenceLock",new object());Set(service,"approvedClientModLock",new object());
object session=Activator.CreateInstance(T("BlockLogger.AntiCheat.AntiCheatSession"))!;
SetMember(session,"PlayerUid","web-player");SetMember(session,"PlayerName","Web player");
var mods=(IList)Get(session,"LastMods")!;var assemblies=(IList)Get(session,"LastAssemblies")!;var findings=(IList)Get(session,"Findings")!;
for(int i=0;i<200;i++)
{
    var dto=Activator.CreateInstance(T("NewfiesAntiCheat.Shared.Models.ClientModDto"))!;SetMember(dto,"ModId","fixturemod"+i);SetMember(dto,"Version","1.0.0");mods.Add(dto);
    var assembly=Activator.CreateInstance(T("NewfiesAntiCheat.Shared.Models.ClientAssemblyDto"))!;SetMember(assembly,"AssemblyName","Fixture"+i);
    SetMember(assembly,"ModSystemTypeNames",Enumerable.Range(0,50).Select(j=>"Fixture.ModSystem.LongNamespace.Type"+j).ToList());assemblies.Add(assembly);
}
for(int i=0;i<30;i++)
{
    var finding=Activator.CreateInstance(T("NewfiesAntiCheat.Shared.Models.ScanFindingDto"))!;SetMember(finding,"Code","PACKET_SPEED_HACK");SetMember(finding,"Scanner","ServerBehavior");SetMember(finding,"Severity",4);SetMember(finding,"Evidence",new string('x',32768));findings.Add(finding);
}
object detail=Call(service,"ToWebPlayer",session,false,true,false)!;
object overview=Call(service,"ToWebPlayer",session,false,false,false)!;
object modPage=Call(service,"ToWebPlayer",session,false,false,true)!;
Check(((IList)Get(overview,"Mods")!).Count==0 && ((IList)Get(overview,"Assemblies")!).Count==0,"Overview retained detailed inventories");
Check(((IList)Get(overview,"Findings")!).Count==12 && (int)Get(overview,"FindingCount")! ==30,"Overview finding limit corrupted full count");
Check(((IList)Get(detail,"Findings")!).Count==30 && ((IList)Get(detail,"Mods")!).Count==200 && ((IList)Get(detail,"Assemblies")!).Count==200,"Player investigation lost evidence/inventories");
Check(((IList)Get(modPage,"Mods")!).Count==200 && ((IList)Get(modPage,"Assemblies")!).Count==0,"Client mod page did not load comparisons on demand");
Check(((string)Get(findings[0]!,"Evidence")!).Length==32768,"Overview projection mutated session evidence");
int Bytes(object value)=>System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value).Length;
int detailedBytes=Bytes(detail),overviewBytes=Bytes(overview);
Check(overviewBytes<detailedBytes/10,"Overview payload reduction regressed");
Console.WriteLine($"PASS: production overview projection {overviewBytes:N0} bytes vs detailed {detailedBytes:N0} bytes ({100d*(1-overviewBytes/(double)detailedBytes):F1}% smaller for this fixture), with complete counters/evidence retained.");
Type webType=T("BlockLogger.WebDashboard.WebDashboardService");
bool Accepts(string? value)=>(bool)webType.GetMethod("AcceptsDashboardGzip",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object?[]{value})!;
Check(Accepts("br, gzip") && Accepts("*") && Accepts("GZip; q=0.5"),"Valid gzip negotiation rejected");
Check(!Accepts(null) && !Accepts("identity") && !Accepts("*;q=1,gzip;q=0") && !Accepts("gzip;q=broken"),"Gzip exclusion/fallback ignored");
byte[] plain=(byte[])webType.GetField("DashboardHtmlBytes",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
byte[] compressed=(byte[])webType.GetField("DashboardHtmlGzipBytes",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
using(var gzip=new System.IO.Compression.GZipStream(new MemoryStream(compressed),System.IO.Compression.CompressionMode.Decompress))
using(var restored=new MemoryStream()){gzip.CopyTo(restored);Check(restored.ToArray().SequenceEqual(plain),"Cached HTML gzip body corrupted");}
Check(compressed.Length<plain.Length/2,"Dashboard transfer reduction regressed");
Console.WriteLine($"PASS: gzip negotiation/fallback and byte-exact cached HTML round trip; dashboard {plain.Length:N0} -> {compressed.Length:N0} bytes.");
// Exercise actual HTTP parsing and root routing over TCP, including the identity fallback.
async Task<byte[]> DashboardHttp(string accept)
{
    var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
    try
    {
        using var client=new System.Net.Sockets.TcpClient();var connecting=client.ConnectAsync((System.Net.IPEndPoint)listener.LocalEndpoint);
        using var accepted=await listener.AcceptTcpClientAsync();await connecting;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        object web=RuntimeHelpers.GetUninitializedObject(webType);
        var read=(Task)webType.GetMethod("ReadRequestAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(web,new object[]{accepted.GetStream(),deadline.Token})!;
        await client.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: localhost\r\nAccept-Encoding: {accept}\r\n\r\n"),deadline.Token);
        await read;object request=read.GetType().GetProperty("Result")!.GetValue(read)!;
        var response=new MemoryStream();var copying=client.GetStream().CopyToAsync(response,deadline.Token);
        var routing=(Task)webType.GetMethod("RouteAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(web,new object[]{accepted.GetStream(),request,System.Net.IPAddress.Loopback,deadline.Token})!;
        await routing;accepted.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);await copying;return response.ToArray();
    }
    finally{listener.Stop();}
}
foreach(var encoding in new[]{"br,gzip","gzip;q=0,identity"})
{
    byte[] wire=await DashboardHttp(encoding);int separator=System.Text.Encoding.ASCII.GetString(wire).IndexOf("\r\n\r\n",StringComparison.Ordinal);
    string header=System.Text.Encoding.ASCII.GetString(wire,0,separator);
    bool gzip=encoding=="br,gzip";byte[] expected=gzip?compressed:plain;
    Check(header.StartsWith("HTTP/1.1 200") && header.Contains("Vary: Accept-Encoding") && header.Contains($"Content-Length: {expected.Length}"),"Root HTTP headers incorrect");
    Check(header.Contains("Content-Encoding: gzip")==gzip && wire.AsSpan(separator+4).SequenceEqual(expected),"Root negotiation/body mismatch");
}
Console.WriteLine("PASS: actual HTTP root routing returns correct gzip/identity bodies, Content-Length and Vary headers.");
string html=(string)T("BlockLogger.WebDashboard.WebDashboardPage").GetField("Html",BindingFlags.Static|BindingFlags.Public)!.GetValue(null)!;
File.WriteAllText(Environment.GetEnvironmentVariable("NSS_WEBPANEL_PREVIEW") ?? Path.Combine(Path.GetTempPath(),"nss-sync1-webpanel.html"),html);
Call(virtualizer,"Dispose");
public class ApiProxy:DispatchProxy
{
    public Func<MethodInfo,object?[]?,object?> Handler=null!;
    protected override object? Invoke(MethodInfo? targetMethod,object?[]? args)=>Handler(targetMethod!,args);
}
