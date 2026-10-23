using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Essentials;

// Disposable validation only. Isolated queues and actual official A* on loaded terrain.
public sealed class EntitySuiteProbe:ModSystem
{
 public override bool ShouldLoad(EnumAppSide side)=>side==EnumAppSide.Server;
 public override void StartServerSide(ICoreServerAPI api)
 {
  api.ChatCommands.Create("nosentitycheck").RequiresPrivilege(Privilege.controlserver).HandleWith(_=>{
   var spawn=api.World.DefaultSpawnPosition;
   api.WorldManager.LoadChunkColumnPriority((int)spawn.X/32,(int)spawn.Z/32,new ChunkLoadOptions{KeepLoaded=true,OnLoaded=()=>api.Event.EnqueueMainThreadTask(()=>Check(api),"nos-entity-validation")});
   return TextCommandResult.Success("NOS_ENTITY_CHECK_QUEUED");
  });
 }
 private static void Check(ICoreServerAPI api)
 {
  string? original=null;AStar? astar=null;var checks=new List<string>();
  void Assert(bool yes,string why){if(!yes)throw new Exception(why);checks.Add(why);}
  JsonElement Control(string operation,string settings="{}")
  {
   var tree=new TreeAttribute();tree.SetString("operation",operation);tree.SetString("settings",settings);
   api.Event.PushEvent("newfiesse:optimizer-control-v1",tree);
   if(!tree.GetBool("success"))throw new Exception(tree.GetString("message"));
   return JsonSerializer.Deserialize<JsonElement>(tree.GetString("snapshot"));
  }
  try
  {
   original=Control("get").GetProperty("settings").GetRawText();
   var before=Control("get");
   Assert(before.GetProperty("settings").GetProperty("broadcastFilterReuse").GetBoolean()&&before.GetProperty("settings").GetProperty("entityLifecycleListReuse").GetBoolean(),"new allocation controls enabled through Suite for real-server validation");
   Assert(before.GetProperty("counters").GetProperty("entityCleanupPasses").GetInt64()>0,"actual server entity cleanup passes use bounded scopes");
   foreach(var key in new[]{"blockUpdateListReuse","chunkLoadRequestListReuse","chunkUnloadListReuse"})Assert(before.GetProperty("settings").GetProperty(key).GetBoolean(),"block/chunk control enabled: "+key);
   foreach(var key in new[]{"blockUpdatePasses","chunkRequestPasses","chunkUnloadCandidatePasses"})Assert(before.GetProperty("counters").GetProperty(key).GetInt64()>0,"normal copied-server work executes "+key);
   foreach(var key in new[]{"delayedCallbackSnapshotReuse","spawnOpportunityListReuse","spawnColumnArrayReuse"})Assert(before.GetProperty("settings").GetProperty(key).GetBoolean(),"callback/spawn control enabled: "+key);
   Assert(before.GetProperty("counters").GetProperty("callbackSnapshotPasses").GetInt64()+before.GetProperty("counters").GetProperty("callbackDebugSnapshotPasses").GetInt64()>0,"copied-server normal/debug callbacks use snapshot scopes");
   var world=api.World;const BindingFlags networkFlags=BindingFlags.NonPublic|BindingFlags.Instance;
   var rawBroadcast=world.GetType().GetMethod("BroadcastArbitraryPacket",networkFlags,null,new[]{typeof(byte[]),typeof(IServerPlayer[])},null)!;
   var packetBroadcast=world.GetType().GetMethod("BroadcastArbitraryPacket",networkFlags,null,new[]{typeof(Packet_Server),typeof(IServerPlayer[])},null)!;
   var udpBroadcast=world.GetType().GetMethod("BroadcastArbitraryUdpPacket",networkFlags,null,new[]{typeof(Packet_UdpPacket),typeof(IServerPlayer[])},null)!;
   rawBroadcast.Invoke(world,new object[]{new byte[]{1},Array.Empty<IServerPlayer>()});
   packetBroadcast.Invoke(world,new object[]{new Packet_Server{Id=2,Ping=new()},Array.Empty<IServerPlayer>()});
   udpBroadcast.Invoke(world,new object[]{new Packet_UdpPacket(),Array.Empty<IServerPlayer>()});
   var broadcastCounters=Control("get").GetProperty("counters");
   Assert(broadcastCounters.GetProperty("broadcastRawCalls").GetInt64()>before.GetProperty("counters").GetProperty("broadcastRawCalls").GetInt64()&&broadcastCounters.GetProperty("broadcastPacketCalls").GetInt64()>before.GetProperty("counters").GetProperty("broadcastPacketCalls").GetInt64()&&broadcastCounters.GetProperty("broadcastUdpCalls").GetInt64()>before.GetProperty("counters").GetProperty("broadcastUdpCalls").GetInt64(),"three stock broadcast overloads execute empty-exclusion fast paths on copied server");
   foreach(var key in new[]{"collisionTraversalReuse","containerTransitionScan","containerTickTraversal"})Assert(before.GetProperty("settings").GetProperty(key).GetBoolean(),"traversal control enabled: "+key);
   foreach(var key in new[]{"nearbyEntityListReuse","cuboidEntityListReuse","shapelessStackMerge","shapelessWildcardMatch"})Assert(before.GetProperty("settings").GetProperty(key).GetBoolean(),"query/recipe control enabled: "+key);
   var spawn=world.DefaultSpawnPosition;
   var queryPos=new Vec3d(spawn.X,spawn.Y,spawn.Z);var queryStart=new BlockPos((int)spawn.X-8,(int)spawn.Y-8,(int)spawn.Z-8);var queryEnd=new BlockPos((int)spawn.X+8,(int)spawn.Y+8,(int)spawn.Z+8);
   var heldEntities=world.GetEntitiesAround(queryPos,8,8);var heldEntityIds=heldEntities.Select(e=>e.EntityId).ToArray();world.GetEntitiesInsideCuboid(queryStart,queryEnd);world.GetEntitiesAround(queryPos,8,8);
   Assert(heldEntities.Select(e=>e.EntityId).SequenceEqual(heldEntityIds),"actual query results remain owned across subsequent queries");
   var stick=world.GetItem(new AssetLocation("game:stick"));Assert(stick!=null,"real vanilla recipe input resolved");
   var recipeSlots=new[]{new ItemSlot(null){Itemstack=new ItemStack(stick!,2)},new ItemSlot(null){Itemstack=new ItemStack(stick!,1)}};
   var exactIngredient=new CraftingRecipeIngredient{Type=EnumItemClass.Item,Code=new("game:stick"),MatchingType=EnumRecipeMatchType.Exact,Quantity=3,ResolvedItemStack=new ItemStack(stick!,3)};
   var wildcardIngredient=new CraftingRecipeIngredient{Type=EnumItemClass.Item,Code=new("game:st*"),MatchingType=EnumRecipeMatchType.Wildcard,Quantity=2};
   var recipeHelper=typeof(RecipeBase).GetMethod("MatchesShapeLess",BindingFlags.NonPublic|BindingFlags.Instance)!;var recipeFixture=new GridRecipe();
   bool RecipeMatch(IRecipeIngredient ingredient)=>(bool)recipeHelper.Invoke(recipeFixture,new object[]{recipeSlots,world,new IRecipeIngredient[]{ingredient}})!;
   Control("set","{\"shapelessStackMerge\":false,\"shapelessWildcardMatch\":false}");
   bool exactStock=RecipeMatch(exactIngredient),wildcardStock=RecipeMatch(wildcardIngredient);
   Control("set","{\"shapelessStackMerge\":true,\"shapelessWildcardMatch\":true}");
   Assert(exactStock&&wildcardStock&&RecipeMatch(exactIngredient)==exactStock&&RecipeMatch(wildcardIngredient)==wildcardStock,"complete shapeless exact/wildcard matches equal native with real game items");
   Assert(recipeSlots[0].Itemstack!.StackSize==2&&recipeSlots[1].Itemstack!.StackSize==1,"recipe checks leave real input stacks unchanged");
   var recipeCounters=Control("get").GetProperty("counters");
   foreach(var key in new[]{"nearbyEntityListPasses","cuboidEntityListPasses","shapelessMergeCalls","shapelessWildcardCalls"})Assert(recipeCounters.GetProperty(key).GetInt64()>0,"copied-server query/recipe helper executes: "+key);
   foreach(var key in new[]{"entityQueryFailures","shapelessMergeFailures","shapelessWildcardFailures"})Assert(recipeCounters.GetProperty(key).GetInt64()==0,"copied-server query/recipe failure counter remains zero: "+key);
   // Enclosed room wholly inside the already loaded spawn column. The open-world
   // fixture can legitimately return before ticking when a room reaches unloaded chunks.
   int roomX=(int)spawn.X/32*32+20,roomZ=(int)spawn.Z/32*32+20;
   int roomY=world.BlockAccessor.GetTerrainMapheightAt(new BlockPos(roomX,0,roomZ))+8;
   var roomRock=world.GetBlock(new AssetLocation("game:rock-granite"));Assert(roomRock!=null,"container fixture wall resolved");
   for(int rx=roomX-2;rx<=roomX+2;rx++)for(int rz=roomZ-2;rz<=roomZ+2;rz++)for(int ry=roomY-1;ry<=roomY+2;ry++)
    world.BlockAccessor.SetBlock(rx==roomX-2||rx==roomX+2||rz==roomZ-2||rz==roomZ+2||ry==roomY-1||ry==roomY+2?roomRock!.Id:0,new BlockPos(rx,ry,rz));
   var containerInventory=new InventoryGeneric(4,"nos-fixture-0",api);containerInventory[0].Itemstack=new ItemStack(stick!,2);
   var containerFixture=new Vintagestory.GameContent.InWorldContainer(()=>containerInventory,"nos-fixture");
   containerFixture.Init(api,()=>new BlockPos(roomX,roomY,roomZ),()=>{});containerFixture.OnTick(1);
   Assert(containerInventory[0].Itemstack!.StackSize==2,"native nonperishable container tick leaves real inventory unchanged");
   // Exercise the native transition loop with a real perishable item if resolved.
   var food=world.GetItem(new AssetLocation("game:vegetable-cabbage"));
   Assert(food!=null,"real perishable item resolved");containerInventory[0].Itemstack=new ItemStack(food!,1);
   Assert(food!.RequiresTransitionableTicking(world,containerInventory[0].Itemstack),"real perishable item requests a transition tick");containerFixture.OnTick(1);
   Assert(containerFixture.Room!=null&&containerFixture.Room.AnyChunkUnloaded==0,"enclosed room passes native chunk-loading guard");
   var collisionEntity=new Vintagestory.API.Common.EntityAgent{World=world,CollisionBox=new Cuboidf(-.2f,0,-.2f,.2f,.8f,.2f)};
   var collisionPosition=new Vintagestory.API.Common.Entities.EntityPos(spawn.X,spawn.Y,spawn.Z);var collidedPosition=new Vec3d();
   new CollisionTester().ApplyTerrainCollision(collisionEntity,collisionPosition,1,ref collidedPosition);
   Assert(collidedPosition.X==collisionPosition.X&&collidedPosition.Y==collisionPosition.Y&&collidedPosition.Z==collisionPosition.Z,"zero-motion native collision still runs and preserves position on copied terrain");
   foreach(var key in new[]{"collisionGenerationReuse","cachedCollisionGenerationReuse","animationCollisionFlagScan"})Assert(before.GetProperty("settings").GetProperty(key).GetBoolean(),"generation/animation control enabled: "+key);
   var cachedCollision=new CachingCollisionTester();cachedCollision.NewTick(collisionPosition);cachedCollision.ApplyTerrainCollision(collisionEntity,collisionPosition,1,ref collidedPosition);
   var animationManager=new AnimationManager();animationManager.Init(api,new EntityAgent{World=world,Api=api});animationManager.Animator=new ServerAnimator(()=>1.0,Array.Empty<Animation>(),Array.Empty<ShapeElement>(),new Dictionary<int,AnimationJoint>());animationManager.ActiveAnimationsByAnimCode.Add("nos-fixture",new AnimationMetaData{AdjustCollisionBox=true});animationManager.OnServerTick(.02f);
   Assert(animationManager.AdjustCollisionBoxToAnimation,"native ServerAnimator frame plus collision flag scan execute");
   var generationCounters=Control("get").GetProperty("counters");
   foreach(var key in new[]{"collisionGenerationCalls","cachedCollisionGenerationCalls","animationCollisionScans"})Assert(generationCounters.GetProperty(key).GetInt64()>0,"native generation/animation helper executes: "+key);
   foreach(var key in new[]{"collisionGenerationFailures","animationScanFailures"})Assert(generationCounters.GetProperty(key).GetInt64()==0,"generation/animation failure counter zero: "+key);
   var traversalCounters=Control("get").GetProperty("counters");
   foreach(var key in new[]{"collisionTraversalCalls","containerTransitionScans","containerTickTraversals"})Assert(traversalCounters.GetProperty(key).GetInt64()>0,"real terrain/container loop executes: "+key);
   foreach(var key in new[]{"collisionTraversalFailures","containerScanFailures","containerTickFailures"})Assert(traversalCounters.GetProperty(key).GetInt64()==0,"traversal failure counter remains zero: "+key);
   astar=new AStar(api);var queue=new PathfindingAsync();
   const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
   typeof(PathfindingAsync).GetField("api",flags)!.SetValue(queue,api);
   typeof(PathfindingAsync).GetField("astar_mainthread",flags)!.SetValue(queue,astar);
   int x=(int)spawn.X/32*32+8,z=(int)spawn.Z/32*32+8;
   int y=api.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos(x,0,z))+1;
   var start=new BlockPos(x,y,z);var target=start.AddCopy(2,0,0);
   PathfinderTask[] Seed(int count){queue.PathfinderTasks.Clear();var tasks=Enumerable.Range(0,count).Select(i=>new PathfinderTask(start.Copy(),target.Copy(),0,3,.6f,new Cuboidf(-.2f,0,-.2f,.2f,.8f,.2f),128,0,Enum.GetValues<EnumAICreatureType>()[i%Enum.GetValues<EnumAICreatureType>().Length])).ToArray();foreach(var task in tasks)queue.PathfinderTasks.Enqueue(task);return tasks;}
   Control("set","{\"pathQueueLimitFix\":false,\"pathMainThreadBudget\":false}");
   var stock=Seed(101);queue.ProcessQueue(astar,100);
   Assert(stock.Count(t=>t.Finished)==100&&queue.PathfinderTasks.Count==0&&!stock[^1].Finished,"stock dequeue-before-limit behavior reproduced on real terrain");
   Control("set","{\"pathQueueLimitFix\":true}");
   var repaired=Seed(101);queue.ProcessQueue(astar,100);
   Assert(repaired.Count(t=>t.Finished)==100&&queue.PathfinderTasks.Count==1,"worker leaves capped request queued");
   queue.ProcessQueue(astar,100);Assert(repaired.All(t=>t.Finished),"101 of 101 actual A* requests complete");
   Control("set","{\"pathMainThreadBudget\":true,\"pathMainThreadBudgetMs\":2,\"pathMainThreadMaxSearches\":1,\"pathMainThreadGraceMs\":0}");
   var help=Seed(3);var first=help[0];var expected=astar.FindPathAsWaypoints(first.startBlockPos,first.targetBlockPos,first.modeMinFleeDistance,first.maxFallHeight,first.stepHeight,first.collisionBox,first.searchDepth,first.mhdistanceTolerance,first.CreatureType);
   typeof(PathfindingAsync).GetMethod("OnMainThreadTick",flags)!.Invoke(queue,new object[]{.02f});
   Assert(help.Count(t=>t.Finished)==1&&queue.PathfinderTasks.Count==2,"real main-thread assistance respects search count");
   Assert((expected==null&&first.waypoints==null)||(expected!=null&&first.waypoints!=null&&expected.Count==first.waypoints.Count&&expected.Zip(first.waypoints).All(p=>p.First.X==p.Second.X&&p.First.Y==p.Second.Y&&p.First.Z==p.Second.Z)),"assisted path equals direct official A* for this terrain case");
   // Controlled flat arena in the disposable copied world; real official terrain solver.
   int arenaY=y+5;var floor=api.World.GetBlock(new AssetLocation("game:rock-granite"));Assert(floor!=null,"arena floor resolved");
   for(int ax=x-2;ax<=x+14;ax++)for(int az=z-2;az<=z+2;az++)
   {api.World.BlockAccessor.SetBlock(floor!.Id,new BlockPos(ax,arenaY-1,az));for(int ay=arenaY;ay<arenaY+3;ay++)api.World.BlockAccessor.SetBlock(0,new BlockPos(ax,ay,az));}
   var origin=new BlockPos(x,arenaY,z);var end=new BlockPos(x+10,arenaY,z);var box=new Cuboidf(-.2f,0,-.2f,.2f,.8f,.2f);
   List<Vec3d>? Solve()=>astar.FindPathAsWaypoints(origin,end,-1,3,.6f,box,512,0,EnumAICreatureType.Default);
   string Geometry(List<Vec3d>? path)=>path==null?"null":string.Join(";",path.Select(p=>$"{Math.Floor(p.X)},{p.Y},{Math.Floor(p.Z)}"));
   Control("set","{\"waypointNodePooling\":false}");var expectedGeometry=Geometry(Solve());Assert(expectedGeometry!="null"&&Solve()!.Count>3,"nontrivial real waypoint path found");
   var raw=astar.FindPath(origin,end,3,.6f,box,512);Assert(raw!=null&&raw.Count>3,"raw official path retained for ownership check");
   string RawState()=>string.Join(";",raw!.Select(p=>$"{p.X},{p.Y},{p.Z},{p.dimension},{p.gCost},{p.hCost},{p.pathLength},{p.HeapIndex}"));string rawBefore=RawState();
   (long Allocated,double Elapsed) Measure(){for(int i=0;i<20;i++)Solve();long before=GC.GetAllocatedBytesForCurrentThread();var sw=Stopwatch.StartNew();for(int i=0;i<5000;i++)Solve();sw.Stop();return(GC.GetAllocatedBytesForCurrentThread()-before,sw.Elapsed.TotalMilliseconds);}
   var stockMeasure=Measure();var active=Control("set","{\"waypointNodePooling\":true}");
   Assert(active.GetProperty("modules").EnumerateArray().Where(m=>m.GetProperty("name").GetString() is "waypointNodePooling" or "waypointNodePoolScope").All(m=>m.GetProperty("status").GetString()=="active"),"both pool components activate on official server");
   var pooledMeasure=Measure();Assert(Geometry(Solve())==expectedGeometry,"repeated pooled waypoints preserve route geometry");Assert(RawState()==rawBefore,"held raw-node path unchanged by later pooled searches");
   Assert(pooledMeasure.Allocated<stockMeasure.Allocated,"pooled repeated waypoint search reduces measured thread allocation in this arena");
   var trials=new List<object>{new{order="stock/pooled",stock=stockMeasure.Allocated,pooled=pooledMeasure.Allocated,stockMs=stockMeasure.Elapsed,pooledMs=pooledMeasure.Elapsed}};
   for(int trial=0;trial<2;trial++)
   {
    (long Allocated,double Elapsed) stockTrial,pooledTrial;
    if(trial==0){Control("set","{\"waypointNodePooling\":true}");pooledTrial=Measure();Control("set","{\"waypointNodePooling\":false}");stockTrial=Measure();}
    else{Control("set","{\"waypointNodePooling\":false}");stockTrial=Measure();Control("set","{\"waypointNodePooling\":true}");pooledTrial=Measure();}
    Assert(pooledTrial.Allocated<stockTrial.Allocated,"repeat allocation comparison "+trial);
    trials.Add(new{order=trial==0?"pooled/stock":"stock/pooled",stock=stockTrial.Allocated,pooled=pooledTrial.Allocated,stockMs=stockTrial.Elapsed,pooledMs=pooledTrial.Elapsed});
   }
   Assert(RawState()==rawBefore,"held raw path unchanged after repeated pool recreation");
   api.Logger.Notification("{0}","NOS_WAYPOINT_MEASUREMENTS "+JsonSerializer.Serialize(new{searches=5000,trials,scope="Repeated controlled-arena path searches on copied server; not production TPS evidence."}));
   api.Logger.Notification("{0}","NOS_ENTITY_INTEGRATION "+JsonSerializer.Serialize(new{success=true,checks,scope="Real terrain/official solver correctness; isolated queues, not player-performance evidence."}));
  }
  catch(Exception error){api.Logger.Error("{0}","NOS_ENTITY_INTEGRATION "+JsonSerializer.Serialize(new{success=false,checks,error=error.ToString()}));}
  finally
  {
   astar?.Dispose();
   if(original!=null)try{Control("set",original);}catch(Exception error){api.Logger.Error("NOS_ENTITY_RESTORE_FAILED "+error.GetType().Name);}
  }
 }
}
