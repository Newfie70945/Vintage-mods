using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.Server;

namespace NewfiesServerEssentials.Discord.Relay;

public sealed partial class GameRelayService
{
    private const string DeathPatchId="newfies.discord.native-death-relay";
    private static GameRelayService? deathOwner;
    private Harmony? deathHarmony;
    private bool nativeDeathCapture,eventWarning;
    private long stormListener=-1;
    private readonly StormTransitions storms=new();
    private void RegisterGameEvents()
    {
        try
        {
            var method=AccessTools.Method(typeof(ServerMain),"joinLeaveDeathMessage",[typeof(string),typeof(EnumChatType),typeof(IServerPlayer)])??throw new MissingMethodException("Native death announcement hook unavailable.");
            if(deathOwner!=null&&deathOwner!=this)throw new InvalidOperationException("A Discord death relay is already registered.");
            deathHarmony=new(DeathPatchId);deathOwner=this;
            deathHarmony.Patch(method,postfix:new HarmonyMethod(typeof(GameRelayService),nameof(AfterNativeDeath)){priority=Priority.Last});nativeDeathCapture=true;
        }
        catch(Exception error){nativeDeathCapture=false;sapi.Logger.Warning("[NSE Discord] Native death-message capture unavailable; cause-aware event fallback enabled: {0}",error.Message);}
        stormListener=sapi.Event.RegisterGameTickListener(_=>PollStorms(),2000);
    }
    private void UnregisterGameEvents()
    {
        nativeDeathCapture=false;
        if(deathOwner==this)
        {
            deathOwner=null;
            try{deathHarmony?.UnpatchAll(DeathPatchId);}catch(Exception error){WarnGameEvent(error);}
        }
        deathHarmony=null;
        try{if(stormListener>=0)sapi.Event.UnregisterGameTickListener(stormListener);}
        catch(Exception error){WarnGameEvent(error);}
        finally{stormListener=-1;storms.Reset();}
    }
    private static void AfterNativeDeath(ServerMain __instance,string message,EnumChatType chatType,IServerPlayer player)
    {
        var owner=deathOwner;
        if(owner==null||!owner.active||!ReferenceEquals(owner.sapi.World,__instance)||chatType!=EnumChatType.Notification||player==null)return;
        // Never allow a relay failure to break the original game announcement.
        try{owner.RelayDeath(player,player.Entity?.DeathReason,message);}
        catch(Exception error){owner.WarnGameEvent(error);}
    }
    private void RelayDeath(IServerPlayer player,DamageSource? damage,string? nativeMessage)
    {
        try
        {
            var cfg=getConfig();if(!active||!cfg.Enabled||!cfg.Events.PlayerDeath.Enabled)return;
            string cause=damage?.Type.ToString()??"Unknown",source=damage?.Source.ToString()??"Unknown";
            var entity=damage?.GetCauseEntity();string killer=entity is EntityPlayer ep?sapi.World.PlayerByUid(ep.PlayerUID)?.PlayerName??ep.PlayerUID:entity?.Code?.ToString()??"";
            var values=new Dictionary<string,string>{{"player",player.PlayerName},{"cause",cause},{"source",source},{"killer",killer},{"message",nativeMessage??GameEventText.Fallback(player.PlayerName,cause,source,killer)}};
            var route=cfg.Events.PlayerDeath;RelayEvent(route,GameEventText.Format(route.Format??"{message}",values));
        }
        catch(Exception error){WarnGameEvent(error);}
    }
    private void PollStorms()
    {
        if(!active)return;
        try
        {
            var system=sapi.ModLoader.GetModSystem<SystemTemporalStability>();
            if(system==null||sapi.World.Config.GetString("temporalStorms","off")=="off"){storms.Reset();return;}
            var data=system.StormData;if(data==null)return;
            var changes=storms.Observe(new(data.nowStormActive,data.stormDayNotify,data.nextStormStrength.ToString()));
            var cfg=getConfig();if(!cfg.Enabled||!cfg.Events.TemporalStorm.Enabled)return;
            foreach(var change in changes)
            {
                string strength=change.Strength.ToLowerInvariant();
                string text=change.Phase switch
                {
                    "approaching"=>Lang.Get("A "+strength+" temporal storm is approaching"),
                    "imminent"=>Lang.Get("A "+strength+" temporal storm is imminent"),
                    "waning"=>Lang.Get("The temporal storm seems to be waning"),
                    "started"=>Lang.Get("A {0} temporal storm has begun.",strength),
                    _=>Lang.Get("The temporal storm has ended.")
                };
                var route=cfg.Events.TemporalStorm;
                RelayEvent(route,GameEventText.Format(route.Format??"{message}",new Dictionary<string,string>{{"message",text},{"phase",change.Phase},{"strength",change.Strength}}));
            }
        }
        catch(Exception error){WarnGameEvent(error);}
    }
    private void WarnGameEvent(Exception error)
    {if(eventWarning)return;eventWarning=true;sapi.Logger.Warning("[NSE Discord] Game-event relay failed; game handling continues. Check module compatibility: {0}",error.Message);}
}
