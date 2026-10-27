using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using NewfiesAntiCheat.Shared;

namespace BlockLogger.AntiCheat;

// Server-only short allowances; never derived from a client flying/knockback flag.
internal static class ServerImpulseAllowance
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,long> until=new();
    internal static void Mark(string uid,long now){if(until.Count<1024||until.ContainsKey(uid))until[uid]=now+750;}
    internal static bool Active(string uid,long now){if(!until.TryGetValue(uid,out long end))return false;if(now<=end && end-now<=750)return true;until.TryRemove(uid,out _);return false;}
    internal static void Clear()=>until.Clear();
    internal static void Remove(string uid)=>until.TryRemove(uid,out _);
}

internal sealed class ServerActionChecks : IDisposable
{
    private sealed class State
    {
        internal readonly ActionRateWindow Rate=new();
        internal readonly PacketFloodConfirmation Burst=new();
        internal long EntityId,LastAttack,LastLos,LastReport;
        internal int Dimension;
        internal string Tool="";
        internal int Fast,Wall;
    }
    private readonly ICoreServerAPI api;
    private readonly AntiCheatConfig config;
    private readonly Func<PacketGuardTpsSnapshot> health;
    private readonly Action<IServerPlayer,string,AntiCheatSeverity,string,string,string> report;
    private readonly Dictionary<string,State> states=new();
    private readonly PacketIngressBudget lineOfSightBudget=new();
    private Harmony? harmony;
    private bool started;
    internal string CombatStatus=>!started?"Stopped":config.PacketGuardCombatChecksEnabled?"Active":"Disabled by configuration";
    internal string ImpulseStatus {get;private set;}="Disabled by configuration";
    private static ServerActionChecks? current;
    private long lastError;
    private static readonly double[] RayFractions={0.2,0.5,0.8};
    internal ServerActionChecks(ICoreServerAPI api,AntiCheatConfig config,Func<PacketGuardTpsSnapshot> health,Action<IServerPlayer,string,AntiCheatSeverity,string,string,string> report)
    {this.api=api;this.config=config;this.health=health;this.report=report;}
    internal void Start()
    {
        started=true;
        api.Event.OnPlayerInteractEntity+=OnInteract;
        api.Event.PlayerDisconnect+=Remove;
        if(!config.PacketGuardServerImpulseAllowanceEnabled)return;
        try
        {
            harmony=new Harmony("newfies.packetguard.server-impulses");current=this;
            int installed=0;
            foreach(Type type in new[]{typeof(Entity),typeof(EntityAgent),typeof(EntityPlayer)})
            {
                var method=AccessTools.DeclaredMethod(type,nameof(Entity.ReceiveDamage),new[]{typeof(DamageSource),typeof(float)});
                if(method!=null&&method.ReturnType==typeof(bool)){harmony.Patch(method,postfix:new HarmonyMethod(typeof(ServerActionChecks),nameof(AfterDamage)));installed++;}
            }
            ImpulseStatus=installed>0?$"Active ({installed} official method hooks)":"Unavailable: no matching official damage method";
        }
        catch(Exception ex){ImpulseStatus="Unavailable: "+ex.GetType().Name;api.Logger.Warning("[PacketGuard] Server impulse hook unavailable; combat checks continue. {0}",ex.Message);}
    }
    private static void AfterDamage(Entity __instance,DamageSource damageSource,float damage,bool __result)
    {
        try
        {
            if(current?.config.PacketGuardServerImpulseAllowanceEnabled!=true||!__result||!float.IsFinite(damage)||damage<=0||damageSource==null||!float.IsFinite(damageSource.KnockbackStrength)||damageSource.KnockbackStrength<=0)return;
            if(__instance is EntityPlayer ep && ep.Player is IServerPlayer player)ServerImpulseAllowance.Mark(player.PlayerUID,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }catch{/* An observer must not interfere with damage processing. */}
    }
    private void Remove(IServerPlayer player){states.Remove(player.PlayerUID);ServerImpulseAllowance.Remove(player.PlayerUID);}
    private void OnInteract(Entity target,IPlayer byPlayer,ItemSlot slot,Vec3d hit,int mode,ref EnumHandling handling)
    {
        if(mode!=0||!config.PacketGuardCombatChecksEnabled||!config.Enabled||byPlayer is not IServerPlayer player)return;
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            if(player.Entity==null||!player.Entity.Alive||player.WorldData.CurrentGameMode!=EnumGameMode.Survival||player.Entity.MountedOn!=null||(target is EntityAgent mountedTarget && mountedTarget.MountedOn!=null)||
                ServerProtectionExemptions.Has(player.PlayerUID,ServerProtectionScope.Combat,now)||
                (config.PacketGuardExemptAdmins&&(player.HasPrivilege(Privilege.root)||player.HasPrivilege(Privilege.controlserver))))return;
            if(!states.TryGetValue(player.PlayerUID,out var state)){if(states.Count>=1024)return;states[player.PlayerUID]=state=new();}
            var actorPos=EntityRuntimeCompat.GetPos(player.Entity);if(actorPos==null)return;
            if(state.EntityId!=player.Entity.EntityId||state.Dimension!=actorPos.Dimension||now<state.LastAttack)
            {
                state.EntityId=player.Entity.EntityId;state.Dimension=actorPos.Dimension;
                state.Rate.Reset();state.Burst.Observe(now,false);state.Fast=state.Wall=0;state.LastAttack=state.LastLos=0;
            }
            var timing=health();bool reliable=!timing.Enabled||(timing.Ready&&!timing.IsDegraded&&!timing.IsCritical);
            if(!reliable||PacketGuardTrustedState.IsActive(player.PlayerUID,now,PacketGuardTrustScope.Movement)){state.Rate.Reset();state.Burst.Observe(now,false);state.Fast=state.Wall=0;state.LastAttack=now;return;}
            string tool=slot?.Itemstack?.Collectible?.Code?.ToString()??"hand";
            int rate=state.Rate.Observe(now);
            bool rateExceeded=rate>config.PacketGuardCombatMaxRequestsPerSecond;
            bool persistent=rateExceeded&&state.Burst.Observe(now);
            if(!rateExceeded)state.Burst.Observe(now,false);
            long interval=now-state.LastAttack;
            bool configured=config.PacketGuardWeaponMinimumIntervalMs.TryGetValue(tool,out int minimum);
            state.Fast=configured&&state.Tool==tool&&interval>=0&&interval<minimum?Math.Min(100,state.Fast+1):0;
            state.LastAttack=now;state.Tool=tool;
            // No universal weapon cooldown is assumed. Exact code overrides are server policy.
            bool cadence=configured&&state.Fast>=3;
            bool? blocked=null;
            if(now-state.LastLos>3000)state.Wall=0;
            if(now-state.LastLos>=250 && lineOfSightBudget.TryConsume(now,32,32))
            {
                state.LastLos=now;blocked=BlockedFromAllSamples(player.Entity,target);
                state.Wall=blocked==true?Math.Min(10,state.Wall+1):0;
            }
            bool wall=blocked==true&&state.Wall>=3;
            bool deny=!config.PacketGuardNewActionShadowMode&&(persistent||cadence||wall);
            string code=wall?"SERVER_ACTION_COMBAT_WALL":cadence?"SERVER_ACTION_WEAPON_CADENCE":rateExceeded?"SERVER_ACTION_COMBAT_RATE":"";
            if(code.Length>0&&now-state.LastReport>=3000)
            {
                state.LastReport=now;
                report(player,code,AntiCheatSeverity.Moderate,"Server combat observation; review weapon/mod compatibility. New combat checks never automatically kick.",player.PlayerUID,
                    $"shadow={config.PacketGuardNewActionShadowMode};decision={(deny?"denied":"observed")};targetEntity={target.EntityId};tool={tool};requestsPerSecond={rate};intervalMs={interval};configuredMinimumMs={(configured?minimum:0)};rapidSamples={state.Fast};wallObservations={state.Wall};{timing.Evidence}");
            }
            if(deny)handling=EnumHandling.PreventDefault;
        }
        catch(Exception ex){if(now-lastError>=30000){lastError=now;api.Logger.Warning("[PacketGuard] Combat observer skipped incomplete evidence: {0}",ex.Message);}}
    }
    // All three rays must meet known ordinary full-cube terrain. Doors, chiseling,
    // modded block classes, unloaded terrain and oversized ranges invalidate evidence.
    internal bool? BlockedFromAllSamples(Entity player,Entity target)
    {
        var from=EntityRuntimeCompat.GetPos(player);
        var to=EntityRuntimeCompat.GetPos(target);
        var box=target.SelectionBox;
        if(from==null||to==null||from.Dimension!=to.Dimension||box==null||!PacketReachGeometry.ValidBox(box.X1,box.Y1,box.Z1,box.X2,box.Y2,box.Z2))return null;
        double fx=from.X+player.LocalEyePos.X,fy=from.Y+player.LocalEyePos.Y,fz=from.Z+player.LocalEyePos.Z;
        foreach(double fraction in RayFractions)
        {
            double tx=Math.Clamp(fx,to.X+box.X1,to.X+box.X2),ty=to.Y+box.Y1+(box.Y2-box.Y1)*fraction,tz=Math.Clamp(fz,to.Z+box.Z1,to.Z+box.Z2);
            double dx=tx-fx,dy=ty-fy,dz=tz-fz,distance=Math.Sqrt(dx*dx+dy*dy+dz*dz);
            if(!double.IsFinite(distance)||distance>16)return null;
            int steps=Math.Clamp((int)Math.Ceiling(distance/0.25),1,64);bool blocked=false;
            for(int i=1;i<steps;i++)
            {
                double t=i/(double)steps;int x=(int)Math.Floor(fx+dx*t),y=(int)Math.Floor(fy+dy*t),z=(int)Math.Floor(fz+dz*t);
                var accessor=api.World.BlockAccessor;
                if(x<0||y<0||z<0||x>=accessor.MapSizeX||y>=accessor.MapSizeY||z>=accessor.MapSizeZ)return null;
                var pos=new BlockPos(x,y,z,from.Dimension);
                if(accessor.GetChunkAtBlockPos(pos)==null)return null;
                Block block=accessor.GetBlock(pos,BlockLayersAccess.Solid);
                if(block.Id==0||block.IsLiquid())continue;
                if(block.IsMissing||block.Climbable||!string.IsNullOrWhiteSpace(block.EntityClass)||(block.BlockBehaviors?.Length??0)>0||block.GetType()!=typeof(Block)||block.Code?.Path.Contains("door",StringComparison.OrdinalIgnoreCase)==true||block.Code?.Path.Contains("chisel",StringComparison.OrdinalIgnoreCase)==true)return null;
                var shapes=block.GetCollisionBoxes(accessor,pos);
                if(shapes==null||shapes.Length==0)continue;
                if(shapes.Length!=1||shapes[0].X1!=0||shapes[0].Y1!=0||shapes[0].Z1!=0||shapes[0].X2!=1||shapes[0].Y2!=1||shapes[0].Z2!=1)return null;
                blocked=true;break;
            }
            if(!blocked)return false;
        }
        return true;
    }
    public void Dispose(){started=false;ImpulseStatus="Stopped";api.Event.OnPlayerInteractEntity-=OnInteract;api.Event.PlayerDisconnect-=Remove;states.Clear();harmony?.UnpatchAll("newfies.packetguard.server-impulses");if(ReferenceEquals(current,this)){current=null;ServerImpulseAllowance.Clear();}}
}
