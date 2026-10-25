using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using NewfiesOptimizer;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY");
AssemblyLoadContext.Default.Resolving+=(_,name)=>{
 foreach(string folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){
  string path=Path.Combine(folder,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
 }return null;
};
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? method,object?[]? args)=>Handler(method!,args);
 public static T Create<T>(System.Func<MethodInfo,object?[]?,object?> handler) where T:class {var value=DispatchProxy.Create<T,Proxy>();((Proxy)(object)value).Handler=handler;return value;}
}
static class Checks
{
 static int checks;
 static bool OtherPrefix()=>true;
 static void Assert(bool result,string message){if(!result)throw new Exception(message);checks++;}
 static (ICoreServerAPI Api,List<Action<float>> Ticks) Api(bool failListener=false, bool failUnregister=false)
 {
  var ticks=new List<Action<float>>();
  var logger=Proxy.Create<ILogger>((m,a)=>null);
  var events=Proxy.Create<IServerEventAPI>((m,a)=>{
   if(m.Name=="UnregisterGameTickListener" && failUnregister)throw new InvalidOperationException("test unregister failure");
   if(m.Name=="RegisterGameTickListener") {if(failListener)throw new InvalidOperationException("test listener failure");ticks.Add((Action<float>)a![0]!);return 1L;}
   return m.ReturnType==typeof(void)?null:m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null;
  });
  return (Proxy.Create<ICoreServerAPI>((m,a)=>m.Name=="get_Event"?events:m.Name=="get_Logger"?logger:m.ReturnType==typeof(void)?null:m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null),ticks);
 }
 static OptimizerConfig Config() => new() { EngineOptimizations = new() { ListenerRegistrationIndex=true,DespawnCleanupTimings=true,EntityLoadedDispatchReuse=true,EntityDespawnDispatchReuse=true,TemperatureAnimationChecks=true,EmotionSearchReuse=true,CraftingCandidateDedup=true,BlockSimulationBudget=true,BarrelMatchCountGuard=true,IdleFirepitFastPath=true,BarrelIngredientPairing=true,WildBushCallbackFlatten=true,ChunkUnloadStagingReuse=true,NativeBuildLogGuard=true,NearbyPlayerQueries = true, CollisionGenerationReuse=true,CachedCollisionGenerationReuse=true,AnimationCollisionFlagScan=true, CollisionTraversalReuse=true,ContainerTransitionScan=true,ContainerTickTraversal=true, NearbyEntityListReuse=true,CuboidEntityListReuse=true,ShapelessStackMerge=true,ShapelessWildcardMatch=true, PathMainThreadBudget = true, DelayedCallbackSnapshotReuse=true,SpawnOpportunityListReuse=true,SpawnColumnArrayReuse=true,BlockUpdateListReuse=true,ChunkLoadRequestListReuse=true,ChunkUnloadListReuse=true, BroadcastFilterReuse=true,EntityLifecycleListReuse=true, AiDistanceThrottle=true,DistantWanderThrottle=true,RepulsionDistanceThrottle=true,AttributeNoOpSkip=true,WaypointNodePooling=true } };
 [MethodImpl(MethodImplOptions.NoInlining)]
 public static void Run()
 {
  Assert(!EngineOptimizations.IsKnownRuntime("StratumLoadTester"),"load-test tools are not mistaken for replacement runtimes");
  Assert(EngineOptimizations.IsKnownRuntime("StratumServer"),"actual runtime recognized");
  var api=Api();
  var masterOff=Config();masterOff.Enabled=false;
  using(var module=new EngineOptimizations(api.Api,masterOff))Assert(module.Modules().All(x=>x.Status=="blocked"),"master off");
  var safe=Config();safe.Compatibility.SafeMode=true;
  using(var module=new EngineOptimizations(api.Api,safe))Assert(module.Modules().All(x=>x.Status=="blocked"),"safe mode");
  var off=Config();off.EngineOptimizations.ListenerRegistrationIndex=false;off.EngineOptimizations.DespawnCleanupTimings=false;off.EngineOptimizations.EntityLoadedDispatchReuse=false;off.EngineOptimizations.EntityDespawnDispatchReuse=false;off.EngineOptimizations.TemperatureAnimationChecks=false;off.EngineOptimizations.EmotionSearchReuse=false;off.EngineOptimizations.CraftingCandidateDedup=false;off.EngineOptimizations.BlockSimulationBudget=false;off.EngineOptimizations.BarrelMatchCountGuard=false;off.EngineOptimizations.IdleFirepitFastPath=off.EngineOptimizations.BarrelIngredientPairing=off.EngineOptimizations.WildBushCallbackFlatten=off.EngineOptimizations.ChunkUnloadStagingReuse=off.EngineOptimizations.NativeBuildLogGuard=false;off.EngineOptimizations.CollisionGenerationReuse=off.EngineOptimizations.CachedCollisionGenerationReuse=off.EngineOptimizations.AnimationCollisionFlagScan=off.EngineOptimizations.CollisionTraversalReuse=off.EngineOptimizations.ContainerTransitionScan=off.EngineOptimizations.ContainerTickTraversal=off.EngineOptimizations.NearbyEntityListReuse=off.EngineOptimizations.CuboidEntityListReuse=off.EngineOptimizations.ShapelessStackMerge=off.EngineOptimizations.ShapelessWildcardMatch=off.EngineOptimizations.DelayedCallbackSnapshotReuse=off.EngineOptimizations.SpawnOpportunityListReuse=off.EngineOptimizations.SpawnColumnArrayReuse=off.EngineOptimizations.BlockUpdateListReuse=off.EngineOptimizations.ChunkLoadRequestListReuse=off.EngineOptimizations.ChunkUnloadListReuse=off.EngineOptimizations.BroadcastFilterReuse=off.EngineOptimizations.EntityLifecycleListReuse=false;off.EngineOptimizations.AiDistanceThrottle=off.EngineOptimizations.DistantWanderThrottle=off.EngineOptimizations.RepulsionDistanceThrottle=off.EngineOptimizations.AttributeNoOpSkip=off.EngineOptimizations.WaypointNodePooling=false;off.EngineOptimizations.DespawnPacketProjection=off.EngineOptimizations.PhysicsPacketListReuse=off.EngineOptimizations.PathQueueLimitFix=off.EngineOptimizations.PathMainThreadBudget=off.EngineOptimizations.NearestEntitySearch=off.EngineOptimizations.InteractableEntitySearch=off.EngineOptimizations.AiTaskStartDispatch=off.EngineOptimizations.WorldgenRequestReuse=off.EngineOptimizations.SaveSnapshotRelease=off.EngineOptimizations.AiFilterDispatch=off.EngineOptimizations.RepulseDelegateReuse=off.EngineOptimizations.OnlinePlayerSnapshots=off.EngineOptimizations.NearbyPlayerQueries=off.EngineOptimizations.BulkStreamReads=off.EngineOptimizations.AssetCompressionCache=false;
  using(var module=new EngineOptimizations(api.Api,off))Assert(module.Modules().All(x=>x.Status=="disabled"),"independent switches");
  Assert(!Harmony.GetAllPatchedMethods().Any(),"disposing blocked/disabled modules never wraps their stock targets");
  // NOS timing observers must coexist with optimized methods and survive optimizer disposal.
  var timingObserver=new Harmony(EngineOptimizations.ProfilerHarmonyId);
  timingObserver.Patch(PlayerQueryOptimizations.SnapshotTarget,prefix:new HarmonyMethod(typeof(Checks).GetMethod(nameof(OtherPrefix),BindingFlags.Static|BindingFlags.NonPublic)!));
  try
  {
   var observed=Api();
   using(var module=new EngineOptimizations(observed.Api,Config(),()=>200))
   {
    Assert(module.Modules().Single(x=>x.Name=="onlinePlayerSnapshots").Status=="active","own profiler observer does not block installation");
    foreach(var tick in observed.Ticks)tick(1);
    Assert(module.Modules().Single(x=>x.Name=="onlinePlayerSnapshots").Status=="active","own observer does not trigger late conflict");
   }
   Assert(Harmony.GetPatchInfo(PlayerQueryOptimizations.SnapshotTarget)!.Owners.Contains(timingObserver.Id),"engine disposal leaves profiler observer installed");
  }
  finally{timingObserver.UnpatchAll(timingObserver.Id);}
  int population=0; var adaptive=Api();
  using(var module=new EngineOptimizations(adaptive.Api,Config(),()=>population))
  {
   Assert(module.Modules().Single(x=>x.Name=="nearbyPlayerQueries").Status=="standby","low population keeps original query unpatched");
   population=200; foreach(var tick in adaptive.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="nearbyPlayerQueries").Status=="active","query activates above threshold");
   population=110; foreach(var tick in adaptive.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="nearbyPlayerQueries").Status=="active","hysteresis avoids threshold churn");
   population=50; foreach(var tick in adaptive.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="nearbyPlayerQueries").Status=="standby","low population unpatches query again");
  }
  var test=Api();var foreign=new Harmony("test.nos.foreign");var prefix=new HarmonyMethod(typeof(Checks).GetMethod(nameof(OtherPrefix),BindingFlags.Static|BindingFlags.NonPublic)!);
  using(var module=new EngineOptimizations(test.Api,Config(),()=>200))
  {
   Assert(module.Modules().All(x=>x.Status=="active"),"verified official targets activate");
   Assert(PlayerQueryOptimizations.SnapshotEnabled&&PlayerQueryOptimizations.AroundEnabled&&StreamCopyOptimizations.ReadEnabled,"runtime gates enabled");
   foreign.Patch(PlayerQueryOptimizations.SnapshotTarget,prefix:prefix);
   foreach(var tick in test.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="onlinePlayerSnapshots").Status=="conflict","late conflict detected");
   Assert(!PlayerQueryOptimizations.SnapshotEnabled,"late conflict disables gate");
   Assert(Harmony.GetPatchInfo(PlayerQueryOptimizations.SnapshotTarget)!.Owners.Contains(foreign.Id),"foreign patch untouched");
   Assert(module.Modules().Where(x=>x.Name!="onlinePlayerSnapshots").All(x=>x.Status=="active"),"unrelated modules remain active");
  }
  foreign.UnpatchAll(foreign.Id);
  // Closed LINQ hooks are observable in the stock projection and must not be bypassed.
  foreach(var dependency in DespawnPacketOptimizations.Dependencies.Where(m=>m.DeclaringType==typeof(Enumerable)))
  {
   var packetApi=Api();using(var module=new EngineOptimizations(packetApi.Api,Config(),()=>200))
   {
    foreign.Patch(dependency,prefix:prefix);foreach(var tick in packetApi.Ticks)tick(1);
    Assert(module.Modules().Single(x=>x.Name=="despawnPacketProjection").Status=="conflict","late LINQ hook disables despawn projection");
    Assert(Harmony.GetPatchInfo(dependency)!.Owners.Contains(foreign.Id),"foreign LINQ hook survives fallback");
   }
   foreign.UnpatchAll(foreign.Id);
  }
  Assert(!PlayerQueryOptimizations.AroundEnabled&&!StreamCopyOptimizations.ReadEnabled,"dispose disables gates");
  Assert(!Harmony.GetAllPatchedMethods().Any(m=>Harmony.GetPatchInfo(m)!.Owners.Contains(EngineOptimizations.HarmonyId)),"dispose removes own patches");
  foreign.Patch(StreamCopyOptimizations.ReadConflictTargets[1],prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config()))Assert(module.Modules().Single(x=>x.Name=="bulkStreamReads").Status=="conflict","dependent accessor conflict blocks copy");
  foreign.UnpatchAll(foreign.Id);
  foreign.Patch(EntitySearchOptimizations.WalkTarget,prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
  {
   Assert(module.Modules().Single(x=>x.Name=="nearestEntitySearch").Status=="conflict","foreign partition walker blocks nearest replacement");
   Assert(module.Modules().Single(x=>x.Name=="interactableEntitySearch").Status=="conflict","foreign partition walker blocks interactable replacement");
  }
  foreign.UnpatchAll(foreign.Id);
  foreign.Patch(PathQueueOptimizations.WorkerTarget,prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
   Assert(module.Modules().Single(x=>x.Name=="pathQueueLimitFix").Status=="conflict","foreign path queue patch blocks queue replacement");
  foreign.UnpatchAll(foreign.Id);
  foreign.Patch(DespawnPacketOptimizations.Target,prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
   Assert(module.Modules().Single(x=>x.Name=="despawnPacketProjection").Status=="conflict","foreign despawn packet patch blocks projection optimization");
  foreign.UnpatchAll(foreign.Id);
  foreign.Patch(PhysicsPacketOptimizations.Target,prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
   Assert(module.Modules().Single(x=>x.Name=="physicsPacketListReuse").Status=="conflict","foreign physics packet patch blocks list reuse");
  foreign.UnpatchAll(foreign.Id);
  foreign.Patch(AiTaskStartOptimizations.Target,prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
   Assert(module.Modules().Single(x=>x.Name=="aiTaskStartDispatch").Status=="conflict","task-start foreign ownership blocks only its module");
  foreign.UnpatchAll(foreign.Id);
  foreign.Patch(AiFilterOptimizations.Target,prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
   Assert(module.Modules().Single(x=>x.Name=="aiFilterDispatch").Status=="conflict","AI foreign ownership blocks only its module");
  foreign.UnpatchAll(foreign.Id);
  for(int i=0;i<2;i++)
  {
   var cgApi=Api();using(var module=new EngineOptimizations(cgApi.Api,Config(),()=>200))
   {
    foreign.Patch(CollisionGenerationOptimizations.Target(i),prefix:prefix);foreach(var tick in cgApi.Ticks)tick(1);
    string affected=i==0?"collisionGenerationReuse":"cachedCollisionGenerationReuse";
    Assert(module.Modules().Single(x=>x.Name==affected).Status=="conflict","late generation conflict detected independently");
    Assert(module.Modules().Where(x=>x.Name!=affected).All(x=>x.Status=="active"),"unrelated generation/animation modules survive");
   }foreign.UnpatchAll(foreign.Id);
  }
  var boundsApi=Api();var boundsHelper=AccessTools.Method(typeof(Vintagestory.API.MathTools.BlockPos),nameof(Vintagestory.API.MathTools.BlockPos.SetAndEquals),[typeof(int),typeof(int),typeof(int)]);
  using(var module=new EngineOptimizations(boundsApi.Api,Config(),()=>200))
  {
   foreign.Patch(boundsHelper,prefix:prefix);foreach(var tick in boundsApi.Ticks)tick(1);
   Assert(module.Modules().Where(x=>x.Name is "collisionGenerationReuse" or "cachedCollisionGenerationReuse").All(x=>x.Status=="conflict"),"late foreign bounds helper blocks cache-hit shortcuts");
   Assert(module.Modules().Where(x=>x.Name is not ("collisionGenerationReuse" or "cachedCollisionGenerationReuse")).All(x=>x.Status=="active"),"bounds helper conflict leaves other modules active");
   Assert(Harmony.GetPatchInfo(boundsHelper)!.Owners.Contains(foreign.Id),"foreign bounds hook is preserved");
  }
  foreign.UnpatchAll(foreign.Id);
  foreign.Patch(CollisionGenerationOptimizations.Walk,prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config(),()=>200))Assert(module.Modules().Where(x=>x.Name is "collisionGenerationReuse" or "cachedCollisionGenerationReuse").All(x=>x.Status=="conflict"),"foreign walker blocks borrowed callback ownership");
  foreign.UnpatchAll(foreign.Id);
  var animApi=Api();using(var module=new EngineOptimizations(animApi.Api,Config(),()=>200))
  {
   AnimationScanOptimizations.Finalizer(new InvalidOperationException("animation-hook"),true);foreach(var tick in animApi.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="animationCollisionFlagScan").Status=="failed","animation failure removes affected scan");
   Assert(module.Modules().Where(x=>x.Name!="animationCollisionFlagScan").All(x=>x.Status=="active"),"unrelated modules survive animation error");
  }
  var craftApi=Api();using(var module=new EngineOptimizations(craftApi.Api,Config(),()=>200))
  {
   foreign.Patch(CraftingCandidateOptimizations.Target,prefix:prefix);foreach(var tick in craftApi.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="craftingCandidateDedup").Status=="conflict","foreign crafting-grid patch blocks candidate replacement");
   Assert(module.Modules().Where(x=>x.Name!="craftingCandidateDedup").All(x=>x.Status=="active"),"crafting conflict leaves other modules active");
  }foreign.UnpatchAll(foreign.Id);
  var craftDependency=Api();using(var module=new EngineOptimizations(craftDependency.Api,Config(),()=>200))
  {
   foreign.Patch(CraftingCandidateOptimizations.NativeAdd,prefix:prefix);foreach(var tick in craftDependency.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="craftingCandidateDedup").Status=="conflict","foreign dedup helper is not bypassed");
   Assert(Harmony.GetPatchInfo(CraftingCandidateOptimizations.NativeAdd)!.Owners.Contains(foreign.Id),"foreign dedup hook survives fallback");
  }foreign.UnpatchAll(foreign.Id);
  var craftFailure=Api();using(var module=new EngineOptimizations(craftFailure.Api,Config(),()=>200))
  {
   var grid=(Vintagestory.Common.InventoryCraftingGrid)RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Common.InventoryCraftingGrid));
   CraftingCandidateOptimizations.Prefix(grid,out var state);CraftingCandidateOptimizations.Finalizer(new InvalidOperationException("crafting-sentinel"),state);foreach(var tick in craftFailure.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="craftingCandidateDedup").Status=="failed","crafting failure stands down affected module");
   Assert(module.Modules().Where(x=>x.Name!="craftingCandidateDedup").All(x=>x.Status=="active"),"other modules survive crafting failure");
  }
  string[] traversalNames=["collisionTraversalReuse","containerTransitionScan","containerTickTraversal"];
  for(int i=0;i<3;i++)
  {
   var tickApi=Api();using(var module=new EngineOptimizations(tickApi.Api,Config(),()=>200))
   {
    foreign.Patch(TraversalOptimizations.Target(i),prefix:prefix);foreach(var tick in tickApi.Ticks)tick(1);
    Assert(module.Modules().Single(x=>x.Name==traversalNames[i]).Status=="conflict","late foreign traversal target disables independently "+i);
    Assert(module.Modules().Where(x=>x.Name!=traversalNames[i]).All(x=>x.Status=="active"),"other modules survive traversal conflict "+i);
   }foreign.UnpatchAll(foreign.Id);
  }
  foreign.Patch(TraversalOptimizations.Dependencies(1).Single(m=>m.Name=="MoveNext"),prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
  {
   Assert(module.Modules().Where(x=>x.Name is "containerTransitionScan" or "containerTickTraversal").All(x=>x.Status=="conflict"),"foreign inventory iterator state machine blocks both affected consumers");
   Assert(module.Modules().Where(x=>x.Name is not "containerTransitionScan" and not "containerTickTraversal").All(x=>x.Status=="active"),"unrelated targets survive iterator-state conflict");
  }foreign.UnpatchAll(foreign.Id);
  var traversalFailure=Api();using(var module=new EngineOptimizations(traversalFailure.Api,Config(),()=>200))
  {
   TraversalOptimizations.TickFinalizer(new InvalidOperationException("transition-hook"),true);foreach(var tick in traversalFailure.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="containerTickTraversal").Status=="failed","transition failure removes affected target");
   Assert(module.Modules().Where(x=>x.Name!="containerTickTraversal").All(x=>x.Status=="active"),"transition failure keeps independent modules active");
  }
  string[] queryRecipeNames=["nearbyEntityListReuse","cuboidEntityListReuse","shapelessStackMerge","shapelessWildcardMatch"];
  for(int i=0;i<4;i++)
  {
   var target=i<2?EntityQueryScratchOptimizations.Target(i):RecipeOptimizations.Target(i-2);
   using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
   {
    foreign.Patch(target,prefix:prefix);
    foreach(var tick in api.Ticks.ToArray())tick(1);
    Assert(module.Modules().Single(x=>x.Name==queryRecipeNames[i]).Status=="conflict","late foreign ownership disables affected query/recipe target "+i);
    Assert(module.Modules().Where(x=>x.Name!=queryRecipeNames[i]).All(x=>x.Status=="active"),"query/recipe conflict keeps independent modules active "+i);
    Assert(Harmony.GetPatchInfo(target)!.Owners.Contains(foreign.Id),"foreign query/recipe owner preserved");
   }
   foreign.UnpatchAll(foreign.Id);
  }
  foreach(var dependency in RecipeOptimizations.Dependencies(1).Skip(1))
  {
   foreign.Patch(dependency,prefix:prefix);
   using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
    Assert(module.Modules().Single(x=>x.Name=="shapelessWildcardMatch").Status=="conflict","foreign wildcard dependency retains native behavior");
   foreign.UnpatchAll(foreign.Id);
  }
  foreach(var dependency in EmotionSearchOptimizations.Dependencies)
  {
   foreign.Patch(dependency,prefix:prefix);
   using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
    Assert(module.Modules().Single(x=>x.Name=="emotionSearchReuse").Status=="conflict","foreign emotion-search dependency retains native behavior");
   foreign.UnpatchAll(foreign.Id);
  }
  foreach(var dependency in TemperatureAnimationOptimizations.Dependencies)
  {
   foreign.Patch(dependency,prefix:prefix);
   using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
    Assert(module.Modules().Single(x=>x.Name=="temperatureAnimationChecks").Status=="conflict","foreign temperature animation dependency retains native behavior");
   foreign.UnpatchAll(foreign.Id);
  }
  for(int kind=0;kind<2;kind++)
  {
   string name=kind==0?"entityLoadedDispatch":"entityDespawnDispatch";
   foreach(var dependency in EntityEventDispatchOptimizations.Dependencies(kind))
   {
    foreign.Patch(dependency,prefix:prefix);
    using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
     Assert(module.Modules().Single(x=>x.Name==name).Status=="conflict","foreign lifecycle dependency retains stock dispatch");
    foreign.UnpatchAll(foreign.Id);
   }
   var lifecycleFailure=Api();
   using(var module=new EngineOptimizations(lifecycleFailure.Api,Config(),()=>200))
   {
    if(kind==0)EntityEventDispatchOptimizations.LoadedFinalizer(new InvalidOperationException("dispatch-infrastructure"));
    else EntityEventDispatchOptimizations.DespawnFinalizer(new InvalidOperationException("dispatch-infrastructure"));
    foreach(var tick in lifecycleFailure.Ticks)tick(1);
    Assert(module.Modules().Single(x=>x.Name==name).Status=="failed","lifecycle infrastructure failure disables only its dispatch path");
   }
  }
  foreach(var target in new[]{ListenerRegistrationOptimizations.Target(0),ListenerRegistrationOptimizations.Target(1),DespawnCleanupTimings.Target})
  {
   string name=target==DespawnCleanupTimings.Target?"despawnCleanupTimings":target==ListenerRegistrationOptimizations.Target(0)?"entityListenerRegistrationIndex":"blockListenerRegistrationIndex";
   foreign.Patch(target,prefix:prefix);
   using(var module=new EngineOptimizations(api.Api,Config(),()=>200))Assert(module.Modules().Single(x=>x.Name==name).Status=="conflict","foreign cleanup/registration target keeps stock");
   foreign.UnpatchAll(foreign.Id);
  }
  var emotionFailure=Api();
  using(var module=new EngineOptimizations(emotionFailure.Api,Config(),()=>200))
  {
   EmotionSearchOptimizations.Finalizer(new InvalidOperationException("emotion-hook"),true);
   foreach(var tick in emotionFailure.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="emotionSearchReuse").Status=="failed","emotion query error disables only its module");
  }
  var recipeFailure=Api();
  using(var module=new EngineOptimizations(recipeFailure.Api,Config(),()=>200))
  {
   RecipeOptimizations.WildcardFinalizer(new InvalidOperationException("crafting-hook"),true);
   foreach(var tick in recipeFailure.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="shapelessWildcardMatch").Status=="failed","wildcard hook error removes affected module");
   Assert(module.Modules().Where(x=>x.Name!="shapelessWildcardMatch").All(x=>x.Status=="active"),"recipe hook error keeps other modules active");
  }
  string[] scratchNames=["blockUpdateListReuse","chunkLoadRequestListReuse","chunkUnloadCandidateListReuse","chunkUnloadNotificationReuse"];
  for(int kind=0;kind<4;kind++)
  {
   foreign.Patch(BlockChunkScratchOptimizations.Target(kind),prefix:prefix);
   using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
   {
    Assert(module.Modules().Single(x=>x.Name==scratchNames[kind]).Status=="conflict","foreign patch blocks only affected block/chunk target "+kind);
    Assert(module.Modules().Where(x=>x.Name!=scratchNames[kind]).All(x=>x.Status=="active"),"other modules survive block/chunk conflict "+kind);
   }
   foreign.UnpatchAll(foreign.Id);
  }
  var chunkFailure=Api();
  using(var module=new EngineOptimizations(chunkFailure.Api,Config(),()=>200))
  {
   BlockChunkScratchOptimizations.NotifyPrefix(out var state);BlockChunkScratchOptimizations.Finalizer(new InvalidOperationException("native-send"),state);
   foreach(var tick in chunkFailure.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="chunkUnloadNotificationReuse").Status=="failed","unload exception disables notification target");
   Assert(module.Modules().Where(x=>x.Name!="chunkUnloadNotificationReuse").All(x=>x.Status=="active"),"native unload error does not disable unrelated targets");
  }
  string[] workNames=["callbackSnapshotReuse","callbackDebugSnapshotReuse","spawnOpportunityListReuse","spawnColumnArrayReuse"];
  for(int kind=0;kind<4;kind++)
  {
   foreign.Patch(CallbackSpawnOptimizations.Target(kind),prefix:prefix);
   using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
   {
    var affected=kind==2?new[]{workNames[2],workNames[3]}:new[]{workNames[kind]};
    Assert(module.Modules().Where(x=>affected.Contains(x.Name)).All(x=>x.Status=="conflict"),"foreign ownership blocks affected callback/spawn target and dependent column scope "+kind);
    Assert(module.Modules().Where(x=>!affected.Contains(x.Name)).All(x=>x.Status=="active"),"unrelated modules survive callback/spawn conflict "+kind);
   }
   foreign.UnpatchAll(foreign.Id);
  }
  foreign.Patch(AccessTools.Method(typeof(Vintagestory.Server.ServerSystemEntitySpawner),"CanSpawnAt_offthread"),prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
  {
   Assert(module.Modules().Single(x=>x.Name=="spawnColumnArrayReuse").Status=="conflict","foreign spawn validator retains ownership of column input");
   Assert(module.Modules().Single(x=>x.Name=="spawnOpportunityListReuse").Status=="active","unrelated spawn scratch remains available with custom validator");
  }
  foreign.UnpatchAll(foreign.Id);
  var callbackFailure=Api();
  using(var module=new EngineOptimizations(callbackFailure.Api,Config(),()=>200))
  {
   CallbackSpawnOptimizations.TickPrefix(out var scope);CallbackSpawnOptimizations.Finalizer(new InvalidOperationException("callback-fixture"),scope);
   foreach(var tick in callbackFailure.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="callbackSnapshotReuse").Status=="failed","callback exception removes only its target");
   Assert(module.Modules().Where(x=>x.Name!="callbackSnapshotReuse").All(x=>x.Status=="active"),"other callback/spawn modules remain active after a normal-tick error");
  }
  var rangeConflict=Api();
  using(var module=new EngineOptimizations(rangeConflict.Api,Config(),()=>200))
  {
   foreign.Patch(AccessTools.Method(typeof(Vintagestory.GameContent.EntityBehaviorRepulseAgents),"IsInRangePartition"),prefix:prefix);
   foreach(var tick in rangeConflict.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="repulseDelegateReuse").Status=="conflict","late range-predicate patch disables repulsion optimization");
   Assert(module.Modules().Where(x=>x.Name!="repulseDelegateReuse").All(x=>x.Status=="active"),"unrelated modules survive repulsion conflict");
  }
  foreign.UnpatchAll(foreign.Id);
  foreign.Patch(WorldgenRequestOptimizations.Dependencies[1],prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
   Assert(module.Modules().Single(x=>x.Name=="worldgenRequestReuse").Status=="conflict","generation queue foreign patch retains stock lookup");
  foreign.UnpatchAll(foreign.Id);

  foreach(var pair in new[]{(Name:"idleFirepitFastPath",Method:BlockTickOptimizations.Dependencies(0)[1]),(Name:"barrelIngredientPairing",Method:BlockTickOptimizations.Dependencies(1)[1]),(Name:"wildBushCallbackFlatten",Method:BlockTickOptimizations.Dependencies(2)[1]),(Name:"chunkUnloadStagingReuse",Method:ChunkUnloadStagingOptimizations.Dependencies[1])})
  {
   var blockConflict=Api();
   using(var module=new EngineOptimizations(blockConflict.Api,Config(),()=>200))
   {
    foreign.Patch(pair.Method,prefix:prefix);foreach(var tick in blockConflict.Ticks)tick(1);
    Assert(module.Modules().Single(x=>x.Name==pair.Name).Status=="conflict","late block/unload dependency conflict disables only affected module: "+pair.Name);
    Assert(module.Modules().Any(x=>x.Name!="nativeBuildLogGuard"&&x.Name!=pair.Name&&x.Status=="active"),"unrelated modules remain active after block/unload conflict");
   }
   foreign.UnpatchAll(foreign.Id);
  }
  var logConflict=Api();
  using(var module=new EngineOptimizations(logConflict.Api,Config(),()=>200))
  {
   foreign.Patch(NativeBuildLogOptimizations.Target,prefix:prefix);
   foreach(var tick in logConflict.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="nativeBuildLogGuard").Status=="conflict","late native build-log dependency patch disables guard");
   Assert(module.Modules().Where(x=>x.Name!="nativeBuildLogGuard").All(x=>x.Status=="active"),"native log conflict leaves unrelated modules active");
  }
  foreign.UnpatchAll(foreign.Id);
  var storageConflict=Api();
  using(var module=new EngineOptimizations(storageConflict.Api,Config(),()=>200))
  {
   foreign.Patch(SaveSnapshotOptimizations.Dependencies[1],prefix:prefix);
   foreach(var tick in storageConflict.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="saveSnapshotRelease").Status=="conflict","late save snapshot patch disables release");
   Assert(module.Modules().Where(x=>x.Name!="saveSnapshotRelease").All(x=>x.Status=="active"),"save conflict leaves unrelated modules active");
  }
  foreign.UnpatchAll(foreign.Id);
  foreign.Patch(typeof(Vintagestory.Essentials.PathNode).GetConstructor([typeof(Vintagestory.Essentials.PathNode),typeof(Vintagestory.API.MathTools.Cardinal)])!,prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
   Assert(module.Modules().Where(x=>x.Name is "waypointNodePooling" or "waypointNodePoolScope").All(x=>x.Status=="conflict"),"foreign node constructor blocks both pool components");
  foreign.UnpatchAll(foreign.Id);
  var policyConflict=Api();
  using(var module=new EngineOptimizations(policyConflict.Api,Config(),()=>200))
  {
   foreign.Patch(EntityDistanceOptimizations.HurtTarget,prefix:prefix);
   foreach(var tick in policyConflict.Ticks)tick(1);
   Assert(module.Modules().Where(x=>x.Name is "entityHurtSafety" or "aiDistanceThrottle" or "distantWanderThrottle" or "repulsionDistanceThrottle").All(x=>x.Status=="conflict"),"late damage observer conflict disables dependent scheduling policies");
   Assert(module.Modules().Single(x=>x.Name=="attributeNoOpSkip").Status=="active","unrelated synchronization module remains active");
  }
  foreign.UnpatchAll(foreign.Id);
  var poolFailure=Api();
  using(var module=new EngineOptimizations(poolFailure.Api,Config(),()=>200))
  {
   var field=typeof(PathNodePoolOptimizations).GetField("failures",BindingFlags.NonPublic|BindingFlags.Static)!;field.SetValue(null,PathNodePoolOptimizations.Failures+1);
   foreach(var tick in poolFailure.Ticks)tick(1);
   Assert(module.Modules().Where(x=>x.Name is "waypointNodePooling" or "waypointNodePoolScope").All(x=>x.Status=="failed"),"pool failure stands down both components without replaying search");
  }
  var shared=Api();
  using(var module=new EngineOptimizations(shared.Api,Config(),()=>200))
  {
   var h=new Harmony(EngineOptimizations.HarmonyId);h.Unpatch(EntityDistanceOptimizations.RepulsionTarget,AccessTools.Method(typeof(EntityDistanceOptimizations),nameof(EntityDistanceOptimizations.RepulsionPrefix)));
   foreach(var tick in shared.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="repulsionDistanceThrottle").Status=="failed","missing exact prefix diagnosed despite another own patch on same target");
   Assert(module.Modules().Single(x=>x.Name=="repulseDelegateReuse").Status=="active","independent repulsion allocation patch survives scheduling stand-down");
   Assert(Harmony.GetPatchInfo(EntityDistanceOptimizations.RepulsionTarget)!.Transpilers.Any(p=>p.owner==EngineOptimizations.HarmonyId),"shared-target transpiler remains installed");
  }
  foreign.Patch(BroadcastOptimizations.RawTarget,prefix:prefix);
  using(var module=new EngineOptimizations(api.Api,Config(),()=>200))
  {
   Assert(module.Modules().Single(x=>x.Name=="broadcastRawFilter").Status=="conflict","foreign raw broadcast patch blocks allocation fast path");
   Assert(module.Modules().Single(x=>x.Name=="broadcastUdpFilter").Status=="active","unrelated UDP broadcast remains independently active");
  }
  foreign.UnpatchAll(foreign.Id);
  var lost=Api();
  using(var module=new EngineOptimizations(lost.Api,Config(),()=>200))
  {
   new Harmony(EngineOptimizations.HarmonyId).Unpatch(AssetCompressionOptimizations.Target,HarmonyPatchType.All,EngineOptimizations.HarmonyId);
   foreach(var tick in lost.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="assetCompressionCache").Status=="failed","removed own patch is diagnosed");
   Assert(module.Modules().Where(x=>x.Name!="assetCompressionCache").All(x=>x.Status=="active"),"removed patch does not disable unrelated modules");
  }
  var broken=Api(true);
  using(var module=new EngineOptimizations(broken.Api,Config(),()=>200))Assert(module.Modules().All(x=>x.Status=="failed"),"monitor startup failure rolls back all installed modules");
  Assert(!Harmony.GetAllPatchedMethods().Any(m=>Harmony.GetPatchInfo(m)!.Owners.Contains(EngineOptimizations.HarmonyId)),"no orphan patches after monitor failure");
  var idleBroken=Api(true);
  using(var module=new EngineOptimizations(idleBroken.Api,Config(),()=>0))
   Assert(module.Modules().All(x=>x.Status=="failed"),"monitor failure cannot leave a stranded standby module");
  var teardown=Api(failUnregister:true);
  var teardownModule=new EngineOptimizations(teardown.Api,Config(),()=>200);teardownModule.Dispose();
  foreach(var tick in teardown.Ticks)tick(1);
  Assert(!Harmony.GetAllPatchedMethods().Any(m=>Harmony.GetPatchInfo(m)!.Owners.Contains(EngineOptimizations.HarmonyId)),"unregister failure cannot orphan patches; disposed callbacks are inert");
  var failureField=typeof(AssetCompressionOptimizations).GetField("failures",BindingFlags.NonPublic|BindingFlags.Static)!;
  failureField.SetValue(null,10L);
  var retry=Api();
  using(var module=new EngineOptimizations(retry.Api,Config(),()=>200))
  {
   foreach(var tick in retry.Ticks)tick(1);
   Assert(module.Modules().All(x=>x.Status=="active"),"historical failure counts do not poison new activations");
   failureField.SetValue(null,11L);foreach(var tick in retry.Ticks)tick(1);
   Assert(module.Modules().Single(x=>x.Name=="assetCompressionCache").Status=="failed","new failures still stand module down");
  }
  var bench=Api();
  using(var module=new EngineOptimizations(bench.Api,Config(),()=>200))
  {
   void Tick(){foreach(var tick in bench.Ticks)tick(1);}
   for(int i=0;i<100;i++)Tick();
   long before=GC.GetAllocatedBytesForCurrentThread(),stamp=System.Diagnostics.Stopwatch.GetTimestamp();
   for(int i=0;i<1000;i++)Tick();
   var snapshots=(System.Collections.IDictionary)typeof(EngineOptimizations).GetField("patchSnapshots",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(module)!;
   Assert(snapshots.Count==0,"compatibility snapshots retain no patch records between checks");
   var result=new{iterations=1000,bytesPerCheck=(GC.GetAllocatedBytesForCurrentThread()-before)/1000d,nsPerCheck=System.Diagnostics.Stopwatch.GetElapsedTime(stamp).TotalNanoseconds/1000,scope="Actual Harmony module monitor with all modules active, 200 synthetic client count, no gameplay or network workload."};
   var report=System.Text.Json.JsonSerializer.Serialize(result);
   Console.WriteLine(report);
   var output=Environment.GetEnvironmentVariable("NOS_MEASUREMENTS");if(output!=null)File.WriteAllText(output,report+"\n");
  }
  foreach(var dependency in BarrelCountOptimizations.Dependencies)
  {
   foreign.Patch(dependency,prefix:prefix);
   using(var module=new EngineOptimizations(api.Api,Config()))Assert(module.Modules().Single(x=>x.Name=="barrelMatchCountGuard").Status=="conflict","foreign barrel count dependency retains stock");
   foreign.UnpatchAll(foreign.Id);
  }
  foreach(var dependency in BlockSimulationBudget.Dependencies)
  {
   foreign.Patch(dependency,prefix:prefix);
   using(var module=new EngineOptimizations(api.Api,Config()))Assert(module.Modules().Single(x=>x.Name=="blockSimulationBudget").Status=="conflict","foreign simulation dependency retains stock");
   foreign.UnpatchAll(foreign.Id);
  }
  AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("StratumServer.TestMarker"),AssemblyBuilderAccess.Run);
  using(var module=new EngineOptimizations(api.Api,Config()))Assert(module.Modules().All(x=>x.Status=="blocked"),"alternative runtime blocked");
  Console.WriteLine($"{checks} engine module lifecycle/conflict checks passed against actual Harmony targets.");
 }
}
