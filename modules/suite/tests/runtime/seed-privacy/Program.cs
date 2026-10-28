using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Vintagestory.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!,dll=Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!;
AssemblyLoadContext.Default.Resolving+=(_,n)=>{foreach(var dir in new[]{Path.GetDirectoryName(dll)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(dir,n.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
Checks.Run(dll);
static class Checks {
 static int checks,sends;
 static readonly BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 public static bool Sink()=>++sends<0;
 public static void Run(string dll){
  var suite=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);RuntimeHelpers.RunModuleConstructor(suite.ManifestModule.ModuleHandle);
  var type=suite.GetType("BlockLogger.SeedPrivacy.SeedPrivacySystem",true)!;var configType=suite.GetType("BlockLogger.SeedPrivacy.SeedPrivacyConfig",true)!;
  object service=Activator.CreateInstance(type)!;object config=Activator.CreateInstance(configType)!;Check(!(bool)configType.GetProperty("Enabled")!.GetValue(config)!,"default is disabled");
  type.GetField("config",F)!.SetValue(service,config);type.GetField("weatherChannel",F)!.SetValue(service,7);type.GetField("weatherMessage",F)!.SetValue(service,2);
  var sanitize=type.GetMethod("Sanitize",F)!;
  Packet_Server Apply(Packet_Server packet)=>(Packet_Server)sanitize.Invoke(service,[packet])!;
  void Rejected(Packet_Server packet){try{Apply(packet);throw new Exception("Malformed seed payload accepted");}catch(TargetInvocationException e){Check(e.InnerException is InvalidDataException,"malformed seed payload rejected");}}
  var id=new Packet_Server{Id=1,Identification=new Packet_ServerIdentification{Seed=9918273,ServerName="fixture",MapSizeX=1024000}};
  var masked=Apply(id);Check(masked.Identification.Seed==0&&id.Identification.Seed==9918273,"identification masked without source mutation");Check(!ReferenceEquals(id,masked)&&!ReferenceEquals(id.Identification,masked.Identification),"outer and sensitive subpacket copied");Check(masked.Identification.ServerName=="fixture"&&masked.Identification.MapSizeX==1024000,"other handshake fields retained");
  foreach(int seed in new[]{0,1,-1,int.MinValue,int.MaxValue})foreach(var pos in new[]{(-12,-4),(0,0),(41,39)}){
   configType.GetProperty("PublicSeed")!.SetValue(config,seed);
   var state=new WeatherState{RegionX=pos.Item1,RegionZ=pos.Item2,LcgWorldSeed=98237123,LcgMapGenSeed=long.MinValue+74,LcgCurrentSeed=long.MaxValue-34,Weight=.74f,TransitionDelay=17,Transitioning=true,NewPattern=new(){Index=1}};
   var raw=SerializerUtil.Serialize(state);byte[] unknown=[0xa2,0x06,3,102,111,111];raw=[..raw,..unknown];var original=(byte[])raw.Clone();
   var packet=new Packet_Server{Id=55,CustomPacket=new(){ChannelId=7,MessageId=2}};packet.CustomPacket.SetData(raw);
   var result=Apply(packet);var client=SerializerUtil.Deserialize<WeatherState>(result.CustomPacket.Data);
   var rng=new LCGRandom(seed);rng.InitPositionSeed(pos.Item1/3,pos.Item2/3);
   Check(client.LcgWorldSeed==rng.worldSeed&&client.LcgMapGenSeed==rng.mapGenSeed&&client.LcgCurrentSeed==rng.currentSeed,"all three RNG seed fields sanitized, signed seed/coordinates supported");
   Check(client.RegionX==state.RegionX&&client.RegionZ==state.RegionZ&&client.Weight==state.Weight&&client.Transitioning&&client.TransitionDelay==17&&client.NewPattern.Index==1,"native weather state values preserved");
   Check(raw.SequenceEqual(original)&&state.LcgWorldSeed==98237123,"canonical weather bytes and object unchanged");
   Check(result.CustomPacket.Data.AsSpan().IndexOf(unknown)>=0,"unknown protobuf field preserved exactly");
   Check(!ReferenceEquals(result.CustomPacket,packet.CustomPacket)&&!ReferenceEquals(result.CustomPacket.Data,raw),"weather network copy owns modified bytes");
   var dict=new Dictionary<string,byte[]>{{"weatherState",raw},{"othermod",[4,5,6]}};var moddata=SerializerUtil.Serialize(dict);var region=new Packet_Server{Id=42,MapRegion=new(){RegionX=pos.Item1,RegionZ=pos.Item2,Moddata=moddata}};
   var rewritten=Apply(region);var copied=SerializerUtil.Deserialize<Dictionary<string,byte[]>>(rewritten.MapRegion.Moddata);var clientSaved=SerializerUtil.Deserialize<WeatherState>(copied["weatherState"]);
   Check(clientSaved.LcgWorldSeed==seed&&copied["othermod"].SequenceEqual(dict["othermod"]),"region weather seed sanitized and unrelated moddata retained");
   Check(SerializerUtil.Serialize(dict).SequenceEqual(moddata)&&region.MapRegion.Moddata.SequenceEqual(moddata),"stored region dictionary and canonical packet bytes unchanged");
  }
  configType.GetProperty("PublicSeed")!.SetValue(config,0);
  var unrelated=new Packet_Server{Id=55,CustomPacket=new(){ChannelId=8,MessageId=2,Data=[0xff]}};Check(ReferenceEquals(unrelated,Apply(unrelated)),"unrelated channel not decoded or modified");unrelated.CustomPacket.ChannelId=7;unrelated.CustomPacket.MessageId=3;Check(ReferenceEquals(unrelated,Apply(unrelated)),"unrelated message not decoded");
  var noWeather=new Packet_Server{Id=42,MapRegion=new(){Moddata=SerializerUtil.Serialize(new Dictionary<string,byte[]>{{"example",[1,2,3]}})}};Check(ReferenceEquals(noWeather,Apply(noWeather)),"region without weather state stays native");
  foreach(byte[] bad in new byte[][]{[0],[0xff],[0x60,0x80],[0x62,0],[0x0a,0xff,0x7f],[0x63],[0x60,255,255,255,255,255,255,255,255,255,2],new byte[16*1024*1024+1]})Rejected(new(){Id=55,CustomPacket=new(){ChannelId=7,MessageId=2,Data=bad}});
  Rejected(new(){Id=55,CustomPacket=new(){ChannelId=7,MessageId=2}});
  // Exercise the actual patched native serializer and verify its wire output.
  var server=(ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(ServerMain));var allocations=AccessTools.Field(typeof(ServerMain),"reusableBuffersDisposalList");allocations.SetValue(server,Activator.CreateInstance(allocations.FieldType));
  var serializer=AccessTools.Method(typeof(ServerMain),"Serialize_",[typeof(Packet_Server)]);var own=new Harmony("test.seedprivacy.serializer");
  type.GetField("active",F)!.SetValue(service,true);type.GetField("current",F)!.SetValue(null,service);
  own.Patch(serializer,prefix:new HarmonyMethod(type.GetMethod("BeforeSerialize",F)!));
  var sink=new Harmony("test.seedprivacy.sink");sink.Patch(AccessTools.Method(typeof(ServerMain),"SendPacket",[typeof(int),typeof(BoxedPacket)]),prefix:new HarmonyMethod(typeof(Checks),nameof(Sink)));
  try{
   int length=(int)serializer.Invoke(server,[id])!;var box=AccessTools.Field(typeof(ServerMain),"reusableBuffer").GetValue(null)!;var bytes=(byte[])AccessTools.Field(typeof(BoxedArray),"buffer").GetValue(box)!;var received=new Packet_Server();Packet_ServerSerializer.DeserializeBuffer(bytes,length,received);Check(received.Identification.Seed==0&&id.Identification.Seed==9918273,"real native serializer emits only the public seed");
   var send=AccessTools.Method(typeof(ServerMain),"SendPacket",[typeof(int),typeof(Packet_Server)]);send.Invoke(server,[123,id]);Check(sends==1,"valid sanitized packet follows native send path");
   try{send.Invoke(server,[123,new Packet_Server{Id=55,CustomPacket=new(){ChannelId=7,MessageId=2,Data=[0xff]}}]);throw new Exception("Malformed packet sent");}catch(TargetInvocationException e){Check(e.InnerException is InvalidDataException&&sends==1,"sanitization failure never sends the previous reusable buffer or raw payload");}
  }finally{sink.UnpatchAll(sink.Id);own.UnpatchAll(own.Id);type.GetField("current",F)!.SetValue(null,null);}
  Check(Harmony.GetPatchInfo(serializer)?.Owners.Contains(own.Id)!=true,"native serializer hook removed cleanly");
  Console.WriteLine($"PASS {checks} seed privacy default-off, immutable packet/weather/region copies, RNG fields, unknown fields, signed coordinates/seeds, malformed bounds and actual native serializer/fail-closed transport checks.");
 }
}
