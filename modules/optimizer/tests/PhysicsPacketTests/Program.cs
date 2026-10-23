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
using Vintagestory.Server.Systems;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Common.Network.Packets;
using Vintagestory.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var path=Path.Combine(folder,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));}return null;};
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? method,object?[]? args)=>Handler(method!,args);
 public static T Make<T>(System.Func<MethodInfo,object?[]?,object?> handler)where T:class{var x=Create<T,Proxy>();((Proxy)(object)x).Handler=handler;return x;}
}
public sealed class Creature:Entity {}
static class Checks
{
 static int checks;
 static void Assert(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 static readonly List<object> Sent=new();
 static bool FailSend;
 static Action? OnSend;
 static bool CaptureUdp(object[] __args){if(FailSend)throw new InvalidOperationException("send failed");OnSend?.Invoke();Sent.Add(__args[1]);return false;}
 static void Set(object o,string name,object? value)=>AccessTools.Field(o.GetType(),name).SetValue(o,value);
 [MethodImpl(MethodImplOptions.NoInlining)]
 static PhysicsManager Fixture(int count,bool tcp=true)
 {
  var manager=(PhysicsManager)RuntimeHelpers.GetUninitializedObject(typeof(PhysicsManager));
  var player=new EntityPlayer { EntityId=999 };
  var client=(ConnectedClient)RuntimeHelpers.GetUninitializedObject(typeof(ConnectedClient));
  Set(client,"TrackedEntities",new HashSet<long>(Enumerable.Range(1,count).Select(i=>(long)i)));
  var worlddata=(ServerWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ServerWorldPlayerData));Set(worlddata,"Entityplayer",player);Set(client,"worlddata",worlddata);
  Set(client,"threadedTrackedEntities",new[]{Enumerable.Range(1,count).Select(i=>(Entity)new Creature { EntityId=i }).ToList()});
  Set(client,"FallBackToTcp",tcp);
  Set(manager,"ClientList",new List<ConnectedClient>{client});
  Set(manager,"entitiesTagPackets",new ConcurrentDictionary<long,EntityTagPacket>());
  Set(manager,"AnimationsAndTagsChannel",Proxy.Make<IServerNetworkChannel>((m,a)=>{if(m.Name=="SendPacket"){if(FailSend)throw new InvalidOperationException("send failed");Sent.Add(a![0]!);}return null;}));
  Set(manager,"udpNetwork",RuntimeHelpers.GetUninitializedObject(typeof(ServerUdpNetwork)));
  return manager;
 }
 static (Dictionary<long,Packet_EntityPosition> P,Dictionary<long,AnimationPacket> A) Input(int count)=>(Enumerable.Range(1,count).ToDictionary(i=>(long)i,i=>new Packet_EntityPosition()),Enumerable.Range(1,count).ToDictionary(i=>(long)i,i=>new AnimationPacket()));
 [MethodImpl(MethodImplOptions.NoInlining)]
 public static void Run()
 {
  Assert(PhysicsPacketOptimizations.ValidateContract(out _),"contract");
  var capture=new Harmony("nos.physics.test.send");
  var udp=AccessTools.GetDeclaredMethods(typeof(ServerUdpNetwork)).Single(x=>x.Name=="SendPacket_Threadsafe" && x.GetParameters()[1].ParameterType==typeof(Packet_BulkEntityPosition));
  capture.Patch(udp,prefix:new HarmonyMethod(typeof(Checks),nameof(CaptureUdp)));
  var own=new Harmony("nos.physics.test.reuse");
  try
  {
   // Exercise the actual official method with real packet construction; intercept
   // only transport to avoid opening sockets. Compare stock and patched sends.
   var input=Input(12);var manager=Fixture(12);
   manager.SendPositionsAndAnimations(input.P,input.A,0,false);var stock=Sent.ToArray();Sent.Clear();
   own.Patch(PhysicsPacketOptimizations.Target,prefix:new HarmonyMethod(PhysicsPacketOptimizations.PrefixMethod),transpiler:new HarmonyMethod(PhysicsPacketOptimizations.TranspilerMethod),finalizer:new HarmonyMethod(PhysicsPacketOptimizations.FinalizerMethod));
   PhysicsPacketOptimizations.Configure(true);
   manager.SendPositionsAndAnimations(input.P,input.A,0,false);var reuse=Sent.ToArray();Sent.Clear();
   Assert(stock.Length==reuse.Length && reuse.Length==2,"same send count");
   Assert(((Packet_BulkEntityPosition)stock[0]).EntityPositions.SequenceEqual(((Packet_BulkEntityPosition)reuse[0]).EntityPositions),"same positions and order");
   Assert(((BulkAnimationPacket)stock[1]).Packets.SequenceEqual(((BulkAnimationPacket)reuse[1]).Packets),"same animations and order");
   var tags=(ConcurrentDictionary<long,EntityTagPacket>)AccessTools.Field(typeof(PhysicsManager),"entitiesTagPackets").GetValue(manager)!;
   tags[1]=new EntityTagPacket();manager.SendPositionsAndAnimations(input.P,input.A,0,false);Assert(ReferenceEquals(Sent.Last(),tags[1]),"tag packets retain stock identity");Sent.Clear();tags.Clear();
   var batch=Fixture(12,false);batch.SendPositionsAndAnimations(input.P,input.A,0,false);Assert(Sent.OfType<Packet_BulkEntityPosition>().Select(x=>x.EntityPositions.Length).SequenceEqual(new[]{8,4}),"UDP batches retain stock eight-packet split");Sent.Clear();
   bool nestedOnce=false;OnSend=()=>{if(nestedOnce)return;nestedOnce=true;manager.SendPositionsAndAnimations(new(),new(),0,false);};manager.SendPositionsAndAnimations(input.P,input.A,0,false);OnSend=null;Assert(nestedOnce && Sent.Count==2,"actual recursive send keeps outer payload");Sent.Clear();
   var ownedArray=((BulkAnimationPacket)reuse[1]).Packets.ToArray();
   manager.SendPositionsAndAnimations(input.P,input.A,0,true);Sent.Clear();
   Assert(((BulkAnimationPacket)reuse[1]).Packets.SequenceEqual(ownedArray),"sent arrays remain owned by packets after buffer reuse");
   var before=PhysicsPacketOptimizations.Counters.Created;
   for(int i=0;i<10;i++){manager.SendPositionsAndAnimations(input.P,input.A,0,false);Sent.Clear();}
   Assert(PhysicsPacketOptimizations.Counters.Created==before,"steady reuse without new buffer sets");
   FailSend=true;bool failed=false;try{manager.SendPositionsAndAnimations(input.P,input.A,0,false);}catch(InvalidOperationException){failed=true;}finally{FailSend=false;}
   Assert(failed,"send exception preserved");
   var ex=new InvalidOperationException();PhysicsPacketOptimizations.Prefix(out var outer);var list=PhysicsPacketOptimizations.Positions();list.Add(input.P[1]);PhysicsPacketOptimizations.Prefix(out var nested);
   Assert(!ReferenceEquals(list,PhysicsPacketOptimizations.Positions()),"nested send has separate lists");
   Assert(ReferenceEquals(ex,PhysicsPacketOptimizations.Finalizer(ex,nested)),"finalizer returns original exception");
   Assert(ReferenceEquals(list,PhysicsPacketOptimizations.Positions()) && list.Count==1,"nested return restores outer state");
   PhysicsPacketOptimizations.Configure(false);PhysicsPacketOptimizations.Prefix(out nested);Assert(!ReferenceEquals(list,PhysicsPacketOptimizations.Positions()),"disable during nested send keeps separate storage");PhysicsPacketOptimizations.Finalizer(null,nested);PhysicsPacketOptimizations.Finalizer(null,outer);PhysicsPacketOptimizations.Configure(true);Assert(list.Count==0,"references cleared on return");
   PhysicsPacketOptimizations.Prefix(out outer);list=PhysicsPacketOptimizations.Positions();list.Capacity=8192;before=PhysicsPacketOptimizations.Counters.Oversized;PhysicsPacketOptimizations.Finalizer(null,outer);
   Assert(PhysicsPacketOptimizations.Counters.Oversized==before+1,"oversized buffers discarded");
   var parallel=new ConcurrentBag<object>();Parallel.For(0,16,_=>{PhysicsPacketOptimizations.Prefix(out var state);parallel.Add(PhysicsPacketOptimizations.Positions());PhysicsPacketOptimizations.Finalizer(null,state);});
   Assert(parallel.Count==16,"parallel leases complete");
   PhysicsPacketOptimizations.Configure(false);before=PhysicsPacketOptimizations.Counters.Calls;manager.SendPositionsAndAnimations(input.P,input.A,0,false);Sent.Clear();Assert(PhysicsPacketOptimizations.Counters.Calls==before,"independent disable retains stock allocations");
   // Alternate true unpatched and patched official methods; output arrays and
   // proxy transport costs remain in both modes. No real networking or players.
   var empty=Fixture(0);var none=Input(0);const int n=30000;
   object Measure(bool enabled,int entities){own.UnpatchAll(own.Id);if(enabled)own.Patch(PhysicsPacketOptimizations.Target,prefix:new HarmonyMethod(PhysicsPacketOptimizations.PrefixMethod),transpiler:new HarmonyMethod(PhysicsPacketOptimizations.TranspilerMethod),finalizer:new HarmonyMethod(PhysicsPacketOptimizations.FinalizerMethod));PhysicsPacketOptimizations.Configure(enabled);var fixture=entities==0?empty:Fixture(entities);var data=entities==0?none:Input(entities);for(int i=0;i<5000;i++){fixture.SendPositionsAndAnimations(data.P,data.A,0,false);Sent.Clear();}var watch=new Stopwatch();long start=GC.GetAllocatedBytesForCurrentThread();watch.Start();for(int i=0;i<n;i++){fixture.SendPositionsAndAnimations(data.P,data.A,0,false);Sent.Clear();}watch.Stop();return new{enabled,entities,bytesPerCall=(GC.GetAllocatedBytesForCurrentThread()-start)/(double)n,nsPerCall=watch.Elapsed.TotalNanoseconds/n};}
   Console.WriteLine(JsonSerializer.Serialize(new{checks,trials=new[]{Measure(false,0),Measure(true,0),Measure(false,0),Measure(true,0),Measure(false,0),Measure(true,0),Measure(false,64),Measure(true,64),Measure(false,64),Measure(true,64),Measure(false,64),Measure(true,64)},scope="Actual official physics send loop, synthetic one-client fixtures, intercepted transport. No real-player/TPS claim."}));
  }
  finally{PhysicsPacketOptimizations.Configure(false);own.UnpatchAll(own.Id);capture.UnpatchAll(capture.Id);}
 }
}
