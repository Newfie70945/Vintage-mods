using System.Reflection;
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
