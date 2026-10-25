using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace BlockLogger.Services;

// Only native delivery for our own named group is filtered. PlayerChat handlers,
// moderation, sender echo, commands and native chat logging retain their ordering.
public sealed class ProximityChatService : IProximityChatControl
{
    internal const string Owner = "newfiesserveressentialssuite:proximity";
    private static readonly ConditionalWeakTable<ServerMain,ProximityChatService> instances=new();
    private readonly ICoreServerAPI api;
    private readonly global::System.Func<ChatDisplayConfig> config;
    private readonly Harmony harmony=new("newfies.suite.proximity."+Guid.NewGuid().ToString("N"));
    private ServerMain? server;
    private bool installed,started;
    private long lastError;
    public string Status { get; private set; }="Disabled";
    public int GroupId => Owned()?.Uid??0;
    public ProximityChatService(ICoreServerAPI api,global::System.Func<ChatDisplayConfig> config){this.api=api;this.config=config;}
    private PlayerGroup? Owned()
    {
        var groups=api.Groups?.PlayerGroupsById;
        if(groups==null)return null;
        if(groups.TryGetValue(config().ProximityGroupId,out var known)&&known.OwnerUID==Owner)return known;
        return groups.Values.FirstOrDefault(g=>g.OwnerUID==Owner);
    }
    public bool IsChannel(int id)=>id>0&&id==GroupId;
    public string? Prepare(ChatDisplayConfig candidate)
    {
        try {return PrepareCore(candidate);}
        catch(Exception e){Status="Local chat setup failed ("+e.GetType().Name+").";Report(e);return Status;}
    }
    private string? PrepareCore(ChatDisplayConfig candidate)
    {
        if(!candidate.ProximityEnabled)return null;
        if(api.World is not ServerMain world)return "Proximity chat requires the supported official server runtime.";
        server=world;
        var target=AccessTools.Method(typeof(ServerMain),nameof(ServerMain.SendMessageToGroup));
        var foreign=Harmony.GetPatchInfo(target)?.Owners.Where(x=>x!=harmony.Id).ToArray()??Array.Empty<string>();
        if(foreign.Length>0)return "Group delivery is patched by another mod; proximity chat was not enabled. Owners: "+string.Join(", ",foreign);
        var group=Owned();var collision=api.Groups.GetPlayerGroupByName(candidate.ProximityGroupName);
        if(collision!=null&&collision!=group)return "That group name already belongs to another group. Choose an unused name; existing groups and history are not adopted.";
        if(!installed)
        {
            instances.Remove(world);instances.Add(world,this);
            harmony.Patch(target,prefix:new HarmonyMethod(typeof(ProximityChatService),nameof(DeliveryPrefix)));
            installed=true;
        }
        if(group==null)
        {
            group=new PlayerGroup{Name=candidate.ProximityGroupName,OwnerUID=Owner,JoinPolicy="everyone",Md5Identifier=Guid.NewGuid().ToString("N")};
            api.Groups.AddPlayerGroup(group);world.PlayerDataManager.playerGroupsDirty=true;
        }
        candidate.ProximityGroupId=group.Uid;
        return null;
    }
    public void Start()
    {
        if(started)return;started=true;api.Event.PlayerNowPlaying+=OnPlaying;
        Apply();
    }
    public void Apply()
    {
        var cfg=config();
        if(!cfg.ProximityEnabled)
        {
            // Keep the range gate installed for an existing owned group even
            // while disabled, so third-party sends cannot turn it global.
            if(Owned() is {} owned)
            {
                var protection=new ChatDisplayConfig{ProximityEnabled=true,ProximityGroupName=owned.Name,ProximityGroupId=owned.Uid};
                string? protectionError=Prepare(protection);
                if(protectionError!=null){Status="Disabled; "+protectionError;return;}
            }
            Status="Disabled; the owned local channel rejects player messages.";return;
        }
        int savedId=cfg.ProximityGroupId;
        string? error=Prepare(cfg);
        if(error!=null){Status=error;return;}
        if(savedId!=cfg.ProximityGroupId)SuiteConfigStorage.Store(api,cfg,ChatDisplayConfig.ConfigFile);
        var group=Owned()!;
        if(group.Name!=cfg.ProximityGroupName)
        {
            group.Name=cfg.ProximityGroupName;server!.PlayerDataManager.playerGroupsDirty=true;
            foreach(var p in api.World.AllOnlinePlayers)if(p is IServerPlayer sp&&Member(sp,group.Uid))Join(sp,group,true);
        }
        if(cfg.ProximityAutoJoin)foreach(var p in api.World.AllOnlinePlayers)if(p is IServerPlayer sp)Join(sp,group,false);
        Status="Active — same dimension, "+cfg.ProximityRange.ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+" blocks; no separate persistent chat history.";
    }
    private void OnPlaying(IServerPlayer player)
    {
        if(config().ProximityEnabled&&config().ProximityAutoJoin&&Owned() is {} group&&installed)Join(player,group,false);
    }
    private bool Member(IServerPlayer player,int group)
    {
        var data=api.PlayerData.GetPlayerDataByUid(player.PlayerUID);
        return data!=null&&data.PlayerGroupMemberships.TryGetValue(group,out var m)&&m!=null&&m.Level!=EnumPlayerGroupMemberShip.None;
    }
    private void Join(IServerPlayer player,PlayerGroup group,bool rename)
    {
        var data=server!.PlayerDataManager.GetServerPlayerData(player.PlayerUID);
        if(data==null)return;
        bool existing=data.PlayerGroupMemberShips.TryGetValue(group.Uid,out var membership)&&membership.Level!=EnumPlayerGroupMemberShip.None;
        if(existing&&!rename)return;
        membership=data.JoinGroup(group,existing?membership!.Level:EnumPlayerGroupMemberShip.Member);
        server.PlayerDataManager.playerDataDirty=true;
        if(!group.OnlinePlayers.Any(p=>p.PlayerUID==player.PlayerUID))group.OnlinePlayers.Add(player);
        var packet=new Packet_PlayerGroup{Uid=group.Uid,Name=group.Name,Owneruid=group.OwnerUID,Membership=(int)membership.Level};
        packet.SetChathistory(Array.Empty<Packet_ChatLine>());
        server.SendPacket(player,new Packet_Server{Id=50,PlayerGroup=packet});
    }
    public bool AllowChat(IServerPlayer player,int group)
    {
        if(!IsChannel(group))return true;
        if(config().ProximityEnabled&&installed&&Member(player,group)&&Harmony.GetPatchInfo(AccessTools.Method(typeof(ServerMain),nameof(ServerMain.SendMessageToGroup)))?.Owners.Any(owner=>owner!=harmony.Id)!=true)return true;
        player.SendMessage(group,"Local chat is unavailable or you are not a member of this group.",EnumChatType.CommandError);return false;
    }
    public static bool InRange(double sx,double sy,double sz,int dimension,double x,double y,double z,int otherDimension,double range)
    {
        if(dimension!=otherDimension||!double.IsFinite(range)||range<1||range>1000)return false;
        double dx=sx-x,dy=sy-y,dz=sz-z,dist=dx*dx+dy*dy+dz*dz;
        return double.IsFinite(dist)&&dist<=range*range;
    }
    private static bool DeliveryPrefix(ServerMain __instance,int groupid,string message,EnumChatType chatType,IServerPlayer? exceptPlayer,string? data)
    {
        if(!instances.TryGetValue(__instance,out var service)||!service.IsChannel(groupid)||chatType is not (EnumChatType.OthersMessage or EnumChatType.OwnMessage))return true;
        // Once a channel is ours, a delivery failure must never fall back to a
        // group-wide broadcast. Native sender echo still happens exactly once.
        try {service.Deliver(groupid,message,exceptPlayer,data);}
        catch(Exception e){service.Report(e);}
        return false;
    }
    private void Deliver(int group,string message,IServerPlayer? sender,string? data)
    {
        if(sender==null||!config().ProximityEnabled||!Member(sender,group)||sender.Entity?.Pos is not {} origin)return;
        var target=AccessTools.Method(typeof(ServerMain),nameof(ServerMain.SendMessageToGroup));
        if(Harmony.GetPatchInfo(target)?.Owners.Any(owner=>owner!=harmony.Id)==true){Status="Blocked: another mod changed group delivery.";sender.SendMessage(group,Status,EnumChatType.CommandError);return;}
        double x=origin.X,y=origin.Y,z=origin.Z,radius=config().ProximityRange;int dimension=origin.Dimension;
        foreach(var p in api.World.AllOnlinePlayers)
        {
            if(p is not IServerPlayer recipient||recipient.PlayerUID==sender.PlayerUID||!Member(recipient,group)||recipient.Entity?.Pos is not {} pos)continue;
            if(!InRange(x,y,z,dimension,pos.X,pos.Y,pos.Z,pos.Dimension,radius))continue;
            try {recipient.SendMessage(group,message,EnumChatType.OthersMessage,data);}
            catch(Exception e){Report(e);}
        }
    }
    private void Report(Exception e)
    {
        long now=Environment.TickCount64;if(now-lastError<30000&&lastError!=0)return;lastError=now;
        api.Logger.Warning("[Suite/ProximityChat] Local delivery failed ({0}); no group-wide fallback was sent.",e.GetType().Name);
    }
    public void Dispose()
    {
        if(started)api.Event.PlayerNowPlaying-=OnPlaying;
        if(server!=null)instances.Remove(server);
        harmony.UnpatchAll(harmony.Id);started=false;installed=false;
    }
}
