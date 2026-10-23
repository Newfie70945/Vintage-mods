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
   astar=new AStar(api);var queue=new PathfindingAsync();
   const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
   typeof(PathfindingAsync).GetField("api",flags)!.SetValue(queue,api);
   typeof(PathfindingAsync).GetField("astar_mainthread",flags)!.SetValue(queue,astar);
   var spawn=api.World.DefaultSpawnPosition;int x=(int)spawn.X/32*32+8,z=(int)spawn.Z/32*32+8;
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
