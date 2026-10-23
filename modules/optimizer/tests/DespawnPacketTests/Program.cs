using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
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
  try
  {
   foreach(int count in new[]{0,1,2,3,64,257})
   {
    var input=Input(count);Switch(false);var stock=ServerPackets.GetEntityDespawnPacket(input);Switch(true);var reuse=ServerPackets.GetEntityDespawnPacket(input);
    Assert(stock.Id==reuse.Id && reuse.Id==36,"packet type");
    Assert(stock.EntityDespawn.EntityId.SequenceEqual(reuse.EntityDespawn.EntityId),"entity IDs and order");
    Assert(stock.EntityDespawn.DespawnReason.SequenceEqual(reuse.EntityDespawn.DespawnReason),"reasons and default death");
    Assert(stock.EntityDespawn.DeathDamageSource.SequenceEqual(reuse.EntityDespawn.DeathDamageSource),"damage sources including Block/Unknown distinction");
    Assert(stock.EntityDespawn.EntityIdCount==reuse.EntityDespawn.EntityIdCount && stock.EntityDespawn.DespawnReasonCount==reuse.EntityDespawn.DespawnReasonCount && stock.EntityDespawn.DeathDamageSourceCount==reuse.EntityDespawn.DeathDamageSourceCount,"serialized array counts");
    var next=ServerPackets.GetEntityDespawnPacket(input);if(count>0)Assert(!ReferenceEquals(next.EntityDespawn.EntityId,reuse.EntityDespawn.EntityId),"packets independently own output arrays");
   }
   foreach(var invalid in new List<EntityDespawn>[] {null!,new(){null!}}){Switch(false);var stock=Error(invalid);Switch(true);Assert(stock==Error(invalid),"invalid input exception/argument preserved");}
   Switch(true);long before=DespawnPacketOptimizations.Counters.Calls;DespawnPacketOptimizations.Configure(false);ServerPackets.GetEntityDespawnPacket(Input(2));Assert(DespawnPacketOptimizations.Counters.Calls==before,"independent disable uses stock");
   DespawnPacketOptimizations.Configure(true);Parallel.For(0,100,_=>{var p=ServerPackets.GetEntityDespawnPacket(Input(16));if(p.EntityDespawn.EntityId.Length!=16)throw new Exception("parallel packet mismatch");});Assert(DespawnPacketOptimizations.Counters.Calls==before+100,"parallel calls counted without shared buffers");
   object Measure(bool on,int count){Switch(on);var input=Input(count);for(int i=0;i<5000;i++)ServerPackets.GetEntityDespawnPacket(input);var clock=new Stopwatch();long start=GC.GetAllocatedBytesForCurrentThread();clock.Start();for(int i=0;i<30000;i++)ServerPackets.GetEntityDespawnPacket(input);clock.Stop();return new{enabled=on,entities=count,bytesPerCall=(GC.GetAllocatedBytesForCurrentThread()-start)/30000d,nsPerCall=clock.Elapsed.TotalNanoseconds/30000};}
   Console.WriteLine(JsonSerializer.Serialize(new{checks,trials=new[]{Measure(false,1),Measure(true,1),Measure(false,64),Measure(true,64),Measure(false,64),Measure(true,64),Measure(false,64),Measure(true,64)},scope="Actual official packet factory, synthetic inputs; transport/player workload not benchmarked."}));
  }
  finally{DespawnPacketOptimizations.Configure(false);own.UnpatchAll(own.Id);}
 }
}
