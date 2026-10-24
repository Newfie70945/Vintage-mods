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
 private delegate bool VariantMatch(RecipeBase recipe,List<ItemStack> stacks,IRecipeIngredient?[] ingredients);
 private readonly List<Entity> animals=[];
 private long moves,vetoCalls,taskStarts,filterCalls;
 private static long barrelTicks,firepitTicks;
 private readonly List<BlockEntity> laboratoryBlocks=[];
 private readonly List<(double X,double Y,double Z)> positions=[];
 private static ModpackProbe? active;
 private readonly List<double> ticks=new(18000);
 private long lastTick;
 private int lastIndex;
 private bool collecting;
 private Harmony? laboratoryHooks;
 private static int BlockCount=>int.TryParse(Environment.GetEnvironmentVariable("NOS_LAB_BLOCK_COUNT"),out int n)?Math.Clamp(n,1,1000):100;

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
  Add("nosloadoptimizer",()=>{
   var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("NewfiesOptimizer.OptimizerMod")).FirstOrDefault(t=>t!=null);
   if(type==null){Emit("OPTIMIZER_STATE",new{installed=false,modules=Array.Empty<object>(),counters=new Dictionary<string,long>(),profileRunning=false});return TextCommandResult.Success();}
   var system=api.ModLoader.GetModSystem(type.FullName!);var engine=Field(system,"optimizations");var profiler=Field(system,"profiler");
   Emit("OPTIMIZER_STATE",new{installed=true,modules=engine.GetType().GetMethod("Modules")!.Invoke(engine,null),counters=engine.GetType().GetMethod("Counters")!.Invoke(engine,null),profileRunning=(bool)profiler.GetType().GetProperty("IsRunning")!.GetValue(profiler)!});return TextCommandResult.Success();
  });
  Add("nosloadvariantstress",()=>{
   var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("NewfiesOptimizer.Optimizations.RecipeOptimizations")).First(t=>t!=null)!;
   var configure=type.GetMethod("Configure")!;
   var match=AccessTools.Method(typeof(RecipeBase),"MatchWildcardIngredients").CreateDelegate<VariantMatch>();
   var item=api.World.GetItem(new AssetLocation("game:stick"));
   var stack=new ItemStack(item,64);stack.Attributes.SetString("nosfixture","untouched");
   var recipe=new GridRecipe();IRecipeIngredient?[] ingredients={new CraftingRecipeIngredient{Code=new("game:st*"),Type=EnumItemClass.Item,Quantity=1,MatchingType=EnumRecipeMatchType.Wildcard,AllowedVariants=new[]{"ick"}}};
   var input=new List<ItemStack>(1);var measurements=new List<object>();
   try
   {
    foreach(bool on in new[]{false,true,true,false})
    {
     configure.Invoke(null,new object[]{1,on});
     void Check(){input.Clear();input.Add(stack);if(!match(recipe,input,ingredients)||input.Count!=0||stack.StackSize!=64||stack.Attributes.GetString("nosfixture")!="untouched")throw new Exception("Variant match changed input/result");}
     for(int i=0;i<10000;i++)Check();
     const int n=100000;long bytes=GC.GetAllocatedBytesForCurrentThread();var timer=Stopwatch.StartNew();
     for(int i=0;i<n;i++)Check();
     measurements.Add(new{enabled=on,queries=n,nsPerMatch=timer.Elapsed.TotalNanoseconds/n,bytesPerMatch=(GC.GetAllocatedBytesForCurrentThread()-bytes)/(double)n});
    }
   }
   finally{configure.Invoke(null,new object[]{1,true});}
   Emit("VARIANT_STRESS",new{measurements,stackSize=stack.StackSize,attributesPreserved=true,scope="Native server helper and real stick item; scratch stacks only, no player inventories or production TPS claim."});return TextCommandResult.Success();
  });
  Add("nosloadmods",()=>{Emit("MODS",api.ModLoader.Mods.Where(m=>m.Info!=null).Select(m=>new{id=m.Info.ModID,version=m.Info.Version,enabled=api.ModLoader.IsModEnabled(m.Info.ModID)}).ToArray());return TextCommandResult.Success();});
  Add("nosloadblocks",()=>{
   var spawn=api.World.DefaultSpawnPosition;int x=(int)(spawn.X/32)*32+160*32+8,z=(int)(spawn.Z/32)*32+160*32+8;
   var stone=api.World.Blocks.First(b=>b?.Code?.ToString()=="game:rock-granite");
   var barrel=api.World.Blocks.First(b=>b?.EntityClass=="Barrel"&&b.Code.Domain=="game");
   var fire=api.World.GetBlock(new AssetLocation("game:firepit-cold"));
   if(fire==null||fire.Id==0)throw new Exception("Official firepit absent");
   int blockCount=BlockCount,grid=(int)Math.Ceiling(Math.Sqrt(blockCount*2));
   for(int i=0;i<blockCount*2;i++){
    var p=new BlockPos(x+(i%grid)*2,0,z+(i/grid)*2);p.Y=api.World.BlockAccessor.GetTerrainMapheightAt(p)+1;
    api.World.BlockAccessor.SetBlock((i<blockCount?barrel:fire).Id,p);
    var be=api.World.BlockAccessor.GetBlockEntity(p)??throw new Exception("Placed block entity absent");laboratoryBlocks.Add(be);
    if(be is BlockEntityBarrel b){b.Inventory[0].Itemstack=new ItemStack(stone);}
    if(be is BlockEntityFirepit f&&i%2==0){f.fuelBurnTime=600;f.maxFuelBurnTime=600;f.maxTemperature=700;f.furnaceTemperature=600;}
   }
   laboratoryHooks!.Patch(AccessTools.Method(typeof(BlockEntityBarrel),"OnEvery3Second"),postfix:new HarmonyMethod(typeof(ModpackProbe),nameof(ObserveBarrel)));
   laboratoryHooks.Patch(AccessTools.Method(typeof(BlockEntityFirepit),"OnBurnTick"),postfix:new HarmonyMethod(typeof(ModpackProbe),nameof(ObserveFirepit)));
   Emit("BLOCKS",new{barrels=blockCount,firepits=blockCount,burningFirepits=blockCount/2,coldFirepits=(blockCount+1)/2,barrelInput="rock-granite (intentional recipe mismatch)"});return TextCommandResult.Success();
  });
  Add("nosloadentities",()=>{
   api.World.Config.SetBool("runAiTasks",true);
   var spawn=api.World.DefaultSpawnPosition;int x=(int)(spawn.X/32)*32+160*32+64,z=(int)(spawn.Z/32)*32+160*32+64;
   var prefixes=(Environment.GetEnvironmentVariable("NOS_LAB_SPECIES")??"game:pig-eurasian-adult-").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
   var groups=prefixes.Select(prefix=>api.World.EntityTypes.Where(t=>t.Code.ToString().StartsWith(prefix,StringComparison.Ordinal)&&t.Code.Path.Contains("adult")).OrderBy(t=>t.Code.ToString(),StringComparer.Ordinal).ToArray()).ToArray();
   if(groups.Any(g=>g.Length==0))throw new Exception("Requested adult species prefix absent");
   var types=groups.SelectMany(g=>g).ToArray();
   if(BlockCount>100){x+=64;z+=64;}
   int requested=int.TryParse(Environment.GetEnvironmentVariable("NOS_LAB_NPC_COUNT"),out int supplied)?Math.Clamp(supplied,128,4096):128;
   int columns=requested==128?16:(int)Math.Ceiling(Math.Sqrt(requested));
   for(int i=0;i<requested;i++){
    double px=x+(i%columns)*2+.5,pz=z+(i/columns)*2+.5;
    var pos=new BlockPos((int)px,0,(int)pz);int height=api.World.BlockAccessor.GetTerrainMapheightAt(pos);
    var group=groups[i%groups.Length];
    var entity=api.World.ClassRegistry.CreateEntity(group[(i/groups.Length)%group.Length]);entity.Pos.SetPos(px,height+1,pz);entity.AlwaysActive=true;entity.Attributes.SetBool("nosLaboratoryNpc",true);api.World.SpawnEntity(entity);
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
  Add("nosloadprofiledata",()=>{
   var health=Health();var snapshot=health.GetType().GetMethod("GetSnapshot")!.Invoke(health,[32,1800])!;
   var values=new Dictionary<string,object?>();foreach(string name in new[]{"Hotspots","ModHotspots","Phases","ProfileSlowCauses","LastProfile","ProfileRunning"})values[name]=snapshot.GetType().GetProperty(name)!.GetValue(snapshot);
   Emit("PROFILE_DATA",values);return TextCommandResult.Success();
  });
  Add("nosloadresume",()=>{
   if(animals.Count!=0)throw new Exception("Workload already attached; refusing duplicate resume");
   var spawn=api.World.DefaultSpawnPosition;int cx=(int)(spawn.X/32)-4,cz=(int)(spawn.Z/32)-4,completed=0;
   for(int dx=0;dx<8;dx++)for(int dz=0;dz<8;dz++)api.WorldManager.LoadChunkColumnPriority(cx+dx,cz+dz,new ChunkLoadOptions{KeepLoaded=true,OnLoaded=()=>{
    if(Interlocked.Increment(ref completed)!=64)return;
    api.Event.RegisterCallback(_=>{
     foreach(var entity in api.World.LoadedEntities.Values.Where(e=>e.Attributes.GetBool("nosLaboratoryNpc"))){entity.AlwaysActive=true;var before=entity.State;entity.State=EnumEntityState.Active;if(before!=EnumEntityState.Active)entity.OnStateChanged(before);animals.Add(entity);positions.Add((entity.Pos.X,entity.Pos.Y,entity.Pos.Z));}
     laboratoryHooks!.Patch(AccessTools.Method(typeof(BlockEntityBarrel),"OnEvery3Second"),postfix:new HarmonyMethod(typeof(ModpackProbe),nameof(ObserveBarrel)));
     laboratoryHooks.Patch(AccessTools.Method(typeof(BlockEntityFirepit),"OnBurnTick"),postfix:new HarmonyMethod(typeof(ModpackProbe),nameof(ObserveFirepit)));
     Emit("RESUMED",new{animals=animals.Count,loaded=api.World.LoadedEntities.Count});
    },5000);
   }});
   return TextCommandResult.Success("Saved workload loading; no new animals are spawned");
  });
  Add("nosloadspawncheck",()=>{
   var position=api.World.DefaultSpawnPosition;var savegame=Field(api.World,"SaveGameData");var spawnField=AccessTools.Property(savegame.GetType(),"DefaultSpawn")??throw new MissingMemberException(savegame.GetType().FullName,"DefaultSpawn");var saved=spawnField.GetValue(savegame);
   try{if(saved==null)spawnField.SetValue(savegame,Activator.CreateInstance(spawnField.PropertyType));api.WorldManager.SetDefaultSpawnPosition((int)position.X,(int)position.Y,(int)position.Z);}
   finally{spawnField.SetValue(savegame,saved);}
   Emit("SPAWN_CHECK",new{success=true});return TextCommandResult.Success();
  });
  Add("nosloadsetspawn",()=>{
   var spawn=api.World.DefaultSpawnPosition;int x=(int)(spawn.X/32)*32+160*32+128,z=(int)(spawn.Z/32)*32+160*32+128;
   int y=api.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos(x,0,z))+1;
   // The official API writes through DefaultSpawn without constructing it in a
   // never-joined world. Initialize only this disposable world's spawn object.
   var savegame=Field(api.World,"SaveGameData");var spawnField=AccessTools.Property(savegame.GetType(),"DefaultSpawn")??throw new MissingMemberException(savegame.GetType().FullName,"DefaultSpawn");
   if(spawnField.GetValue(savegame)==null)spawnField.SetValue(savegame,Activator.CreateInstance(spawnField.PropertyType));
   api.WorldManager.SetDefaultSpawnPosition(x,y,z);
   Emit("SPAWN",new{x,y,z});return TextCommandResult.Success();
  });
  Add("nosloadstate",()=>{
   using var process=Process.GetCurrentProcess();var gc=GC.GetGCMemoryInfo();
   collecting=false;var sorted=ticks.Order().ToArray();double? Percent(double p)=>sorted.Length==0?null:sorted[(int)Math.Min(sorted.Length-1,Math.Ceiling(p*sorted.Length)-1)];
   Emit("STATE",new{engineProfilerEnabled=api.World.FrameProfiler.Enabled,slowTickProfilingEnabled=api.World.FrameProfiler.PrintSlowTicks,aiEnabled=AiRuntimeConfig.RunAiTasks,spawned=animals.Count,alive=animals.Count(e=>e.Alive),active=animals.Count(e=>e.State==EnumEntityState.Active),loaded=api.World.LoadedEntities.Count,moves,vetoCalls,taskStarts,filterCalls,
    barrelTicks,firepitTicks,blockEntities=laboratoryBlocks.Count,
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
 public static void ObserveBarrel(){Interlocked.Increment(ref barrelTicks);}
 public static void ObserveFirepit(){Interlocked.Increment(ref firepitTicks);}
 public static void TrackLabEntity(Entity entity){if(entity.Attributes.GetBool("nosLaboratoryNpc")){entity.IsTracked=2;entity.NearestPlayerDistance=0;}}
 public override void Dispose(){collecting=false;active=null;laboratoryHooks?.UnpatchAll(laboratoryHooks.Id);}
}
