using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var path=Path.Combine(folder,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));}return null;};
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? args)=>Handler(m!,args);
 public static T Create<T>(System.Func<MethodInfo,object?[]?,object?> fn)where T:class{var value=Create<T,Proxy>();((Proxy)(object)value).Handler=fn;return value;}
}
public class Creature:Entity { public override bool IsCreature=>true; }
static class Checks
{
 static int checks; static void Assert(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 delegate Entity? Nearest(EntityPartitioning owner,Vec3d position,double radius,ActionConsumable<Entity>? matches,EnumEntitySearchType kind);
 delegate Entity? Interactable(EntityPartitioning owner,Vec3d position,double radius,ActionConsumable<Entity>? matches);
 [MethodImpl(MethodImplOptions.NoInlining)] public static void Run()
 {
  Assert(EntitySearchOptimizations.ValidateContract(out var reason),reason);
  var block=Proxy.Create<IBlockAccessor>((m,a)=>2048);
  var world=Proxy.Create<IServerWorldAccessor>((m,a)=>m.Name=="get_BlockAccessor"?block:null);
  var api=Proxy.Create<ICoreServerAPI>((m,a)=>m.Name=="get_World"?world:null);
  var partition=new EntityPartitioning();AccessTools.Field(typeof(EntityPartitioning),"api").SetValue(partition,api);
  var chunk=new EntityPartitionChunk();
  for(int i=0;i<16;i++){var entity=(Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));entity.EntityId=i+1;entity.State=EnumEntityState.Active;AccessTools.Property(typeof(Entity),"Pos").SetValue(entity,new EntityPos(65+i*.1,64,64));chunk.Add(entity,0);}
  partition.Partitions[MapUtil.Index3dL(2,2,2,64,64)]=chunk;
  var nearest=EntitySearchOptimizations.NearestTarget.CreateDelegate<Nearest>();
  var interactable=EntitySearchOptimizations.InteractableTarget.CreateDelegate<Interactable>();
  var position=new Vec3d(64,64,64);ActionConsumable<Entity> allow=_=>true;
  var order=new List<long>();ActionConsumable<Entity> filtered=e=>{order.Add(e.EntityId);return e.EntityId>2;};
  var stock=nearest(partition,position,8,filtered,EnumEntitySearchType.Creatures);var stockOrder=order.ToArray();
  var stockInteractable=interactable(partition,position,8,null);
  var before=Measure(()=>nearest(partition,position,8,allow,EnumEntitySearchType.Creatures));
  var beforeInteract=Measure(()=>interactable(partition,position,8,allow));
  var harmony=new Harmony("test.nos.entity-search");
  harmony.Patch(EntitySearchOptimizations.NearestTarget,prefix:new HarmonyMethod(EntitySearchOptimizations.NearestPrefixMethod));
  harmony.Patch(EntitySearchOptimizations.InteractableTarget,prefix:new HarmonyMethod(EntitySearchOptimizations.InteractablePrefixMethod));
  EntitySearchOptimizations.ConfigureNearest(true);EntitySearchOptimizations.ConfigureInteractable(true);
  order.Clear();Assert(ReferenceEquals(stock,nearest(partition,position,8,filtered,EnumEntitySearchType.Creatures))&&stockOrder.SequenceEqual(order),"predicate order and selected entity preserved");
  Assert(ReferenceEquals(stockInteractable,interactable(partition,position,8,null)),"null interactable filter preserved");
  Assert(nearest(partition,position,0,allow,EnumEntitySearchType.Creatures)==null,"strict zero-radius exclusion preserved");
  Assert(nearest(partition,position,8,_=>false,EnumEntitySearchType.Creatures)==null,"no matching candidate preserved");
  int calls=0;try{nearest(partition,position,8,_=>{calls++;throw new InvalidOperationException("sentinel");},EnumEntitySearchType.Creatures);throw new Exception("Exception swallowed");}catch(InvalidOperationException e){Assert(e.Message=="sentinel"&&calls==1,"predicate exception is not retried");}
  try{nearest(partition,position,8,null,EnumEntitySearchType.Creatures);throw new Exception("Stock null semantics changed");}catch(NullReferenceException){Assert(true,"stock nearest null filter exception preserved");}
  var recursive=nearest(partition,position,8,e=>{Assert(nearest(partition,position,8,allow,EnumEntitySearchType.Creatures)!.EntityId==1,"nested search independent");return e.EntityId>2;},EnumEntitySearchType.Creatures);
  Assert(ReferenceEquals(stock,recursive),"recursive outer search preserved");
  Assert(!EntitySearchOptimizations.CurrentThreadRetainsInputs,"idle context clears position/filter/result references");
  Parallel.For(0,128,_=>{if(nearest(partition,position,8,allow,EnumEntitySearchType.Creatures)!.EntityId!=1||EntitySearchOptimizations.CurrentThreadRetainsInputs)throw new Exception("Concurrent state leaked");});
  var after=Measure(()=>nearest(partition,position,8,allow,EnumEntitySearchType.Creatures));
  var afterInteract=Measure(()=>interactable(partition,position,8,allow));
  Console.WriteLine(JsonSerializer.Serialize(new{nearestStock=before,nearestOptimized=after,interactableStock=beforeInteract,interactableOptimized=afterInteract}));
  Assert(before.Bytes-after.Bytes>=64&&beforeInteract.Bytes-afterInteract.Bytes>=64,"warm searches reduce allocation beyond unchanged proxy/walker overhead");
  EntitySearchOptimizations.ConfigureNearest(false);Assert(ReferenceEquals(stock,nearest(partition,position,8,filtered,EnumEntitySearchType.Creatures)),"disabled nearest uses stock");
  EntitySearchOptimizations.ConfigureInteractable(false);harmony.UnpatchAll(harmony.Id);
  bool patched=false;
  void Mode(bool value){if(value&&!patched){harmony.Patch(EntitySearchOptimizations.NearestTarget,prefix:new HarmonyMethod(EntitySearchOptimizations.NearestPrefixMethod));harmony.Patch(EntitySearchOptimizations.InteractableTarget,prefix:new HarmonyMethod(EntitySearchOptimizations.InteractablePrefixMethod));}else if(!value&&patched)harmony.UnpatchAll(harmony.Id);patched=value;EntitySearchOptimizations.ConfigureNearest(value);EntitySearchOptimizations.ConfigureInteractable(value);}
  object Trial(bool value){Mode(value);return new{nearest=Measure(()=>nearest(partition,position,8,allow,EnumEntitySearchType.Creatures)),interactable=Measure(()=>interactable(partition,position,8,allow))};}
  var trials=new List<object>();
  for(int i=0;i<5;i++){object a,b;if(i%2==0){a=Trial(false);b=Trial(true);}else{b=Trial(true);a=Trial(false);}trials.Add(new{trial=i,stock=a,optimized=b});}
  Mode(false);
  Console.WriteLine(JsonSerializer.Serialize(new{checks,trials}));
  Console.WriteLine("PASS entity search behavior, recursion, exceptions, thread isolation, reference clearing and allocation comparison against actual stock walker.");
 }
 public record Measurement(double Nanoseconds,double Bytes);
 static Measurement Measure(Action action){for(int i=0;i<10000;i++)action();const int count=100000;long allocated=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();for(int i=0;i<count;i++)action();return new(Stopwatch.GetElapsedTime(start).TotalNanoseconds/count,(GC.GetAllocatedBytesForCurrentThread()-allocated)/(double)count);}
}
