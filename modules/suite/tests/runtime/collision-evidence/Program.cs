using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!,dll=Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!;
AssemblyLoadContext.Default.Resolving+=(_,n)=>{foreach(var d in new[]{Path.GetDirectoryName(dll)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(d,n.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
Checks.Run(dll);
public class TestEntity:Entity {public override bool IsCreature=>true;}
public class TestBlock:Block {
 public Cuboidf[] Boxes=[new(0,0,0,1,1,1)];public bool Fail;
 public override Cuboidf[] GetCollisionBoxes(IBlockAccessor accessor,BlockPos pos){if(Fail)throw new IOException("shape-failure");return Boxes;}
}
public class Proxy:DispatchProxy{
 public System.Func<MethodInfo,object?[]?,object?> Fn=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? a)=>Fn(m!,a);
 public static T Make<T>(System.Func<MethodInfo,object?[]?,object?> fn)where T:class{var p=Create<T,Proxy>();((Proxy)(object)p).Fn=fn;return p;}
}
static class Checks{
 public static void Run(string dll){
 var asm=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);var type=asm.GetType("BlockLogger.AntiCheat.PacketGuardService",true)!;
 const BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
 int count=0,blockReads=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);count++;}
 bool loaded=true,support=false;int missingX=-1;
 var chunk=(ServerChunk)RuntimeHelpers.GetUninitializedObject(typeof(ServerChunk));
 var wall=new TestBlock{BlockId=1,Code=new("test:wall")};var air=new Block{BlockId=0};
 var accessor=Proxy.Make<IBlockAccessor>((m,a)=>m.Name switch{
 "get_MapSizeX" or "get_MapSizeZ"=>1000,"get_MapSizeY"=>256,
 "GetChunkAtBlockPos"=>loaded&&((BlockPos)a![0]!).X!=missingX?chunk:null,
 "GetBlock"=>Read((BlockPos)a![0]!),_=>throw new Exception("Unexpected terrain API "+m.Name)});
 Block Read(BlockPos p){blockReads++;return p.X==5&&p.Y>=4&&p.Y<=6||support&&p.Y==8?wall:air;}
 var world=Proxy.Make<IServerWorldAccessor>((m,a)=>m.Name=="get_BlockAccessor"?accessor:throw new Exception("Unexpected world API "+m.Name));
 var api=Proxy.Make<ICoreServerAPI>((m,a)=>m.Name=="get_World"?world:throw new Exception("Unexpected API "+m.Name));
 var guard=RuntimeHelpers.GetUninitializedObject(type);type.GetField("api",F)!.SetValue(guard,api);
 var config=Activator.CreateInstance(asm.GetType("BlockLogger.AntiCheat.AntiCheatConfig",true)!)!;config.GetType().GetProperty("PacketGuardNoclipEnabled")!.SetValue(config,true);type.GetField("config",F)!.SetValue(guard,config);
 var entity=new TestEntity();var body=new Cuboidf(-.3f,0,-.3f,.3f,1.8f,.3f);
 void SetBody(Cuboidf box){var f=typeof(Entity).GetField("OriginCollisionBox",F);if(f!=null)f.SetValue(entity,box);else typeof(Entity).GetProperty("OriginCollisionBox",F)!.SetValue(entity,box);}
 SetBody(body);
 var collision=type.GetMethods(F).Single(m=>m.Name=="EntityBodyIntersectsCollision"&&m.GetParameters().Length==8);
 (bool Hit,bool Known) At(double x,double y=4){object?[] a=[entity,x,y,3.5,0,null,0d,false];bool hit=(bool)collision.Invoke(guard,a)!;return(hit,(bool)a[7]!);}
 var path=type.GetMethod("TryDetectNoclipPath",F)!;
 bool Across(double from=3.5,double to=7.5){object?[] a=[entity,from,4d,3.5,to,4d,3.5,0,0,0d,null];return(bool)path.Invoke(guard,a)!;}
 Check(At(3.5)==(false,true),"known air is clear");Check(At(5.5)==(true,true),"deep wall collision measured");Check(Across(),"loaded wall crossing remains detectable");
 Check(!Across(5.5,7.5),"leaving existing overlap is not a new wall-crossing accusation");
 missingX=6;Check(!Across(),"unknown later terrain invalidates the whole path, even after collision samples");missingX=-1;
 loaded=false;blockReads=0;Check(At(5.5)==(false,false)&&blockReads==0,"unloaded terrain is unknown without block reads/load requests");loaded=true;
 wall.Fail=true;Check(At(5.5)==(false,false),"throwing mod geometry is unknown");wall.Fail=false;
 wall.Boxes=[new(float.NaN,0,0,1,1,1)];Check(At(5.5)==(false,false),"non-finite mod geometry is unknown");wall.Boxes=[new(0,0,0,1,1,1)];
 SetBody(new(-30,0,-30,30,30,30));blockReads=0;Check(At(5.5)==(false,false)&&blockReads==0,"oversized body probe is bounded");SetBody(body);
 var flight=type.GetMethod("HasLoadedUnsupportedFlightVolume",F)!;var pos=new EntityPos(3.5,10,3.5);
 Check((bool)flight.Invoke(guard,[entity,pos])!,"loaded unsupported space can support flight review");support=true;Check(!(bool)flight.Invoke(guard,[entity,pos])!,"nearby ground suppresses flight review");support=false;loaded=false;Check(!(bool)flight.Invoke(guard,[entity,pos])!,"unloaded space cannot support flight review");loaded=true;
 var stateType=type.GetNestedType("MovementState",BindingFlags.NonPublic)!;var state=Activator.CreateInstance(stateType,true)!;var observe=stateType.GetMethod("ObserveEndpointPenetration")!;
 void Endpoint(long now,int dimension=0)=>observe.Invoke(state,[0d,0d,1d,0d,dimension,now]);
 Endpoint(1000);Endpoint(1000);Check((int)stateType.GetField("EndpointCollisionSamples")!.GetValue(state)! ==1,"same-time samples reset instead of confirming");
 Endpoint(1100);Endpoint(1500);Check((long)stateType.GetField("EndpointCollisionSinceUtc")!.GetValue(state)! ==1000,"sustained endpoint evidence spans actual time");
 Endpoint(3000);Check((int)stateType.GetField("EndpointCollisionSamples")!.GetValue(state)! ==1,"stale endpoint window resets");
 Endpoint(3100,1);Check((long)stateType.GetField("EndpointCollisionSinceUtc")!.GetValue(state)! ==3100,"dimension change resets endpoint window");
 // A failed correction must not teleport, mutate baselines, or grant transition trust.
 var rollback=type.GetMethod("RollbackMovement",F)!;
 object StateAt(double x,long when)=>stateType.GetMethod("From")!.Invoke(null,[new EntityPos(x,4,3.5),when,1L])!;
 bool Correct(object sample)=> (bool)rollback.Invoke(guard,[null,sample,entity,5000L,"test-correction"])!;
 var blocked=StateAt(5.5,4000);
 Check(!Correct(blocked),"all colliding rollback candidates cancel correction instead of falling back");
 Check((long)stateType.GetField("GraceUntilUtc")!.GetValue(blocked)! ==0,"cancelled correction grants no grace");
 loaded=false;Check(!Correct(StateAt(3.5,4000)),"unloaded rollback destination is unknown, not safe");loaded=true;
 wall.Fail=true;Check(!Correct(StateAt(5.5,4000)),"throwing rollback geometry cancels correction");wall.Fail=false;
 Check(!Correct(StateAt(3.5,-1000000)),"expired rollback history cannot revive stale fallback");
 Console.WriteLine($"PASS {count} compiled collision/flight checks: loaded geometry, bounded probes, unknown terrain, shape failures, overlap exit, support and endpoint timing. No chunk loader is available in fixture.");
 }
}
