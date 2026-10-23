using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var dir in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(dir,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
Checks.Run();
public class FilterPlayer:ServerPlayer,IServerPlayer
{
 public FilterPlayer():base(null!,null!){}
 public System.Func<int> Filter=null!;
 public new int ClientId=>Filter();
}
public class FixtureWire:DummyNetConnection
{
 public static int Prepared;
 private static readonly System.Func<BoxedPacket,int,byte[]> Copy=AccessTools.Method(typeof(BoxedPacket),"Clone").CreateDelegate<System.Func<BoxedPacket,int,byte[]>>();
 public override byte[] PreparePacketForSending(BoxedPacket box,bool allowed,out bool compressed){Prepared++;compressed=allowed;return Copy(box,0);}
}
static class Checks
{
 static int checks,attempts,failAt;static bool record=true;static readonly List<string> sent=new();
 static T New<T>()where T:class=>(T)RuntimeHelpers.GetUninitializedObject(typeof(T));
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 static void Capture(string text){attempts++;if(failAt==attempts)throw new InvalidOperationException("fixture-send");if(record)sent.Add(text);}
 static bool RawCapture(IServerPlayer player,byte[] packetBytes){if(!record){attempts++;return false;}Capture("raw:"+player.ClientId+":"+packetBytes.Length);return false;}
 static bool UdpCapture(ConnectedClient client,Packet_UdpPacket packet){Capture("udp:"+client.Id);return false;}
 static bool DespawnCapture(int clientId,Packet_Server packet){Capture("despawn:"+clientId+":"+string.Join(',',packet.EntityDespawn.EntityId)+":"+string.Join(',',packet.EntityDespawn.DespawnReason)+":"+string.Join(',',packet.EntityDespawn.DeathDamageSource));return false;}
 static bool PreparedCapture(ConnectedClient client,byte[] packetBytes,bool compressed){Capture("packet:"+client.Id+":"+Convert.ToHexString(packetBytes)+":"+compressed);return false;}
 static ServerMain Fixture(int count)
 {
  var s=New<ServerMain>();s.Clients=new CachingConcurrentDictionary<int,ConnectedClient>();s.Config=New<ServerConfig>();s.Config.CompressPackets=true;
  AccessTools.Field(typeof(ServerMain),"reusableBuffersDisposalList").SetValue(s,new List<BoxedArray>());
  AccessTools.Field(typeof(ServerMain),"LoadedEntities").SetValue(s,new CachingConcurrentDictionary<long,Entity>());
  s.EntityDespawnSendQueue=new();
  for(int i=0;i<count;i++){var c=New<ConnectedClient>();c.Id=i;c.State=EnumClientState.Playing;c.Socket=new FixtureWire();c.Player=New<ServerPlayer>();AccessTools.Field(typeof(ServerPlayer),"client").SetValue(c.Player,c);s.Clients.TryAdd(i,c);}
  _=s.Clients.Values;return s;
 }
 static void Switch(bool enabled,Harmony h)
 {
  h.UnpatchAll(h.Id);BroadcastOptimizations.ConfigureRaw(enabled);BroadcastOptimizations.ConfigurePacket(enabled);BroadcastOptimizations.ConfigureUdp(enabled);
  if(enabled){h.Patch(BroadcastOptimizations.RawTarget,prefix:new HarmonyMethod(typeof(BroadcastOptimizations),nameof(BroadcastOptimizations.RawPrefix)));h.Patch(BroadcastOptimizations.PacketTarget,prefix:new HarmonyMethod(typeof(BroadcastOptimizations),nameof(BroadcastOptimizations.PacketPrefix)));h.Patch(BroadcastOptimizations.UdpTarget,prefix:new HarmonyMethod(typeof(BroadcastOptimizations),nameof(BroadcastOptimizations.UdpPrefix)));}
 }
 public static void Run()
 {
  Check(BroadcastOptimizations.ValidateContract(out _),"broadcast contracts");Check(EntityLifecycleOptimizations.ValidateContract(out _),"lifecycle contracts");
  var transport=new Harmony("test.nos.broadcast.transport");
  transport.Patch(BroadcastOptimizations.RawSend,prefix:new HarmonyMethod(typeof(Checks),nameof(RawCapture)));
  transport.Patch(BroadcastOptimizations.UdpSend,prefix:new HarmonyMethod(typeof(Checks),nameof(UdpCapture)));
  transport.Patch(BroadcastOptimizations.PreparedSend,prefix:new HarmonyMethod(typeof(Checks),nameof(PreparedCapture)));
  var own=new Harmony("test.nos.broadcast.optimization");
  var raw=BroadcastOptimizations.RawTarget.CreateDelegate<Action<ServerMain,byte[],IServerPlayer[]>>();
  var packet=BroadcastOptimizations.PacketTarget.CreateDelegate<Action<ServerMain,Packet_Server,IServerPlayer[]>>();
  var udp=BroadcastOptimizations.UdpTarget.CreateDelegate<Action<ServerMain,Packet_UdpPacket,IServerPlayer[]>>();
  var fixture=Fixture(8);fixture.Clients[1].State=EnumClientState.Offline;fixture.Clients[2].State=EnumClientState.Queued;
  var skip=New<FilterPlayer>();skip.Filter=()=>4;
  foreach(var excluded in new IServerPlayer[]?[]{null,[],[skip],[null!,skip]})
  {
   string[] Execute(){sent.Clear();FixtureWire.Prepared=0;raw(fixture,[1,2],excluded!);packet(fixture,new Packet_Server{Id=2,Ping=new()},excluded!);udp(fixture,new(),excluded!);return sent.ToArray();}
   Switch(false,own);var expected=Execute();int prepares=FixtureWire.Prepared;Switch(true,own);var actual=Execute();Check(expected.SequenceEqual(actual),"all broadcast overloads preserve state filters, exclusions, payload bytes and send order");Check(FixtureWire.Prepared==prepares,"dummy connections retain stock packet-preparation resets");
  }
  int callbacks=0;var throws=New<FilterPlayer>();throws.Filter=()=>{callbacks++;throw new InvalidOperationException("callback");};
  foreach(bool on in new[]{false,true}){Switch(on,own);callbacks=0;try{raw(fixture,[1],[throws]);throw new Exception("missing throw");}catch(InvalidOperationException e){Check(e.Message=="callback"&&callbacks==1,"callback exception propagated without replay");}}
  Switch(true,own);attempts=0;failAt=2;try{raw(fixture,[1],[]);throw new Exception("missing send error");}catch(InvalidOperationException){Check(attempts==2,"partial sends never retried through stock");}failAt=0;
  object Measure(bool on){Switch(on,own);record=false;var s=Fixture(32);var empty=Array.Empty<IServerPlayer>();var data=new byte[]{1};for(int i=0;i<5000;i++)raw(s,data,empty);long start=GC.GetAllocatedBytesForCurrentThread();var sw=Stopwatch.StartNew();for(int i=0;i<10000;i++)raw(s,data,empty);sw.Stop();record=true;return new{enabled=on,clients=32,bytesPerBroadcast=(GC.GetAllocatedBytesForCurrentThread()-start)/10000d,nsPerBroadcast=sw.Elapsed.TotalNanoseconds/10000};}
  var measurements=new[]{Measure(false),Measure(true),Measure(true),Measure(false)};
  Switch(false,own);transport.UnpatchAll(transport.Id);
  // Actual official lifecycle targets with an empty loaded-entity fixture, then
  // direct scope tests for reference clearing, nested calls, exceptions and caps.
  var owner=RuntimeHelpers.GetUninitializedObject(EntityLifecycleOptimizations.Owner);AccessTools.Field(EntityLifecycleOptimizations.Owner,"server").SetValue(owner,Fixture(0));
  AccessTools.Field(typeof(ServerMain),"FrameProfiler").SetValue(null,new FrameProfilerUtil((Action<string>)(_=>{})));
  void Tick()=>EntityLifecycleOptimizations.TickTarget.Invoke(owner,[.03f]);
  Tick();EntityLifecycleOptimizations.ConfigureTicks(true);EntityLifecycleOptimizations.ConfigureDespawns(true);
  var sends=new Harmony("test.nos.lifecycle.transport");sends.Patch(AccessTools.Method(typeof(ServerMain),"SendPacket",[typeof(int),typeof(Packet_Server)]),prefix:new HarmonyMethod(typeof(Checks),nameof(DespawnCapture)));
  foreach(var target in new[]{EntityLifecycleOptimizations.TickTarget,EntityLifecycleOptimizations.DespawnTarget})own.Patch(target,prefix:new HarmonyMethod(typeof(EntityLifecycleOptimizations),target==EntityLifecycleOptimizations.TickTarget?nameof(EntityLifecycleOptimizations.TickPrefix):nameof(EntityLifecycleOptimizations.DespawnPrefix)),transpiler:new HarmonyMethod(typeof(EntityLifecycleOptimizations),nameof(EntityLifecycleOptimizations.Transpiler)),finalizer:new HarmonyMethod(typeof(EntityLifecycleOptimizations),nameof(EntityLifecycleOptimizations.Finalizer)));
  Tick();Tick();Check(EntityLifecycleOptimizations.Counters.Calls>=2,"official injected cleanup method executes");
  EntityLifecycleOptimizations.TickPrefix(out var scope);var removed=EntityLifecycleOptimizations.Removed();removed.Add(new(new EntityAgent(),new()));
  EntityLifecycleOptimizations.TickPrefix(out var child);Check(!ReferenceEquals(removed,EntityLifecycleOptimizations.Removed()),"nested cleanup owns separate temporary list");EntityLifecycleOptimizations.Finalizer(null,child);

  var live=Fixture(3);foreach(var client in live.Clients.Values)AccessTools.Field(typeof(ConnectedClient),"TrackedEntities").SetValue(client,new HashSet<long>(client.Id==0?[1,2]:client.Id==1?[2]:[]));
  AccessTools.Field(EntityLifecycleOptimizations.Owner,"server").SetValue(owner,live);AccessTools.Field(typeof(ServerMain),"spawnSendQueue").SetValue(null,0);
  string[] Despawn(bool enabled){EntityLifecycleOptimizations.ConfigureDespawns(enabled);live.EntityDespawnSendQueue.Add(new(new EntityAgent{EntityId=1},null!));live.EntityDespawnSendQueue.Add(new(new EntityAgent{EntityId=2},new EntityDespawnData{Reason=EnumDespawnReason.Death}));sent.Clear();EntityLifecycleOptimizations.DespawnTarget.Invoke(owner,null);Check(live.EntityDespawnSendQueue.Count==0,"stock despawn queue drained after successful sends");return sent.ToArray();}
  var stockDespawn=Despawn(false);var reusedDespawn=Despawn(true);Check(stockDespawn.SequenceEqual(reusedDespawn),"actual despawn packets preserve per-client tracked IDs, reasons and damage defaults");sends.UnpatchAll(sends.Id);
  var tick=EntityLifecycleOptimizations.TickTarget.CreateDelegate<Action<ServerSystemEntitySimulation,float>>();
  (double Bytes,double Ns) TickMeasure(bool enabled){EntityLifecycleOptimizations.ConfigureTicks(enabled);for(int i=0;i<5000;i++)tick((ServerSystemEntitySimulation)owner,.03f);long before=GC.GetAllocatedBytesForCurrentThread();var sw=Stopwatch.StartNew();for(int i=0;i<20000;i++)tick((ServerSystemEntitySimulation)owner,.03f);sw.Stop();return((GC.GetAllocatedBytesForCurrentThread()-before)/20000d,sw.Elapsed.TotalNanoseconds/20000);}
  var tickStock=TickMeasure(false);var tickReuse=TickMeasure(true);Check(tickReuse.Bytes<tickStock.Bytes,"actual empty-entity cleanup tick allocation reduced");
  var error=new InvalidOperationException("fixture");Check(ReferenceEquals(error,EntityLifecycleOptimizations.Finalizer(error,scope))&&removed.Count==0,"exception preserved and entity references cleared");
  EntityLifecycleOptimizations.DespawnPrefix(out scope);var ids=EntityLifecycleOptimizations.Ids();ids.AddRange(Enumerable.Range(0,5000).Select(i=>(long)i));long released=EntityLifecycleOptimizations.Counters.Released;EntityLifecycleOptimizations.Finalizer(null,scope);Check(ids.Count==0&&EntityLifecycleOptimizations.Counters.Released==released+1,"oversized cleanup buffers discarded");
  EntityLifecycleOptimizations.ConfigureTicks(false);EntityLifecycleOptimizations.TickPrefix(out scope);Check(scope==null,"disabled cleanup uses stock lists");EntityLifecycleOptimizations.ConfigureDespawns(false);own.UnpatchAll(own.Id);
  Console.WriteLine(JsonSerializer.Serialize(new{checks,measurements,tickCleanup=new{stockBytes=tickStock.Bytes,reusedBytes=tickReuse.Bytes,stockNs=tickStock.Ns,reusedNs=tickReuse.Ns},scope="Official broadcast methods, real serialization, transport intercepted; lifecycle engine fixtures, not player workload or TCP batching."}));
 }
}
