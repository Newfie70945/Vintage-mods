using System.Collections;
using System.Reflection;
using HarmonyLib;
using NewfiesServerEssentials.Discord;
using NewfiesServerEssentials.Discord.Config;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.Server;

public class EventProbe : ModSystem
{
    static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    static object Get(object o,string name)=>o.GetType().GetField(name,Fields)!.GetValue(o)!;
    static void Set(object o,string name,object? value)=>o.GetType().GetField(name,Fields)!.SetValue(o,value);
    static object? Call(object o,string name,params object?[] args)=>o.GetType().GetMethod(name,Fields)!.Invoke(o,args);
    public override bool ShouldLoad(EnumAppSide side)=>side==EnumAppSide.Server;
    public override void StartServerSide(ICoreServerAPI api)=>api.ChatCommands.Create("nsediscordeventprobe").RequiresPrivilege("controlserver").HandleWith(_=>Run(api));
    TextCommandResult Run(ICoreServerAPI api)
    {
        int checks=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;}
        var mod=api.ModLoader.GetModSystem<DiscordIntegrationModSystem>();
        var cfg=(DiscordIntegrationConfig)Get(mod,"config");var bridge=Get(mod,"discord");var relay=Get(mod,"relay");
        if(cfg.Enabled||Get(bridge,"cts")!=null||Get(bridge,"workerTask")!=null)throw new Exception("Probe requires disabled Discord/no worker");
        var death=cfg.Events.PlayerDeath;var storm=cfg.Events.TemporalStorm;var chatChannel=cfg.Chat.ChannelId;
        var system=api.ModLoader.GetModSystem<SystemTemporalStability>();var data=system.StormData;
        var oldActive=data.nowStormActive;var oldNotice=data.stormDayNotify;var oldStrength=data.nextStormStrength;
        var oldStormMode=api.World.Config.GetString("temporalStorms","off");var oldDisable=api.World.Config.GetBool("disableDeathMessages",false);
        using var lifetime=new CancellationTokenSource();
        var queue=Get(bridge,"queue");
        object[] Messages()=>((IEnumerable)queue).Cast<object>().ToArray();
        void Clear(){Call(queue,"Clear");Set(bridge,"queuedCount",0);while(((SemaphoreSlim)Get(bridge,"queueSignal")).Wait(0)){} }
        string Message()=> (string)Messages().Last().GetType().GetProperty("Message")!.GetValue(Messages().Last())!;
        try
        {
            Check(death.Format=="☠️ **{message}**","stock template migration");
            Check(storm!=null&&!storm.Enabled,"null storm route normalization/default off");
            Check((bool)Get(relay,"nativeDeathCapture"),"native hook attached");
            cfg.Enabled=true;cfg.Chat.ChannelId=42;
            cfg.Events.PlayerDeath=new DiscordIntegrationConfig.RoutedEvent(true,0,"☠️ **{message}**");cfg.Events.TemporalStorm=new DiscordIntegrationConfig.RoutedEvent(true,84,"{phase}|{strength}|{message}");Set(bridge,"cts",lifetime);
            api.World.Config.SetBool("disableDeathMessages",false);
            var fake=FakePlayer.Create();
            var server=(ServerMain)api.World;
            foreach(var text in new[]{"Player ProbePlayer forgot to eat.","Player ProbePlayer had a too close encounter with a wolf.","Player ProbePlayer received a shocking revelation."})
            {
                Clear();Call(relay,"OnPlayerDeath",fake,new DamageSource{Type=EnumDamageType.Hunger});
                Check(Messages().Length==0,"event waits for native message");
                server.joinLeaveDeathMessage(text,EnumChatType.Notification,fake);
                Check(Messages().Length==1&&Message()=="☠️ **"+text+"**","native text captured once");
                Check((ulong)Messages()[0].GetType().GetProperty("ChannelId")!.GetValue(Messages()[0])! ==42,"channel inherits chat");
            }
            Clear();server.joinLeaveDeathMessage("join",EnumChatType.JoinLeave,fake);Check(Messages().Length==0,"join ignored");
            cfg.Events.PlayerDeath.Enabled=false;server.joinLeaveDeathMessage("death",EnumChatType.Notification,fake);Check(Messages().Length==0,"disabled route ignored");cfg.Events.PlayerDeath.Enabled=true;
            api.World.Config.SetBool("disableDeathMessages",true);Call(relay,"OnPlayerDeath",fake,new DamageSource{Type=EnumDamageType.Hunger});Check(Messages().Length==1&&Message().Contains("starved"),"native-disabled fallback");Clear();
            api.World.Config.SetString("temporalStorms","often");Call(Get(relay,"storms"),"Reset");
            data.nowStormActive=false;data.stormDayNotify=99;Call(relay,"PollStorms");Check(Messages().Length==0,"storm baseline");
            foreach(var step in new[]{(false,1,"approaching"),(false,0,"imminent"),(true,0,"started"),(true,-1,"waning"),(false,99,"ended")})
            {
                Clear();data.nowStormActive=step.Item1;data.stormDayNotify=step.Item2;Call(relay,"PollStorms");Check(Messages().Length==1&&Message().StartsWith(step.Item3+"|"),"storm "+step.Item3);Call(relay,"PollStorms");Check(Messages().Length==1,"storm duplicate suppression");
            }
            Clear();cfg.Events.TemporalStorm.Enabled=false;data.stormDayNotify=1;Call(relay,"PollStorms");cfg.Events.TemporalStorm.Enabled=true;Call(relay,"PollStorms");Check(Messages().Length==0,"disabled phase not replayed");
            Call(relay,"Unregister");Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(ServerMain),"joinLeaveDeathMessage"))?.Owners.Contains("newfies.discord.native-death-relay") != true,"hook detached");
            Call(relay,"Register");Call(relay,"Register");Check((bool)Get(relay,"nativeDeathCapture"),"idempotent re-register");
            api.Logger.Notification("NSE_DISCORD_EVENT_PROBE PASS checks={0}",checks);
            return TextCommandResult.Success("Offline event probe passed "+checks);
        }
        catch(Exception e){api.Logger.Error("NSE_DISCORD_EVENT_PROBE FAIL {0}",e.ToString());return TextCommandResult.Error(e.Message);}
        finally
        {
            cfg.Enabled=false;cfg.Chat.ChannelId=chatChannel;cfg.Events.PlayerDeath=death;cfg.Events.TemporalStorm=storm!;Set(bridge,"cts",null);Clear();
            data.nowStormActive=oldActive;data.stormDayNotify=oldNotice;data.nextStormStrength=oldStrength;api.World.Config.SetString("temporalStorms",oldStormMode);api.World.Config.SetBool("disableDeathMessages",oldDisable);Call(Get(relay,"storms"),"Reset");
        }
    }
}
public class FakePlayer : ServerPlayer, IServerPlayer
{
    public FakePlayer():base(null!,null!){}
    public static FakePlayer Create()=>(FakePlayer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(FakePlayer));
    public new string PlayerName=>"ProbePlayer";
    public override string PlayerUID=>"discord-event-probe";
    public new PlayerGroupMembership[] Groups=>[];
    public new EntityPlayer Entity=>null!;
}
