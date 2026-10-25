using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using BlockLogger.WebMap;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!,dll=Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var root in new[]{Path.GetDirectoryName(dll)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(root,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Fn=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? args)=>Fn(m!,args);
 public static T New<T>(System.Func<MethodInfo,object?[]?,object?> fn)where T:class{var p=Create<T,Proxy>();((Proxy)(object)p).Fn=fn;return p;}
}
public class LocalPlayer:ServerPlayer,IServerPlayer
{
 public LocalPlayer():base(null!,null!){}
 public string Uid="player-a";public EntityPlayer Value=null!;
 public override string PlayerUID=>Uid;
 public new EntityPlayer Entity=>Value;
 public new EnumClientState ConnectionState=>EnumClientState.Playing;
 public new IWorldPlayerData WorldData=>null!;
 public new BlockSelection CurrentBlockSelection=>null!;
}
static class Checks
{
 static int count;
 static void Assert(bool ok,string message){if(!ok)throw new Exception(message);count++;}
 static string Json(WebMapTranslocatorRegistry r)=>JsonSerializer.Serialize(r.Snapshot(),new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
 public static void Run()
 {
  var r=new WebMapTranslocatorRegistry();var secret=new BlockPos(123456789,99,987654321,0);
  r.Observe(1,60,3,true,secret);
  Assert(r.Snapshot().Count==1&&!r.Snapshot()[0].HasTarget,"reading a target does not discover it");
  var json=Json(r);Assert(!json.Contains("123456789")&&!json.Contains("targetX")&&!json.Contains("TargetId"),"undiscovered coordinates absent from JSON, not merely hidden visually");
  r.Observe(secret.X,secret.Y,secret.Z,true,null);
  Assert(r.Snapshot().Single(x=>x.X==1).HasTarget,"another player's independent discovery reveals the link");
  Assert(!r.Snapshot().Single(x=>x.X==secret.X).HasTarget,"no invented return link");
  r.Observe(secret.X,secret.Y,secret.Z,false,null);
  Assert(!r.Snapshot().Single(x=>x.X==1).HasTarget,"broken destination suppresses link");
  r.Observe(secret.X,secret.Y,secret.Z,true,null);
  Assert(r.Snapshot().Single(x=>x.X==1).HasTarget,"repairing an independently known destination restores link");
  r.Remove(secret.X,secret.Y,secret.Z);Assert(!Json(r).Contains("123456789"),"removing destination revokes public target coordinates");
  r.Observe(1,60,3,false,secret);Assert(!r.Snapshot()[0].HasTarget,"broken source cannot advertise target");
  var document=new WebMapTranslocatorDocument{WorldId="world-a",Translocators=r.Export().ToList()};
  var restored=new WebMapTranslocatorRegistry();restored.Restore(document,"world-b");Assert(restored.Snapshot().Count==0,"world mismatch rejected");
  document.SchemaVersion=1;restored.Restore(document,"world-a");Assert(restored.Snapshot().Count==0,"legacy inferred discoveries never imported");
  document.SchemaVersion=2;restored.Restore(document,"world-a");Assert(restored.Snapshot().Count==1,"confirmed discovery survives restart");
  Assert(WebMapTranslocatorRules.Near(0,60,0,.5,60.5,.5,3)&&!WebMapTranslocatorRules.Near(0,60,0,.5,80,.5,3),"discovery uses vertical distance too");
  var bounded=new WebMapTranslocatorRegistry();for(int i=0;i<5100;i++)bounded.Observe(i,60,0,true,null);
  Assert(bounded.Snapshot().Count==WebMapTranslocatorRules.MaximumRecords,"retention capped");
  Tracker();Console.WriteLine($"PASS {count} translocator privacy, native observation, bounded work, persistence and lifecycle checks");
 }
 static void Tracker()
 {
  var root=Path.Combine(Path.GetTempPath(),"nss-translocators-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  var probes=new List<(int X,int Y,int Z)>();var pads=new Dictionary<(int,int,int),BlockEntityStaticTranslocator>();
  var pad=new BlockEntityStaticTranslocator {repairState=4,tpLocation=new BlockPos(100000,70,100000,0)};
  pads[(0,60,0)]=pad;
  object? chunk=null;bool available=true,fail=false;
  var blocks=Proxy.New<IBlockAccessor>((m,a)=>{
   var p=a?.FirstOrDefault() as BlockPos;
   if(m.Name=="GetChunkAtBlockPos"){probes.Add((p!.X,p.Y,p.Z));if(fail)throw new IOException("Injected observation failure");if(!available)return null;return chunk??=DispatchProxy.Create(m.ReturnType,typeof(Proxy));}
   if(m.Name=="GetBlockEntity")return pads.GetValueOrDefault((p!.X,p.Y,p.Z));
   throw new Exception("Unexpected world access / potential chunk loading: "+m.Name);
  });
  var entity=(EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
  AccessTools.Property(typeof(Entity),nameof(Entity.Pos)).SetValue(entity,new EntityPos(.5,60.5,.5));
  var player=(LocalPlayer)RuntimeHelpers.GetUninitializedObject(typeof(LocalPlayer));player.Uid="player-a";player.Value=entity;
  IPlayer[] players=[player];int registrations=0,unregistrations=0;
  var events=Proxy.New<IServerEventAPI>((m,a)=>{if(m.Name=="RegisterGameTickListener"){registrations++;return (long)registrations;}if(m.Name=="UnregisterGameTickListener")unregistrations++;return null;});
  var world=Proxy.New<IServerWorldAccessor>((m,a)=>m.Name switch {"get_SavegameIdentifier"=>"world-a","get_AllOnlinePlayers"=>players,"get_BlockAccessor"=>blocks,_=>throw new Exception(m.Name)});
  var logger=Proxy.New<ILogger>((m,a)=>null);
  var api=Proxy.New<ICoreServerAPI>((m,a)=>m.Name switch {"get_World"=>world,"get_Event"=>events,"get_Logger"=>logger,_=>throw new Exception(m.Name)});
  var tracker=new WebMapTranslocatorTracker(api,root,()=>6);
  void Call(string method,params object?[] args)=>typeof(WebMapTranslocatorTracker).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(tracker,args);
  Assert(SpinWait.SpinUntil(()=>tracker.StorageIssue!="Loading discoveries",3000),"background load completes");
  tracker.Start();
  try
  {
   for(int i=0;i<20;i++){int before=probes.Count;Call("Tick");Assert(probes.Count-before<=WebMapTranslocatorTracker.ReadsPerTick,"hard probe bound per tick");}
   Assert(tracker.Snapshot().Count==1,"stationary nearby pad eventually discovered beyond initial scan slice");
   Assert(!tracker.Snapshot()[0].HasTarget&&!probes.Any(p=>p.X==100000),"target coordinates never cause a remote world query");
   var otherEntity=(EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));AccessTools.Property(typeof(Entity),nameof(Entity.Pos)).SetValue(otherEntity,new EntityPos(1000.5,60.5,.5));
   var other=(LocalPlayer)RuntimeHelpers.GetUninitializedObject(typeof(LocalPlayer));other.Uid="player-b";other.Value=otherEntity;
   pads[(1000,60,0)]=new BlockEntityStaticTranslocator {repairState=4};players=[player,other];
   for(int i=0;i<40;i++)Call("Tick");
   Assert(tracker.Snapshot().Any(p=>p.X==1000),"second player's discovery shared without starving first player");
   var selection=new BlockSelection{Position=new BlockPos(2,60,0,0)};pads[(2,60,0)]=new BlockEntityStaticTranslocator{repairState=0};
   Call("OnPlace",player,0,selection,null);Assert(tracker.Snapshot().Any(p=>p.X==2&&p.State=="broken"),"player-created pad registered");
   pads[(2,60,0)].repairState=4;Call("OnUse",player,selection);Assert(tracker.Snapshot().Any(p=>p.X==2&&p.State=="active"),"repair refreshes marker");
   available=false;Call("OnUse",player,selection);Assert(tracker.Snapshot().Any(p=>p.X==2),"unloaded pad skipped without erasing discovery");available=true;
   fail=true;Call("OnUse",player,selection);Assert(tracker.StorageIssue.Contains("Observation skipped"),"observation failure is diagnosed without escaping player interaction");fail=false;
   tracker.Stop();int stopped=probes.Count;Call("Tick");Call("OnUse",player,selection);Assert(probes.Count==stopped,"disabled tracker performs no world reads");
   tracker.Start();Assert(registrations==2&&unregistrations==1,"enable toggle reuses store with a single active listener");
  }
  finally{tracker.Dispose();}
  Assert(registrations==unregistrations,"shutdown unregisters all tick listeners");
  Assert(Directory.GetFiles(root,"translocators-v2-*.json").Length==1,"one world-scoped discovery file saved");
  using(var again=new WebMapTranslocatorTracker(api,root,()=>6))
  {Assert(SpinWait.SpinUntil(()=>again.StorageIssue!="Loading discoveries",3000),"reload completes");Assert(again.Snapshot().Count==3,"player discoveries reload after shutdown");}
  Directory.Delete(root,true);
 }
}
