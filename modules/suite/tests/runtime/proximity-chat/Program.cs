using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BlockLogger.Services;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.Server;
var game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;var suite=Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var d in new[]{Path.GetDirectoryName(suite)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(d,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Fn=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? a)=>Fn(m!,a);
 public static T Create<T>(System.Func<MethodInfo,object?[]?,object?> fn)where T:class{var p=Create<T,Proxy>();((Proxy)(object)p).Fn=fn;return p;}
}
public class TestEntity:Entity {public override bool IsCreature=>true;}
public class LocalPlayer:ServerPlayer,IServerPlayer
{
 public LocalPlayer():base(null!,null!){}
 public string Uid="";public EntityPlayer Value=null!;public Action<string,string,string> OnSend=null!;
 public override string PlayerUID=>Uid;
 public new EntityPlayer Entity=>Value;
 public new int ClientId=>Uid.GetHashCode();
 public new void SendMessage(int groupId,string message,EnumChatType type,string data=null!)=>OnSend(Uid,message,data??"");
}
public class Groups: IGroupManager
{
 public Dictionary<int,PlayerGroup> PlayerGroupsById{get;}=new();
 public PlayerGroup GetPlayerGroupByName(string name)=>PlayerGroupsById.Values.FirstOrDefault(g=>g.Name.Equals(name,StringComparison.OrdinalIgnoreCase))!;
 public void AddPlayerGroup(PlayerGroup group){group.Uid=100+PlayerGroupsById.Count;PlayerGroupsById.Add(group.Uid,group);}
 public void RemovePlayerGroup(PlayerGroup group)=>PlayerGroupsById.Remove(group.Uid);
}
static class Checks
{
 static int checks;static IPlayer[] players=[];static readonly List<(string Uid,string Message,string Data)> delivered=[];static int groupPackets;
 static void Assert(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
 static T New<T>()where T:class=>(T)RuntimeHelpers.GetUninitializedObject(typeof(T));
 static bool AllPlayers(ref IPlayer[] __result){__result=players;return false;}
 static bool Packet()=>false;
 static bool GroupPacket(){groupPackets++;return false;}
 static bool Foreign()=>true;
 static IServerPlayer Player(string uid,double x,double y,double z,int dimension)
 {
  var e=New<EntityPlayer>();AccessTools.Property(typeof(Entity),nameof(Entity.Pos)).SetValue(e,new EntityPos());
  e.Pos.SetPos(x,y,z);e.Pos.Dimension=dimension;
  var p=New<LocalPlayer>();p.Uid=uid;p.Value=e;p.OnSend=(u,m,d)=>delivered.Add((u,m,d));return p;
 }
 static object? Record(string uid,object?[] a){delivered.Add((uid,(string)a[1]!,a.Length>3?(string?)a[3]??"":""));return null;}
 static bool Route(ServerMain world,int group,IServerPlayer? sender,string message="<strong>A:</strong> hello",string data="payload")=>(bool)AccessTools.Method(typeof(ProximityChatService),"DeliveryPrefix").Invoke(null,[world,group,message,EnumChatType.OthersMessage,sender,data])!;
 public static void Run()
 {
  var transport=new Harmony("test.proximity.transport");transport.Patch(AccessTools.PropertyGetter(typeof(ServerMain),nameof(ServerMain.AllOnlinePlayers)),prefix:new HarmonyMethod(typeof(Checks),nameof(AllPlayers)));
  transport.Patch(AccessTools.Method(typeof(ServerMain),nameof(ServerMain.SendPacket),[typeof(IServerPlayer),typeof(Packet_Server)]),prefix:new HarmonyMethod(typeof(Checks),nameof(GroupPacket)));
  var world=New<ServerMain>();world.PlayerDataManager=New<PlayerDataManager>();world.PlayerDataManager.PlayerDataByUid=new();var groups=new Groups();world.PlayerDataManager.PlayerGroupsById=groups.PlayerGroupsById;
  var cfg=new ChatDisplayConfig{ProximityEnabled=false,ProximityAutoJoin=false,ProximityRange=100};
  var api=Proxy.Create<ICoreServerAPI>((m,a)=>m.Name switch {"get_World"=>world,"get_Groups"=>groups,"get_PlayerData"=>Proxy.Create<IPlayerDataManager>((method,args)=>method.Name=="GetPlayerDataByUid"?world.PlayerDataManager.GetServerPlayerData((string)args![0]!):null),"get_Event"=>Proxy.Create<IServerEventAPI>((_,_)=>null),"get_Logger"=>Proxy.Create<ILogger>((_,_)=>null),_=>null});
  using var service=new ProximityChatService(api,()=>cfg);service.Start();Assert(groups.PlayerGroupsById.Count==0,"disabled installation creates no group");
  cfg.ProximityEnabled=true;Assert(service.Prepare(cfg)==null,"owned group preparation");service.Apply();int id=service.GroupId;Assert(id>0&&groups.PlayerGroupsById[id].ChatHistory.Count==0,"fresh owned group with empty history");
  players=[Player("sender",0,0,0,0),Player("near",30,0,0,0),Player("edge",100,0,0,0),Player("far",100.1,0,0,0),Player("above",0,100.1,0,0),Player("dimension",0,0,0,1),Player("nonmember",0,0,0,0)];
  foreach(var p in players){var d=new ServerPlayerData{PlayerUID=p.PlayerUID};world.PlayerDataManager.PlayerDataByUid[p.PlayerUID]=d;if(p.PlayerUID!="nonmember")d.JoinGroup(groups.PlayerGroupsById[id],EnumPlayerGroupMemberShip.Member);}
  delivered.Clear();Assert(!Route(world,id,(IServerPlayer)players[0]),"owned delivery suppresses native global broadcast");Assert(delivered.Select(x=>x.Uid).SequenceEqual(new[]{"near","edge"}),"inclusive 3D range, dimension, membership and sender exclusion");Assert(delivered.All(x=>x.Data=="payload"&&x.Message.Contains("<strong>")),"native metadata and completed formatting retained");
  delivered.Clear();world.SendMessageToGroup(id,"native route",EnumChatType.OthersMessage,(IServerPlayer)players[0],"native-data");Assert(delivered.Select(x=>x.Uid).SequenceEqual(new[]{"near","edge"})&&delivered.All(x=>x.Data=="native-data"),"actual patched native broadcast routes only nearby members");
  delivered.Clear();Assert(Route(world,0,(IServerPlayer)players[0])&&delivered.Count==0,"General retains original native delivery");Assert(Route(world,id+1,(IServerPlayer)players[0]),"foreign groups untouched");
  Assert(!Route(world,id,null)&&delivered.Count==0,"unknown sender never becomes global");Assert(!Route(world,id,(IServerPlayer)players[^1])&&delivered.Count==0,"forged group sender denied");
  cfg.ProximityEnabled=false;service.Apply();Assert(!Route(world,id,(IServerPlayer)players[0])&&delivered.Count==0,"disabled group remains fail-closed");Assert(!service.AllowChat((IServerPlayer)players[0],id),"disabled sender gets private feedback");
  cfg.ProximityEnabled=true;cfg.ProximityAutoJoin=true;delivered.Clear();service.Apply();Assert(world.PlayerDataManager.GetServerPlayerData("nonmember").PlayerGroupMemberShips.ContainsKey(id)&&groupPackets>0,"auto-join records native membership and sends group packet");
  cfg.ProximityGroupName="Nearby";service.Apply();Assert(service.GroupId==id&&groups.GetPlayerGroupByName("Nearby").Uid==id,"owned rename keeps stable group identity");
  groups.AddPlayerGroup(new PlayerGroup{Name="Existing",OwnerUID="someone-else"});var collision=new ChatDisplayConfig{ProximityEnabled=true,ProximityGroupName="Existing"};Assert(service.Prepare(collision)!=null,"existing foreign group not adopted");
  var foreign=new Harmony("test.proximity.foreign");foreign.Patch(AccessTools.Method(typeof(ServerMain),nameof(ServerMain.SendMessageToGroup)),prefix:new HarmonyMethod(typeof(Checks),nameof(Foreign)));delivered.Clear();Assert(!service.AllowChat((IServerPlayer)players[0],id),"late foreign patch blocks native player-chat delivery before broadcast");Route(world,id,(IServerPlayer)players[0]);Assert(!delivered.Any(x=>x.Uid!="sender"),"late foreign delivery patch fails closed");foreign.UnpatchAll(foreign.Id);
  Assert(groups.PlayerGroupsById[id].ChatHistory.Count==0,"messages never added to group history");
  Assert(!ProximityChatService.InRange(double.NaN,0,0,0,0,0,0,0,100)&&!ProximityChatService.InRange(0,0,0,0,0,0,0,0,double.NaN),"invalid coordinates/range deny delivery");
  service.Dispose();transport.UnpatchAll(transport.Id);Console.WriteLine($"PASS {checks} official-API proximity routing, ownership, membership, history and privacy checks; transport intercepted, no authenticated clients.");
 }
}
