using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.Server;

// Laboratory fixture only: real stock animal AI, forced-active so it can run
// without authenticated players. The population and callback subscriber are
// synthetic controls, not representative players or bot capacity evidence.
public sealed class ModpackProbe:ModSystem
{
 private readonly List<Entity> animals=[];
 private long moves,vetoCalls,taskStarts,filterCalls;
 private readonly List<(double X,double Y,double Z)> positions=[];
 private static ModpackProbe? active;
 private readonly List<double> ticks=new(18000);
 private long lastTick;
 private int lastIndex;
 private bool collecting;
 private Harmony? laboratoryHooks;

 public override bool ShouldLoad(EnumAppSide side)=>side==EnumAppSide.Server;
 public override void StartServerSide(ICoreServerAPI api)
 {
  void Emit(string tag,object value)=>api.Logger.Notification("{0}","NOSLOAD_"+tag+" "+JsonSerializer.Serialize(value));
  active=this;api.Event.RegisterGameTickListener(_=>Tick((ServerMain)api.World),1);
  // The stock worker skips physics for untracked NPCs when no clients exist.
  // Force ONLY fixture-tagged NPCs through the ordinary worker, without fake
  // client records or authentication changes. This is a lab control, not NOS.
  laboratoryHooks=new("nos.lab.tracked-npcs");
  laboratoryHooks.Patch(AccessTools.Method(typeof(PhysicsManager),"UpdateTrackedEntityState"),postfix:new HarmonyMethod(typeof(ModpackProbe),nameof(TrackLabEntity)));
  object Field(object o,string n)=>o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.GetValue(o)!;
  object Health(){var t=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("BlockLogger.BlockLoggerModSystem")).First(x=>x!=null)!;var system=api.ModLoader.GetModSystem(t.FullName!);var module=Field(system,"module");return module.GetType().GetProperty("ServerHealth")!.GetValue(module)!;}
  void Add(string name,System.Func<TextCommandResult> handler)=>api.ChatCommands.Create(name).RequiresPrivilege(Privilege.controlserver).HandleWith(_=>{try{return handler();}catch(Exception e){api.Logger.Error("NOSLOAD_FAIL "+e);return TextCommandResult.Error(e.Message);}});
  Add("nosloadmods",()=>{Emit("MODS",api.ModLoader.Mods.Where(m=>m.Info!=null).Select(m=>new{id=m.Info.ModID,version=m.Info.Version,enabled=api.ModLoader.IsModEnabled(m.Info.ModID)}).ToArray());return TextCommandResult.Success();});
  Add("nosloadentities",()=>{
   api.World.Config.SetBool("runAiTasks",true);
   var spawn=api.World.DefaultSpawnPosition;int x=(int)(spawn.X/32)*32+160*32+64,z=(int)(spawn.Z/32)*32+160*32+64;
   var types=api.World.EntityTypes.Where(t=>t.Code.Domain=="game"&&t.Code.Path.StartsWith("pig-eurasian-adult-")).ToArray();
   if(types.Length==0)throw new Exception("Stock wild-pig type absent");
   int requested=int.TryParse(Environment.GetEnvironmentVariable("NOS_LAB_NPC_COUNT"),out int supplied)?Math.Clamp(supplied,128,2048):128;
   int columns=requested==128?16:(int)Math.Ceiling(Math.Sqrt(requested));
   for(int i=0;i<requested;i++){
    double px=x+(i%columns)*2+.5,pz=z+(i/columns)*2+.5;
    var pos=new BlockPos((int)px,0,(int)pz);int height=api.World.BlockAccessor.GetTerrainMapheightAt(pos);
    var entity=api.World.ClassRegistry.CreateEntity(types[i%types.Length]);entity.Pos.SetPos(px,height+1,pz);entity.AlwaysActive=true;entity.Attributes.SetBool("nosLaboratoryNpc",true);api.World.SpawnEntity(entity);
    if(entity.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager is {} manager)
    {
     manager.OnExecuteTask+=(task,previous)=>{Interlocked.Increment(ref vetoCalls);return true;};
     manager.OnShouldExecuteTask+=task=>{Interlocked.Increment(ref filterCalls);return true;};
     manager.OnTaskStarted+=task=>Interlocked.Increment(ref taskStarts);
    }
    animals.Add(entity);positions.Add((entity.Pos.X,entity.Pos.Y,entity.Pos.Z));
   }
   Emit("ENTITIES",new{spawned=animals.Count,types=types.Select(t=>t.Code.ToString()).ToArray(),aiEnabled=AiRuntimeConfig.RunAiTasks,behaviors=animals[0].SidedProperties.Behaviors.Select(b=>b.GetType().FullName).ToArray(),tasks=animals[0].GetBehavior<EntityBehaviorTaskAI>()?.TaskManager.AllTasks.Count});return TextCommandResult.Success();
  });
  Add("nosloadstate",()=>{
   using var process=Process.GetCurrentProcess();var gc=GC.GetGCMemoryInfo();
   collecting=false;var sorted=ticks.Order().ToArray();double? Percent(double p)=>sorted.Length==0?null:sorted[(int)Math.Min(sorted.Length-1,Math.Ceiling(p*sorted.Length)-1)];
   Emit("STATE",new{engineProfilerEnabled=api.World.FrameProfiler.Enabled,slowTickProfilingEnabled=api.World.FrameProfiler.PrintSlowTicks,aiEnabled=AiRuntimeConfig.RunAiTasks,spawned=animals.Count,alive=animals.Count(e=>e.Alive),active=animals.Count(e=>e.State==EnumEntityState.Active),loaded=api.World.LoadedEntities.Count,moves,vetoCalls,taskStarts,filterCalls,
    tickCount=sorted.Length,tickAverageMs=sorted.Length==0?0:sorted.Average(),tickP95Ms=Percent(.95),tickP99Ms=Percent(.99),tickWorstMs=sorted.Length==0?0:sorted[^1],
    allocatedBytes=GC.GetTotalAllocatedBytes(false),cpuMs=process.TotalProcessorTime.TotalMilliseconds,rssBytes=process.WorkingSet64,heapBytes=GC.GetTotalMemory(false),gcCommittedBytes=gc.TotalCommittedBytes,gcFragmentedBytes=gc.FragmentedBytes,gcPauseMs=GC.GetTotalPauseDuration().TotalMilliseconds});return TextCommandResult.Success();
  });
  Add("nosloadreset",()=>{ticks.Clear();collecting=true;Emit("RESET",new{success=true});return TextCommandResult.Success();});
  Add("nosloadhealth",()=>{var h=Health();h.GetType().GetMethod("SetEnabled")!.Invoke(h,[true]);h.GetType().GetMethod("SetAutomaticIncidentProfilingEnabled")!.Invoke(h,[false]);Emit("HEALTH",new{success=true});return TextCommandResult.Success();});
  Add("nosloadprofile",()=>{
   var health=Health();var args=new object[]{3,true,""};
   bool ok=(bool)health.GetType().GetMethod("StartProfile",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(health,args)!;
   Emit("PROFILE",new{success=ok,message=args[2]});return ok?TextCommandResult.Success():TextCommandResult.Error(args[2].ToString());
  });
  Add("nosloadprofileoff",()=>{var health=Health();health.GetType().GetMethod("SetAutomaticIncidentProfilingEnabled")!.Invoke(health,[false]);Emit("PROFILE_OFF",new{success=true});return TextCommandResult.Success();});
  Add("nosloadcanceltest",()=>{
   var h=Health();var type=h.GetType();var args=new object[]{3,true,""};
   if(!(bool)type.GetMethod("StartProfile",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(h,args)!)throw new Exception("Automatic cancellation fixture could not start");
   type.GetMethod("SetAutomaticIncidentProfilingEnabled")!.Invoke(h,[false]);
   if((bool)Field(h,"profileRunning"))throw new Exception("Disabling automatic captures did not stop active automatic capture");
   var manual=new object[]{5,""};if(!(bool)type.GetMethod("StartManualProfile")!.Invoke(h,manual)!)throw new Exception("Manual capture could not start");
   type.GetMethod("SetAutomaticIncidentProfilingEnabled")!.Invoke(h,[false]);
   if(!(bool)Field(h,"profileRunning"))throw new Exception("Automatic disable stopped manual capture");
   api.Event.RegisterCallback(_=>{
    bool running=(bool)Field(h,"profileRunning");
    type.GetMethod("FinishManualProfileSafe",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(h,null);
    Emit(running?"CANCEL_PASS":"FAIL",new{staleAutomaticTimerPreservedNewManualCapture=running});
   },3500);
   return TextCommandResult.Success();
  });
  api.Event.RegisterGameTickListener(_=>{for(int i=0;i<animals.Count;i++){var e=animals[i];var p=positions[i];double dx=e.Pos.X-p.X,dy=e.Pos.Y-p.Y,dz=e.Pos.Z-p.Z;if(e.Alive&&dx*dx+dy*dy+dz*dz>0.000001)moves++;positions[i]=(e.Pos.X,e.Pos.Y,e.Pos.Z);}},1000);
 }
 public static void Tick(ServerMain __instance)
 {
  var owner=active;if(owner?.collecting!=true)return;int index=__instance.StatsCollectorIndex;var stats=__instance.StatsCollector[index];
  if((owner.lastIndex!=index||owner.lastTick!=stats.ticksTotal)&&stats.ticksTotal>0&&owner.ticks.Count<18000){int last=(stats.tickTimeIndex+stats.tickTimes.Length-1)%stats.tickTimes.Length;owner.ticks.Add(stats.tickTimes[last]);}
  owner.lastIndex=index;owner.lastTick=stats.ticksTotal;
 }
 public static void TrackLabEntity(Entity entity){if(entity.Attributes.GetBool("nosLaboratoryNpc")){entity.IsTracked=2;entity.NearestPlayerDistance=0;}}
 public override void Dispose(){collecting=false;active=null;laboratoryHooks?.UnpatchAll(laboratoryHooks.Id);}
}
