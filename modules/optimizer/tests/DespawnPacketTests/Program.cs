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
using Vintagestory.Common;
using Vintagestory.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var path=Path.Combine(folder,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));}return null;};
Checks.Run();
static class Checks
{
 static int checks;
 static void Assert(bool yes,string why){if(!yes)throw new Exception(why);checks++;}
 static List<EntityDespawn> Input(int count)=>Enumerable.Range(0,count).Select(i=>new EntityDespawn {EntityId=i%3==0?long.MaxValue:i%3==1?-i:i,DespawnData=i%3==0?null:new EntityDespawnData {Reason=Enum.GetValues<EnumDespawnReason>()[i%Enum.GetValues<EnumDespawnReason>().Length],DamageSourceForDeath=i%3==1?null:new DamageSource{Source=Enum.GetValues<EnumDamageSource>()[i%Enum.GetValues<EnumDamageSource>().Length]}}}).ToList();
 static (Type?,string?) Error(List<EntityDespawn> input){try{ServerPackets.GetEntityDespawnPacket(input);return(null,null);}catch(Exception e){return(e.GetType(),(e as ArgumentException)?.ParamName);}}
 [MethodImpl(MethodImplOptions.NoInlining)]
 public static void Run()
 {
  Assert(DespawnPacketOptimizations.ValidateContract(out _),"contract");
  var own=new Harmony("nos.despawn.test");
  void Switch(bool on){own.UnpatchAll(own.Id);DespawnPacketOptimizations.Configure(on);if(on)own.Patch(DespawnPacketOptimizations.Target,prefix:new HarmonyMethod(DespawnPacketOptimizations.PrefixMethod));}
  void Mode(int mode){own.UnpatchAll(own.Id);DespawnPacketOptimizations.Configure(mode==2);PreviousDespawnPacketOptimizations.Configure(mode==1);if(mode!=0)own.Patch(DespawnPacketOptimizations.Target,prefix:new HarmonyMethod(mode==2?DespawnPacketOptimizations.PrefixMethod:PreviousDespawnPacketOptimizations.PrefixMethod));}
  byte[] Wire(Packet_Server packet)=>ServerPackets.Serialize(packet,new IntRef());
  try
  {
   foreach(int count in new[]{0,1,2,3,64,257,4096})
   {
    var input=Input(count);Switch(false);var stock=ServerPackets.GetEntityDespawnPacket(input);Switch(true);var reuse=ServerPackets.GetEntityDespawnPacket(input);
    Assert(stock.Id==reuse.Id && reuse.Id==36,"packet type");
    Assert(stock.EntityDespawn.EntityId.SequenceEqual(reuse.EntityDespawn.EntityId),"entity IDs and order");
    Assert(stock.EntityDespawn.DespawnReason.SequenceEqual(reuse.EntityDespawn.DespawnReason),"reasons and default death");
    Assert(stock.EntityDespawn.DeathDamageSource.SequenceEqual(reuse.EntityDespawn.DeathDamageSource),"damage sources including Block/Unknown distinction");
    Assert(stock.EntityDespawn.EntityIdCount==reuse.EntityDespawn.EntityIdCount && stock.EntityDespawn.DespawnReasonCount==reuse.EntityDespawn.DespawnReasonCount && stock.EntityDespawn.DeathDamageSourceCount==reuse.EntityDespawn.DeathDamageSourceCount,"serialized array counts");
    Assert(Wire(stock).SequenceEqual(Wire(reuse)),"wire bytes match official packet serializer");
    var next=ServerPackets.GetEntityDespawnPacket(input);
    if(count>0)
    {
     Assert(!ReferenceEquals(next.EntityDespawn.EntityId,reuse.EntityDespawn.EntityId) && !ReferenceEquals(next.EntityDespawn.DespawnReason,reuse.EntityDespawn.DespawnReason) && !ReferenceEquals(next.EntityDespawn.DeathDamageSource,reuse.EntityDespawn.DeathDamageSource),"packets independently own all three output arrays");
     var original=input[0].EntityId;var saved=next.EntityDespawn.EntityId[0];reuse.EntityDespawn.EntityId[0]=123;
     Assert(input[0].EntityId==original && next.EntityDespawn.EntityId[0]==saved,"mutating one packet leaves input and later packets untouched");
    }
    input.Add(new EntityDespawn{EntityId=42});
    Assert(ServerPackets.GetEntityDespawnPacket(input).EntityDespawn.EntityId.Length==count+1,"list growth between calls is observed");
    input.Clear();Assert(ServerPackets.GetEntityDespawnPacket(input).EntityDespawn.EntityId.Length==0,"list clear between calls is observed");
   }
   foreach(var invalid in new List<EntityDespawn>[] {null!,new(){null!}}){Switch(false);var stock=Error(invalid);Switch(true);Assert(stock==Error(invalid),"invalid input exception/argument preserved");}
   Switch(true);long before=DespawnPacketOptimizations.Counters.Calls;DespawnPacketOptimizations.Configure(false);ServerPackets.GetEntityDespawnPacket(Input(2));Assert(DespawnPacketOptimizations.Counters.Calls==before,"independent disable uses stock");
   DespawnPacketOptimizations.Configure(true);Parallel.For(0,100,_=>{var p=ServerPackets.GetEntityDespawnPacket(Input(16));if(p.EntityDespawn.EntityId.Length!=16)throw new Exception("parallel packet mismatch");});Assert(DespawnPacketOptimizations.Counters.Calls==before+100,"parallel calls counted without shared buffers");
   Assert(DespawnPacketOptimizations.Dependencies.Count(m=>m.DeclaringType==typeof(Enumerable))==4,"closed native LINQ operators are conflict dependencies");
   object Observe(int mode,int count)
   {
    Mode(mode);var input=Input(count);
    for(int i=0;i<20000;i++)ServerPackets.GetEntityDespawnPacket(input);
    int iterations=count==512?30000:150000;
    long bytes=GC.GetAllocatedBytesForCurrentThread(),stamp=Stopwatch.GetTimestamp();
    for(int i=0;i<iterations;i++)ServerPackets.GetEntityDespawnPacket(input);
    double ns=Stopwatch.GetElapsedTime(stamp).TotalNanoseconds/iterations;
    return new{mode=new[]{"official","nos0.20.6","candidate"}[mode],count,ns,bytes=(GC.GetAllocatedBytesForCurrentThread()-bytes)/(double)iterations};
   }
   var rows=new List<object>();
   for(int trial=0;trial<7;trial++)foreach(int count in new[]{1,64,512})foreach(int mode in trial%2==0?new[]{0,1,2}:new[]{2,1,0})rows.Add(new{trial,result=Observe(mode,count)});
   string json=JsonSerializer.Serialize(new{checks,rows,tieredCompilation=Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")??"default",scope="Alternating actual official packet factory and author-owned previous/current Harmony prefixes; synthetic lists, not network transport or server ticks."});
   Console.WriteLine(json);var output=Environment.GetEnvironmentVariable("NOS_MEASUREMENTS");if(output!=null)File.WriteAllText(output,json+"\n");
  }
  finally{PreviousDespawnPacketOptimizations.Configure(false);DespawnPacketOptimizations.Configure(false);own.UnpatchAll(own.Id);}
 }
}
