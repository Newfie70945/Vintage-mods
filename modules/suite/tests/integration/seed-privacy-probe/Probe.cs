using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Vintagestory.Server;
[assembly:ModInfo("Seed privacy native integration fixture","nssseedprivacyprobe",Version="0.0.0",Side="Server")]
public sealed class Probe:ModSystem
{
 public override bool ShouldLoad(EnumAppSide side)=>side==EnumAppSide.Server;
 public override void StartServerSide(ICoreServerAPI api)
 {
  api.ChatCommands.Create("seedprivacycheck").RequiresPrivilege(Privilege.controlserver).HandleWith(_=>{
   int checks=0;void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
   try{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    var server=(ServerMain)api.World;int savedSeed=api.World.Seed;
    var privacy=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("BlockLogger.SeedPrivacy.SeedPrivacySystem")).First(t=>t!=null)!;
    var service=privacy.GetField("current",F)!.GetValue(null);Check(service!=null,"privacy service active");
    var cfg=privacy.GetField("config",F)!.GetValue(service)!;int publicSeed=(int)cfg.GetType().GetProperty("PublicSeed")!.GetValue(cfg)!;
    Packet_Server Wire(Packet_Server packet){var serialize=typeof(ServerMain).GetMethod("Serialize_",F)!;int n=(int)serialize.Invoke(server,[packet])!;var box=typeof(ServerMain).GetField("reusableBuffer",F)!.GetValue(null)!;var bytes=(byte[])typeof(BoxedArray).GetField("buffer",F)!.GetValue(box)!;var received=new Packet_Server();Packet_ServerSerializer.DeserializeBuffer(bytes,n,received);return received;}
    var native=(Packet_Server)typeof(ServerMain).GetMethod("CreatePacketIdentification",F)!.Invoke(server,[false])!;
    Check(native.Identification.Seed==savedSeed,"native handshake begins with real saved seed");
    var sent=Wire(native);Check(sent.Identification.Seed==publicSeed&&publicSeed!=savedSeed,"native serialized handshake exposes only public seed");Check(native.Identification.Seed==savedSeed,"canonical handshake remains unchanged");
    var state=new WeatherState{RegionX=-3,RegionZ=4,Weight=.6f,TransitionDelay=18,LcgWorldSeed=savedSeed,LcgMapGenSeed=long.MinValue+3,LcgCurrentSeed=long.MaxValue-3,NewPattern=new(){Index=1}};
    var raw=SerializerUtil.Serialize(state);int channel=(int)privacy.GetField("weatherChannel",F)!.GetValue(service)!,message=(int)privacy.GetField("weatherMessage",F)!.GetValue(service)!;
    var custom=new Packet_Server{Id=55,CustomPacket=new(){ChannelId=channel,MessageId=message,Data=raw}};
    var visible=SerializerUtil.Deserialize<WeatherState>(Wire(custom).CustomPacket.Data);var rng=new LCGRandom(publicSeed);rng.InitPositionSeed(-3/3,4/3);
    Check(visible.LcgWorldSeed==publicSeed&&visible.LcgMapGenSeed==rng.mapGenSeed&&visible.LcgCurrentSeed==rng.currentSeed,"weather wire RNG values sanitized");Check(visible.Weight==state.Weight&&visible.TransitionDelay==18&&visible.NewPattern.Index==1,"weather state unchanged");Check(state.LcgWorldSeed==savedSeed&&SerializerUtil.Serialize(state).SequenceEqual(raw),"server weather object and serialized save bytes unchanged");
    var dictionary=new Dictionary<string,byte[]>{{"weatherState",raw},{"fixture.other",[9,8,7]}};var regionBytes=SerializerUtil.Serialize(dictionary);var region=new Packet_Server{Id=42,MapRegion=new(){RegionX=-3,RegionZ=4,Moddata=regionBytes}};
    var received=SerializerUtil.Deserialize<Dictionary<string,byte[]>>(Wire(region).MapRegion.Moddata);
    Check(SerializerUtil.Deserialize<WeatherState>(received["weatherState"]).LcgWorldSeed==publicSeed,"map-region weather seed sanitized");Check(received["fixture.other"].SequenceEqual(dictionary["fixture.other"]),"unrelated region data retained");Check(SerializerUtil.Serialize(dictionary).SequenceEqual(regionBytes)&&region.MapRegion.Moddata.SequenceEqual(regionBytes),"canonical region bytes remain unchanged");
    Check(api.World.Seed==savedSeed,"actual server world seed unchanged after all native wire sends");
    api.Logger.Notification("NSS_SEED_PRIVACY_PASS checks={0}; native identification/weather/region wire masked; server seed and save payloads unchanged.",checks);
    return TextCommandResult.Success("NSS_SEED_PRIVACY_PASS");
   }catch(Exception e){api.Logger.Error("NSS_SEED_PRIVACY_FAIL "+e.GetType().Name+": "+e.Message);return TextCommandResult.Error("NSS_SEED_PRIVACY_FAIL");}
  });
 }
}
