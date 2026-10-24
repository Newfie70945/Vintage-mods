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
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var d in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(d,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? args)=>Handler(m!,args);
 public static T Make<T>(System.Func<MethodInfo,object?[]?,object?> handler)where T:class{var p=Create<T,Proxy>();((Proxy)(object)p).Handler=handler;return p;}
}
public class Creature:Entity{public override bool IsCreature=>true;}
static class Checks
{
 delegate Entity? Nearest(EntityPartitioning owner,Vec3d p,double radius,ActionConsumable<Entity>? filter,EnumEntitySearchType kind);
 delegate Entity? Interactable(EntityPartitioning owner,Vec3d p,double radius,ActionConsumable<Entity>? filter);
 static void Require(bool value,string message){if(!value)throw new Exception(message);}
 public static void Run()
 {
  var block=Proxy.Make<IBlockAccessor>((m,a)=>2048);
  var world=Proxy.Make<IServerWorldAccessor>((m,a)=>m.Name=="get_BlockAccessor"?block:null);
  var api=Proxy.Make<ICoreServerAPI>((m,a)=>m.Name=="get_World"?world:m.Name=="get_Side"?EnumAppSide.Server:null);
  var owner=new EntityPartitioning();AccessTools.Field(typeof(EntityPartitioning),"api").SetValue(owner,api);var chunk=new EntityPartitionChunk();
  for(int i=0;i<16;i++){var e=(Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));e.EntityId=i+1;e.State=EnumEntityState.Active;AccessTools.Property(typeof(Entity),nameof(Entity.Pos)).SetValue(e,new EntityPos(65+i*.1,64,64));chunk.Add(e,0);}
  owner.Partitions[MapUtil.Index3dL(2,2,2,64,64)]=chunk;
  var nearest=EntitySearchOptimizations.NearestTarget.CreateDelegate<Nearest>();var interactable=EntitySearchOptimizations.InteractableTarget.CreateDelegate<Interactable>();var point=new Vec3d(64,64,64);ActionConsumable<Entity> allow=_=>true;
  ActionConsumable<Entity> nested=e=>{Require(nearest(owner,point,8,allow,EnumEntitySearchType.Creatures)?.EntityId==1,"Nested result changed");return e.EntityId>2;};
  var h=new Harmony("test.nos.search-comparison");
  void Mode(bool current)
  {
   h.UnpatchAll(h.Id);EntitySearchOptimizations.ConfigureNearest(false);EntitySearchOptimizations.ConfigureInteractable(false);PreviousEntitySearchOptimizations.ConfigureNearest(false);PreviousEntitySearchOptimizations.ConfigureInteractable(false);
   Type t=current?typeof(EntitySearchOptimizations):typeof(PreviousEntitySearchOptimizations);
   h.Patch(EntitySearchOptimizations.NearestTarget,prefix:new HarmonyMethod(t,"NearestPrefix"));h.Patch(EntitySearchOptimizations.InteractableTarget,prefix:new HarmonyMethod(t,"InteractablePrefix"));
   if(current){EntitySearchOptimizations.ConfigureNearest(true);EntitySearchOptimizations.ConfigureInteractable(true);}else{PreviousEntitySearchOptimizations.ConfigureNearest(true);PreviousEntitySearchOptimizations.ConfigureInteractable(true);}
  }
  Action flat=()=>Require(nearest(owner,point,8,allow,EnumEntitySearchType.Creatures)?.EntityId==1,"Flat result changed");
  Action interaction=()=>Require(interactable(owner,point,8,null)?.EntityId==1,"Interactable result changed");
  Action recursive=()=>Require(nearest(owner,point,8,nested,EnumEntitySearchType.Creatures)?.EntityId==3,"Outer result changed");
  object Observe(Action action){for(int i=0;i<20000;i++)action();long before=GC.GetAllocatedBytesForCurrentThread(),stamp=Stopwatch.GetTimestamp();for(int i=0;i<200000;i++)action();return new{ns=Stopwatch.GetElapsedTime(stamp).TotalNanoseconds/200000,bytes=(GC.GetAllocatedBytesForCurrentThread()-before)/200000d};}
  object Trial(bool current){Mode(current);return new{nearest=Observe(flat),interactable=Observe(interaction),nestedNearest=Observe(recursive)};}
  var rows=new List<object>();for(int i=0;i<7;i++){object before,after;if(i%2==0){before=Trial(false);after=Trial(true);}else{after=Trial(true);before=Trial(false);}rows.Add(new{trial=i,before,after});}
  h.UnpatchAll(h.Id);EntitySearchOptimizations.ConfigureNearest(false);EntitySearchOptimizations.ConfigureInteractable(false);PreviousEntitySearchOptimizations.ConfigureNearest(false);PreviousEntitySearchOptimizations.ConfigureInteractable(false);
  var result=new{rows,tieredCompilation=Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")??"default",scope="Alternating same-process previous/current prefixes on official search methods/walker; synthetic entity/API fixtures, not server tick or player-capacity evidence."};string json=JsonSerializer.Serialize(result);Console.WriteLine(json);var output=Environment.GetEnvironmentVariable("NOS_MEASUREMENTS");if(output!=null)File.WriteAllText(output,json+"\n");
 }
}
