using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.Server;
using Vintagestory.Essentials;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(string dir in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){string path=Path.Combine(dir,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);}return null;};
Checks.Run();
public class Proxy:DispatchProxy
{
 protected override object? Invoke(MethodInfo? m,object?[]? args)=>m!.ReturnType==typeof(void)?null:m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null;
}
public class CustomAgent:EntityAgent{}
public class CustomBehavior(Entity e):EntityBehavior(e){public override string PropertyName()=>"custom";}
public class CustomTree:SyncedTreeAttribute{}
static class Checks
{
 static int checks;
 static T New<T>()where T:class=>(T)RuntimeHelpers.GetUninitializedObject(typeof(T));
 static void Check(bool value,string why){if(!value)throw new Exception(why);checks++;}
 static void Reset(){EntityDistanceOptimizations.Settings(64,250,1500,100,["game:*"],["game:trader-*"]);EntityDistanceOptimizations.ConfigureSafety(true);EntityDistanceOptimizations.ConfigureAi(true);EntityDistanceOptimizations.ConfigureWander(true);EntityDistanceOptimizations.ConfigureRepulsion(true);}
 static (EntityAgent Entity,AiTaskManager Manager,ConnectedClient Client) Fixture(EntityAgent? entity=null)
 {
  var server=New<ServerMain>();server.Clients=new CachingConcurrentDictionary<int,ConnectedClient>();
  var client=New<ConnectedClient>();client.State=EnumClientState.Playing;client.Player=New<ServerPlayer>();
  var player=New<EntityPlayer>();typeof(Entity).GetProperty(nameof(Entity.Pos))!.SetValue(player,new EntityPos(1000,100,0));
  client.WorldData=New<ServerWorldPlayerData>();client.WorldData.EntityPlayer=player;server.Clients.TryAdd(1,client);
  var e=entity??new EntityAgent();e.World=server;e.Api=DispatchProxy.Create<ICoreServerAPI,Proxy>();e.Code=new AssetLocation("game:wolf");e.OnGround=true;e.AlwaysActive=false;e.EntityId=1;
  typeof(Entity).GetProperty(nameof(Entity.Properties))!.SetValue(e,new EntityProperties{Server=new EntityServerProperties([],new()){Behaviors=new List<EntityBehavior>()}});
  var ai=new EntityBehaviorTaskAI(e);e.Properties.Server.Behaviors.Add(ai);
  return(e,ai.TaskManager,client);
 }
 static bool Ai(EntityAgent e,AiTaskManager m)=>EntityDistanceOptimizations.AiPrefix(m,e);
 static void Exempt(string name,Action<EntityAgent,AiTaskManager,ConnectedClient> change)
 {Reset();var f=Fixture();change(f.Entity,f.Manager,f.Client);Check(Ai(f.Entity,f.Manager)&&Ai(f.Entity,f.Manager),name);}
 public static void Run()
 {
  Check(EntityDistanceOptimizations.ValidateContract(out var why),why);
  Check(EntityDistanceOptimizations.Wildcard("game:w*lf","GAME:wolf")&&!EntityDistanceOptimizations.Wildcard("game:cat*","game:wolf"),"wildcard matches without case sensitivity");
  long due=0;Check(!EntityDistanceOptimizations.Defer(ref due,1000,250,7)&&due==1257&&EntityDistanceOptimizations.Defer(ref due,1256,250,7)&&!EntityDistanceOptimizations.Defer(ref due,1257,250,7)&&due==1507,"deterministic first-use, stagger and interval boundary");
  Reset();var f=Fixture();Check(Ai(f.Entity,f.Manager)&&!Ai(f.Entity,f.Manager),"distant AI first evaluation runs; subsequent evaluation deferred");
  bool result=true;Check(EntityDistanceOptimizations.WanderPrefix(f.Entity,ref result)&&!EntityDistanceOptimizations.WanderPrefix(f.Entity,ref result)&&!result,"distant wander deferred with false result");
  Check(EntityDistanceOptimizations.RepulsionPrefix(f.Entity)&&!EntityDistanceOptimizations.RepulsionPrefix(f.Entity),"stationary distant repulsion deferred");f.Entity.Pos.Motion.X=.01;Check(EntityDistanceOptimizations.RepulsionPrefix(f.Entity),"moving repulsion never deferred");
  EntityDistanceOptimizations.HurtPrefix(f.Entity);Check(Ai(f.Entity,f.Manager)&&Ai(f.Entity,f.Manager),"damage grace immediately wakes AI");
  Exempt("nearby observer",(e,m,c)=>c.Entityplayer.Pos.SetPos(0,0,0));
  Exempt("different dimension has no known observer",(e,m,c)=>c.Entityplayer.Pos.Dimension=1);
  Exempt("connecting observer excluded",(e,m,c)=>c.State=EnumClientState.Connecting);
  Exempt("always active",(e,m,c)=>e.AlwaysActive=true);
  Exempt("airborne",(e,m,c)=>e.OnGround=false);
  Exempt("mounted",(e,m,c)=>typeof(EntityAgent).GetProperty(nameof(EntityAgent.MountedOn))!.SetValue(e,DispatchProxy.Create<IMountableSeat,Proxy>()));
  Exempt("swimming",(e,m,c)=>e.Swimming=true);
  Exempt("burning",(e,m,c)=>e.IsOnFire=true);
  Exempt("bred generation",(e,m,c)=>e.WatchedAttributes.SetInt("generation",1));
  Exempt("owner metadata",(e,m,c)=>e.WatchedAttributes.SetString("ownerUid","owner"));
  Exempt("tamed",(e,m,c)=>e.WatchedAttributes.SetBool("tamed",true));
  Exempt("named",(e,m,c)=>{var name=new TreeAttribute();name.SetString("name","Pet");e.WatchedAttributes["nametag"]=name;});
  Exempt("custom behavior",(e,m,c)=>e.Properties.Server.Behaviors.Add(new CustomBehavior(e)));
  Exempt("explicit exclusion",(e,m,c)=>e.Code=new AssetLocation("game:trader-test"));
  Exempt("outside include",(e,m,c)=>e.Code=new AssetLocation("mod:wolf"));
  Exempt("custom task implementation",(e,m,c)=>m.AllTasks.Add(DispatchProxy.Create<IAiTask,Proxy>()));
  Exempt("active non-idle task",(e,m,c)=>m.ActiveTasksBySlot[0]=New<AiTaskMeleeAttack>());
  Reset();var custom=Fixture(new CustomAgent());Check(Ai(custom.Entity,custom.Manager)&&Ai(custom.Entity,custom.Manager),"custom entity class remains stock");
  Reset();f=Fixture();Check(Ai(f.Entity,f.Manager)&&!Ai(f.Entity,f.Manager),"far fixture active");EntityDistanceOptimizations.ConfigureAi(false);Check(Ai(f.Entity,f.Manager),"independent disable immediately restores evaluations");
  Reset();f=Fixture();Check(Ai(f.Entity,f.Manager)&&!Ai(f.Entity,f.Manager),"far state cached");f.Entity.Pos.SetPos(990,100,0);Check(Ai(f.Entity,f.Manager),"entity movement invalidates proximity cache immediately");
  var fail=EntityDistanceOptimizations.Counters.Failures;Check(EntityDistanceOptimizations.AiPrefix(null!,null!)&&EntityDistanceOptimizations.Counters.Failures==fail+1,"invalid fixture fails open and records failure");
  Reset();f=Fixture();var aiHarmony=new Harmony("test.nos.ai-distance-injection");
  aiHarmony.Patch(EntityDistanceOptimizations.AiTarget,prefix:new HarmonyMethod(typeof(EntityDistanceOptimizations),nameof(EntityDistanceOptimizations.AiPrefix)));
  var start=EntityDistanceOptimizations.AiTarget.CreateDelegate<Action<AiTaskManager>>();long aiBefore=EntityDistanceOptimizations.Counters.Ai;
  start(f.Manager);start(f.Manager);Check(EntityDistanceOptimizations.Counters.Ai==aiBefore+1,"actual injected official task-start method defers eligible evaluations");
  Check(!(Harmony.GetPatchInfo(AccessTools.Method(typeof(AiTaskManager),"ProcessRunningTasks"))?.Owners.Contains(aiHarmony.Id)??false),"running-task method never patched");aiHarmony.UnpatchAll(aiHarmony.Id);
  var exemptBatch=Enumerable.Range(0,256).Select(_=>Fixture()).ToArray();foreach(var entry in exemptBatch)entry.Entity.AlwaysActive=true;
  Reset();foreach(var entry in exemptBatch)Ai(entry.Entity,entry.Manager); // JIT warmup outside measurement.
  var exemptMeasurements=new List<object>();
  for(int trial=0;trial<5;trial++){
   Reset();long allocated=GC.GetAllocatedBytesForCurrentThread(),begin=Stopwatch.GetTimestamp();
   foreach(var entry in exemptBatch)if(!Ai(entry.Entity,entry.Manager))throw new Exception("Exempt entity deferred");
   exemptMeasurements.Add(new{trial,bytesPerEntity=(GC.GetAllocatedBytesForCurrentThread()-allocated)/256d,nsPerEntity=Stopwatch.GetElapsedTime(begin).TotalNanoseconds/256});
  }
  Reset();long exemptStart=GC.GetAllocatedBytesForCurrentThread();
  foreach(var entry in exemptBatch){bool protectedResult=false;if(!Ai(entry.Entity,entry.Manager)||!EntityDistanceOptimizations.WanderPrefix(entry.Entity,ref protectedResult)||!EntityDistanceOptimizations.RepulsionPrefix(entry.Entity))throw new Exception("Protected path changed gameplay");}
  Check(GC.GetAllocatedBytesForCurrentThread()==exemptStart,"all protected scheduler paths avoid new state allocations");
  Console.WriteLine(JsonSerializer.Serialize(new{exemptMeasurements,scope="Enabled distance policies; first encounters of already-exempt native entities, no server TPS claim."}));
  Reset();f=Fixture();Check(Ai(f.Entity,f.Manager)&&!Ai(f.Entity,f.Manager),"prior delay established before exemption change");f.Entity.AlwaysActive=true;Check(Ai(f.Entity,f.Manager),"new exemption runs stock immediately");f.Entity.AlwaysActive=false;Check(Ai(f.Entity,f.Manager),"removing exemption does not resurrect stale delay");
  // Actual Harmony injection into official attribute method, including stock behavior comparisons.
  var tree=new SyncedTreeAttribute();tree.RemoveAttribute("missing");Check(tree.AllDirty,"stock missing removal marks whole tree dirty");
  var harmony=new Harmony("test.nos.distance-policies");harmony.Patch(AttributeSyncOptimizations.Target,prefix:new HarmonyMethod(typeof(AttributeSyncOptimizations),nameof(AttributeSyncOptimizations.Prefix)));AttributeSyncOptimizations.Configure(true);
  tree.MarkClean();tree.RemoveAttribute("missing");Check(!tree.AllDirty&&!tree.PartialDirty,"missing attribute does not force resync");
  tree.SetInt("kept",3);tree.RemoveAttribute("missing");Check(tree.PartialDirty&&tree.GetInt("kept")==3&&!tree.AllDirty,"existing partial dirty paths retained");
  tree.RemoveAttribute("kept");Check(!tree.HasAttribute("kept")&&tree.AllDirty,"real removal unchanged");
  var sub=new CustomTree();sub.RemoveAttribute("missing");Check(sub.AllDirty,"custom attribute subclass remains stock");
  AttributeSyncOptimizations.Configure(false);tree.MarkClean();tree.RemoveAttribute("missing");Check(tree.AllDirty,"attribute switch immediately restores stock behavior");
  // Candidate-node ownership: use actual official PathNode constructor and scope hooks.
  var owner=New<AStar>();PathNodePoolOptimizations.ConfigureScope(true);PathNodePoolOptimizations.Configure(true);
  var nearest=new PathNode(new BlockPos(1,2,3,2));var dir=Cardinal.ALL[0];
  PathNodePoolOptimizations.Prefix(owner,out var lease);var first=PathNodePoolOptimizations.Create(nearest,dir,owner);first.gCost=7;first.hCost=9;first.Parent=nearest;first.pathLength=9;first.HeapIndex=14;first.Action=(EnumTraverseAction)1;
  PathNodePoolOptimizations.Finalizer(null,lease);var raw=PathNodePoolOptimizations.Create(nearest,dir,owner);
  PathNodePoolOptimizations.Prefix(owner,out lease);var next=PathNodePoolOptimizations.Create(nearest,dir,owner);
  Check(ReferenceEquals(first,next)&&!ReferenceEquals(raw,next)&&next.gCost==0&&next.hCost==0&&next.Parent is null&&next.pathLength==0&&next.HeapIndex==0&&next.Action==default&&next.dimension==2,"reused candidate reset; raw node independent; dimension preserved");
  var seen=new HashSet<PathNode>(ReferenceEqualityComparer.Instance){next};bool unique=true;for(int i=1;i<PathNodePoolOptimizations.Capacity+10;i++)unique &= seen.Add(PathNodePoolOptimizations.Create(nearest,dir,owner));Check(unique,"no aliases in a single search including overflow");
  PathNodePoolOptimizations.Prefix(owner,out var nested);Check(!ReferenceEquals(first,PathNodePoolOptimizations.Create(nearest,dir,owner)),"nested same-solver scope bypasses busy pool");PathNodePoolOptimizations.Finalizer(null,nested);
  var expected=new InvalidOperationException("test");Check(ReferenceEquals(expected,PathNodePoolOptimizations.Finalizer(expected,lease)),"finalizer preserves exception");
  PathNodePoolOptimizations.Prefix(owner,out lease);Check(ReferenceEquals(first,PathNodePoolOptimizations.Create(nearest,dir,owner)),"exception releases pool lease");PathNodePoolOptimizations.Finalizer(null,lease);
  PathNodePoolOptimizations.Configure(false);PathNodePoolOptimizations.Prefix(owner,out lease);Check(lease==null&&!ReferenceEquals(first,PathNodePoolOptimizations.Create(nearest,dir,owner)),"pool disable restores stock allocation");
  harmony.UnpatchAll(harmony.Id);Console.WriteLine($"PASS {checks} distance safety, attribute synchronization and candidate ownership checks. Engine fixtures, not a production workload.");
 }
}
