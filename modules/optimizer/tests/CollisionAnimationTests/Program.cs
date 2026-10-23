using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var d in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(d,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);
 public static T Make<T>(System.Func<MethodInfo,object?[]?,object?> h)where T:class{var p=Create<T,Proxy>();((Proxy)(object)p).Handler=h;return p;}
}
public class ShapeBlock:Block
{
 public Action? Hook;
 public static readonly Cuboidf[] Boxes=[new(0,0,0,1,.5f,1),new(.2f,.5f,.2f,.8f,1,.8f)];
 public override Cuboidf[] GetCollisionBoxes(IBlockAccessor a,BlockPos p){Hook?.Invoke();return Boxes;}
}
static class Checks
{
 static int checks,walks;static bool record=true;static readonly List<string> events=new();
 static readonly ShapeBlock block=new(){BlockId=1};static readonly Harmony own=new("test.nos.generation-animation"),fixture=new("test.nos.generation-animation.fixture");
 static T New<T>()where T:class=>(T)RuntimeHelpers.GetUninitializedObject(typeof(T));
 static void Assert(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 static bool Walk(BlockPos minPos,BlockPos maxPos,Action<Block,int,int,int> onBlock,bool centerOrder)
 {walks++;if(record)events.Add($"walk:{minPos.X},{minPos.Y},{minPos.Z},{minPos.dimension}:{maxPos.X},{maxPos.Y},{maxPos.Z}:{centerOrder}");onBlock(block,minPos.X,minPos.Y,minPos.Z);onBlock(block,maxPos.X,maxPos.Y,maxPos.Z);return false;}
 delegate void Generate(CollisionTester t,IBlockAccessor a,double x,double y,double z,float step,float extra,int dimension);
 delegate void CachedGenerate(CachingCollisionTester t,IBlockAccessor a,double x,double y,double z,float step,float extra,int dimension);
 static Generate Gen(int kind){if(kind==0)return CollisionGenerationOptimizations.Target(0).CreateDelegate<Generate>();var g=CollisionGenerationOptimizations.Target(1).CreateDelegate<CachedGenerate>();return (t,a,x,y,z,step,extra,dimension)=>g((CachingCollisionTester)t,a,x,y,z,step,extra,dimension);}
 static IBlockAccessor Native()=>New<BlockAccessorRelaxed>();
 static CollisionTester Tester(int kind)=>kind==0?new CollisionTester():new CachingCollisionTester();
 static void Mode(bool on)
 {
  own.UnpatchAll(own.Id);for(int i=0;i<2;i++){CollisionGenerationOptimizations.Configure(i,on);if(on)own.Patch(CollisionGenerationOptimizations.Target(i),prefix:new HarmonyMethod(typeof(CollisionGenerationOptimizations),i==0?nameof(CollisionGenerationOptimizations.NormalPrefix):nameof(CollisionGenerationOptimizations.CachedPrefix)));}
  AnimationScanOptimizations.Configure(on);if(on)own.Patch(AnimationScanOptimizations.Target,prefix:new HarmonyMethod(typeof(AnimationScanOptimizations),nameof(AnimationScanOptimizations.Prefix)),transpiler:new HarmonyMethod(typeof(AnimationScanOptimizations),nameof(AnimationScanOptimizations.Transpiler)),finalizer:new HarmonyMethod(typeof(AnimationScanOptimizations),nameof(AnimationScanOptimizations.Finalizer)));
 }
 static string Geometry(CollisionTester t)=>string.Join('|',Enumerable.Range(0,t.CollisionBoxList.Count).Select(i=>{var c=t.CollisionBoxList.cuboids[i];var p=t.CollisionBoxList.positions[i];return $"{c.X1},{c.Y1},{c.Z1},{c.X2},{c.Y2},{c.Z2}:{p.X},{p.Y},{p.Z}:{t.CollisionBoxList.blocks[i].Id}";}));
 static string Run(int kind,int dim,double dx,double dy,double dz)
 {
  var t=Tester(kind);t.entityBox.Set(-.7,40.2,-1.1,1.7,42.5,1.1);t.minPos.SetDimension(dim);t.tmpPos.SetDimension(dim);events.Clear();var g=Gen(kind);g(t,Native(),dx,dy,dz,.6f,1f,dim);var a=Geometry(t)+";"+string.Join('|',events);int count=walks;g(t,Native(),dx,dy,dz,.6f,1f,dim);return a+":"+(walks-count);
 }
 static AnimationManager Manager(Action<string>? log=null)
 {
  var m=new AnimationManager();m.Init(null!,new EntityAgent());m.Animator=Proxy.Make<IAnimator>((method,args)=>{if(method.Name=="OnFrame")log?.Invoke("frame");if(method.Name=="set_CalculateMatrices")log?.Invoke("matrices:"+args![0]);if(method.Name=="GetAnimationState"){var a=New<RunningAnimation>();a.CurrentFrame=5;return a;}return method.ReturnType==typeof(void)?null:method.ReturnType.IsValueType?Activator.CreateInstance(method.ReturnType):null;});return m;
 }
 static object Measure(Action call){for(int i=0;i<4000;i++)call();const int n=30000;long before=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();for(int i=0;i<n;i++)call();return new{bytesPerCall=(GC.GetAllocatedBytesForCurrentThread()-before)/(double)n,nsPerCall=Stopwatch.GetElapsedTime(start).TotalNanoseconds/n};}
 public static void Run()
 {
  for(int i=0;i<2;i++)Assert(CollisionGenerationOptimizations.ValidateContract(i,out var why),why);Assert(AnimationScanOptimizations.ValidateContract(out var reason),reason);
  fixture.Patch(CollisionGenerationOptimizations.Walk,prefix:new HarmonyMethod(typeof(Checks),nameof(Walk)));
  var vectors=new(double X,double Y,double Z)[]{(0,0,0),(.4,-.3,-.2),(-2,1,3)};
  var stock=new List<string>();for(int kind=0;kind<2;kind++)foreach(int dim in new[]{0,1})foreach(var v in vectors)stock.Add(Run(kind,dim,v.X,v.Y,v.Z));
  var log=new List<string>();var manager=Manager(log.Add);manager.ActiveAnimationsByAnimCode.Add("walk",new(){AdjustCollisionBox=true});manager.OnServerTick(.1f);var stockLog=log.ToArray();bool flag=manager.AdjustCollisionBoxToAnimation;
  Mode(true);int index=0;for(int kind=0;kind<2;kind++)foreach(int dim in new[]{0,1})foreach(var v in vectors)Assert(Run(kind,dim,v.X,v.Y,v.Z)==stock[index++],"bounds/geometry/layers/dimension/order/cache-hit native equivalence");
  log.Clear();manager.OnServerTick(.1f);Assert(manager.AdjustCollisionBoxToAnimation==flag&&log.SequenceEqual(stockLog),"animation frame, matrices and collision flag order preserved");manager.ActiveAnimationsByAnimCode.Clear();manager.OnServerTick(.1f);Assert(!manager.AdjustCollisionBoxToAnimation,"empty animation state clears adjustment flag");
  var triggerLog=new List<string>();var triggered=Manager(triggerLog.Add);triggered.ActiveAnimationsByAnimCode.Add("walk",new(){AdjustCollisionBox=true});triggered.Triggers=new();triggered.Triggers.Add(new AnimFrameCallback{Animation="walk",Frame=1,Callback=()=>triggerLog.Add("trigger")});triggered.OnServerTick(.1f);Assert(triggerLog.Last()=="trigger"&&triggered.Triggers.Count==0,"native animation triggers still execute after frame/flag update");
  var absent=new AnimationManager();absent.Init(null!,new EntityAgent());absent.OnServerTick(.1f);Assert(!absent.AdjustCollisionBoxToAnimation,"no-animator path retained");
  var failed=Manager();int frames=0;failed.Animator=Proxy.Make<IAnimator>((m,a)=>{if(m.Name=="OnFrame"){frames++;throw new InvalidOperationException("frame-sentinel");}return null;});long af=AnimationScanOptimizations.Counters.Failures;try{failed.OnServerTick(.1f);throw new Exception("swallowed");}catch(InvalidOperationException e){Assert(e.Message=="frame-sentinel"&&frames==1&&AnimationScanOptimizations.Counters.Failures==af+1,"frame failure preserved once and recorded");}AnimationScanOptimizations.Configure(true);
  var pairs=new Dictionary<string,AnimationMetaData>{{"a",new()},{"b",new(){AdjustCollisionBox=true}},{"c",new()}};var seen=new List<string>();Assert(AnimationScanOptimizations.Check(pairs,p=>{seen.Add(p.Key);return p.Value.AdjustCollisionBox;})&&seen.SequenceEqual(new[]{"a","b"}),"predicate order and early exit preserved");
  int errors=0;try{AnimationScanOptimizations.Check(pairs,p=>{errors++;throw new InvalidOperationException("predicate");});throw new Exception("swallowed");}catch(InvalidOperationException){Assert(errors==1,"predicate exception not replayed");}
  var custom=pairs.Where(p=>p.Key!="b");long fallback=AnimationScanOptimizations.Counters.Fallbacks;Assert(!AnimationScanOptimizations.Check(custom,p=>p.Value.AdjustCollisionBox)&&AnimationScanOptimizations.Counters.Fallbacks==fallback+1,"custom enumerable uses native path");
  var customAccessor=Proxy.Make<IBlockAccessor>((m,a)=>{if(m.Name=="WalkBlocks"){((Action<Block,int,int,int>)a![2]!)(block,0,0,0);return null;}return null;});long gf=CollisionGenerationOptimizations.Counters(0).Fallbacks;var t=Tester(0);t.entityBox.Set(0,40,0,1,42,1);Gen(0)(t,customAccessor,0,0,0,1,1,0);Assert(CollisionGenerationOptimizations.Counters(0).Fallbacks==gf+1&&t.CollisionBoxList.Count==2,"custom accessor retains original callback and native traversal");
  for(int kind=0;kind<2;kind++)
  {
   var warmed=Tester(kind);warmed.entityBox.Set(0,40,0,1,42,1);var generator=Gen(kind);generator(warmed,Native(),0,0,0,1,1,0);string original=Geometry(warmed);long hitCount=CollisionGenerationOptimizations.Counters(kind).Hits;
   var neverWalk=Proxy.Make<IBlockAccessor>((m,a)=>throw new Exception("cache hit touched accessor"));generator(warmed,neverWalk,0,0,0,1,1,0);generator(warmed,null!,0,0,0,1,1,0);
   Assert(Geometry(warmed)==original&&CollisionGenerationOptimizations.Counters(kind).Hits==hitCount+2,"native cache hit preserves geometry without inspecting custom/null accessor");
   int visited=0;var changedAccessor=Proxy.Make<IBlockAccessor>((m,a)=>{if(m.Name=="WalkBlocks"){visited++;((Action<Block,int,int,int>)a![2]!)(block,2,40,0);}return null;});generator(warmed,changedAccessor,2,0,0,1,1,0);
   Assert(visited==1&&warmed.CollisionBoxList.Count==2,"custom fallback miss still walks after earlier cache hit; bounds were not pre-mutated");
   string changed=Geometry(warmed);generator(warmed,neverWalk,2,0,0,1,1,0);Assert(Geometry(warmed)==changed,"custom-generated native bounds cache remains caller-owned");
  }
  Assert(!CollisionGenerationOptimizations.CurrentThreadRetainsInputs,"idle callback state retains no tester/accessor");
  block.Hook=()=>{var nested=Tester(0);nested.entityBox.Set(0,20,0,1,21,1);var hook=block.Hook;block.Hook=null;try{Gen(0)(nested,Native(),0,0,0,1,1,0);Assert(nested.CollisionBoxList.Count==4,"nested generation independent");}finally{block.Hook=hook;}};Run(0,0,1,1,1);block.Hook=null;Assert(!CollisionGenerationOptimizations.CurrentThreadRetainsInputs,"recursive generation clears callback inputs");
  block.Hook=()=>{throw new InvalidOperationException("shape-sentinel");};try{Run(0,0,1,1,1);throw new Exception("swallowed");}catch(InvalidOperationException e){Assert(e.Message=="shape-sentinel","shape failure preserved without replay");}block.Hook=null;long calls=CollisionGenerationOptimizations.Counters(0).Calls;Run(0,0,1,1,1);Assert(CollisionGenerationOptimizations.Counters(0).Calls==calls&&!CollisionGenerationOptimizations.CurrentThreadRetainsInputs,"failure disables only normal gate and releases inputs");CollisionGenerationOptimizations.Configure(0,true);
  record=false;Parallel.For(0,32,_=>{var local=Tester(1);local.entityBox.Set(0,40,0,1,42,1);Gen(1)(local,Native(),0,0,0,1,1,0);if(local.CollisionBoxList.Count!=4||CollisionGenerationOptimizations.CurrentThreadRetainsInputs)throw new Exception("thread state leaked");});Assert(true,"thread-local generation independent");
  try{AnimationScanOptimizations.Transpiler([new CodeInstruction(OpCodes.Ret)]).ToArray();throw new Exception("IL accepted");}catch(InvalidOperationException){Assert(true,"unexpected animation IL rejected");}
  record=false;var trials=new List<object>();
  for(int trial=0;trial<4;trial++)
  {
   object Trial(bool on){Mode(on);var a=Native();var normal=Tester(0);normal.entityBox.Set(0,40,0,1,42,1);var cached=Tester(1);cached.entityBox.Set(0,40,0,1,42,1);var g0=Gen(0);var g1=Gen(1);var anim=Manager();anim.ActiveAnimationsByAnimCode.Add("walk",new());anim.ActiveAnimationsByAnimCode.Add("run",new(){AdjustCollisionBox=true});return new{normal=Measure(()=>{normal.minPos.X=int.MinValue;g0(normal,a,0,0,0,1,1,0);}),cached=Measure(()=>{cached.minPos.X=int.MinValue;g1(cached,a,0,0,0,1,1,0);}),cacheHit=Measure(()=>g1(cached,a,0,0,0,1,1,0)),animation=Measure(()=>anim.OnServerTick(.02f))};}
   object before,after;if(trial%2==0){before=Trial(false);after=Trial(true);}else{after=Trial(true);before=Trial(false);}trials.Add(new{trial,stock=before,optimized=after});var a=JsonSerializer.SerializeToElement(before);var b=JsonSerializer.SerializeToElement(after);foreach(var key in new[]{"normal","cached","cacheHit","animation"})Assert(a.GetProperty(key).GetProperty("bytesPerCall").GetDouble()>b.GetProperty(key).GetProperty("bytesPerCall").GetDouble(),"allocation reduction "+key);
  }
  Mode(false);fixture.UnpatchAll(fixture.Id);Console.WriteLine(JsonSerializer.Serialize(new{checks,trials,scope="Official generation/animation methods with synthetic shapes and intercepted native synchronous walker; no production claim."}));Console.WriteLine($"PASS {checks} collision generation and animation checks.");
 }
}
