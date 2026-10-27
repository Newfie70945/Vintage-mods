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
public class ProbeEntity:Entity {public override bool IsCreature=>true;}
public class CustomBlock:Block {}
public class ProbeBehavior:BlockBehavior { public ProbeBehavior(Block block):base(block){} }
public class Proxy:DispatchProxy{
 public System.Func<MethodInfo,object?[]?,object?> Fn=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? a)=>Fn(m!,a);
 public static T Make<T>(System.Func<MethodInfo,object?[]?,object?> fn)where T:class{var p=Create<T,Proxy>();((Proxy)(object)p).Fn=fn;return p;}
}
static class Checks{
 private static bool AllowMining()=>true;
 public static void Run(string dll){
 var asm=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
 int count=0,reads=0;void Check(bool ok,string text){if(!ok)throw new Exception(text);count++;}
 bool loaded=true,wallPresent=true;
 Block wall=new Block{BlockId=1,Code=new("game:stone"),CollisionBoxes=[new(0,0,0,1,1,1)]};var air=new Block{BlockId=0,Code=new("game:air")};
 var chunk=(ServerChunk)RuntimeHelpers.GetUninitializedObject(typeof(ServerChunk));
 var accessor=Proxy.Make<IBlockAccessor>((m,a)=>m.Name switch{"get_MapSizeX" or "get_MapSizeZ"=>1000,"get_MapSizeY"=>256,"GetChunkAtBlockPos"=>loaded?chunk:null,"GetBlock"=>Read((BlockPos)a![0]!),_=>throw new Exception("Unexpected terrain API "+m.Name)});
 Block Read(BlockPos p){reads++;return wallPresent&&p.X==5?wall:air;}
 var world=Proxy.Make<IServerWorldAccessor>((m,a)=>m.Name=="get_BlockAccessor"?accessor:throw new Exception("Unexpected world API "+m.Name));
 var api=Proxy.Make<ICoreServerAPI>((m,a)=>m.Name=="get_World"?world:throw new Exception("Unexpected API "+m.Name));
 Type type=asm.GetType("BlockLogger.AntiCheat.ServerActionChecks",true)!;
 object action=RuntimeHelpers.GetUninitializedObject(type);type.GetField("api",F)!.SetValue(action,api);
 var method=type.GetMethod("BlockedFromAllSamples",F)!;
 var attacker=new ProbeEntity();attacker.Pos.SetPos(3.5,4,3.5);attacker.LocalEyePos=new(0,1.6,0);
 var victim=new ProbeEntity();victim.Pos.SetPos(7.5,4,3.5);victim.SelectionBox=new(-.3f,0,-.3f,.3f,1.8f,.3f);
 bool? Los()=>(bool?)method.Invoke(action,[attacker,victim]);
 Check(Los()==true,"Known full wall blocks all sampled target heights");
 wallPresent=false;Check(Los()==false,"Open combat path remains allowed");wallPresent=true;
 loaded=false;reads=0;Check(Los()==null&&reads==0,"Unloaded terrain is unknown and no terrain block read is issued");loaded=true;
 wall.EntityClass="TestDynamicContainer";Check(Los()==null,"Block entities cannot authorize static-wall rejection");wall.EntityClass="";
 wall.BlockBehaviors=[new ProbeBehavior(wall)];Check(Los()==null,"Behavior-driven collision is not assumed static");wall.BlockBehaviors=[];
 wall.Code=new("game:door-closed");Check(Los()==null,"Doors cannot authorize combat-wall rejection");wall.Code=new("game:stone");
 wall.CollisionBoxes=[new(0,0,0,1,.5f,1)];Check(Los()==null,"Partial geometry remains advisory");wall.CollisionBoxes=[new(0,0,0,1,1,1)];
 wall=new CustomBlock{BlockId=1,Code=new("game:stone"),CollisionBoxes=[new(0,0,0,1,1,1)]};Check(Los()==null,"Custom block class remains advisory");
 victim.Pos.Dimension=1;Check(Los()==null,"Cross-dimension targets cannot generate wall evidence");victim.Pos.Dimension=0;
 victim.Pos.X=100;Check(Los()==null,"Probe range stays bounded");victim.Pos.X=7.5;
 attacker.LocalEyePos.X=double.NaN;Check(Los()==null,"Invalid eye geometry cannot authorize rejection");attacker.LocalEyePos.X=0;
 var palette=asm.GetType("BlockLogger.AntiCheat.OreShieldPalette",true)!;
 var safe=palette.GetMethod("IsSafeOpaqueCube",F)!;
 var missing=new Block{BlockId=3,Code=new("game:rock-missing"),IsMissing=true};
 Check(!(bool)safe.Invoke(null,[missing,accessor])!,"Missing placeholders cannot become OreShield host blocks");
 var impulses=asm.GetType("BlockLogger.AntiCheat.ServerImpulseAllowance",true)!;
 impulses.GetMethod("Mark",F)!.Invoke(null,["fixture",1000L]);
 Check((bool)impulses.GetMethod("Active",F)!.Invoke(null,["fixture",1700L])!,"Server impulse has a brief allowance");
 Check(!(bool)impulses.GetMethod("Active",F)!.Invoke(null,["fixture",1751L])!,"Impulse allowance expires");

 var classify=asm.GetType("BlockLogger.AntiCheat.PacketGuardService",true)!.GetMethod("ClassifyMiningExpectation",F)!;
 var stockBlock=new Block{BlockId=1,Code=new("game:stone")};
 Check(classify.Invoke(null,[stockBlock,null])!.ToString()=="Authoritative","Stock mining classification remains available");
 var miningMethod=typeof(Block).GetMethod(nameof(Block.OnGettingBroken),[typeof(IPlayer),typeof(BlockSelection),typeof(ItemSlot),typeof(float),typeof(float),typeof(int)])!;
 var foreign=new HarmonyLib.Harmony("nss.tests.mining-compatibility");
 try{foreign.Patch(miningMethod,prefix:new HarmonyLib.HarmonyMethod(typeof(Checks).GetMethod(nameof(AllowMining),F)!));Check(classify.Invoke(null,[stockBlock,null])!.ToString()=="CustomUnknown","Foreign mining patches downgrade inferred timing to review-only");}
 finally{foreign.UnpatchAll("nss.tests.mining-compatibility");}

 var guardType=asm.GetType("BlockLogger.AntiCheat.PacketGuardService",true)!;
 var cfg=Activator.CreateInstance(asm.GetType("BlockLogger.AntiCheat.AntiCheatConfig",true)!)!;
 object miningGuard=Activator.CreateInstance(guardType,F,null,[api,cfg,null],null)!;
 var miner=(ServerPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));
 var dataField=typeof(ServerPlayer).GetField("worlddata",F)!;object minerData=RuntimeHelpers.GetUninitializedObject(dataField.FieldType);
 minerData.GetType().GetField("PlayerUID",F)!.SetValue(minerData,"mine-fixture");minerData.GetType().GetField("Entityplayer",F)!.SetValue(minerData,new EntityPlayer());dataField.SetValue(miner,minerData);
 var fakeSelection=new BlockSelection{Position=new BlockPos(5,4,3,0),Block=new Block{BlockId=2,Code=new("test:spoofed-selection")}};
 object?[] miningArgs=[miner,fakeSelection,EnumHandling.PassThrough];guardType.GetMethod("CheckMiningBurst",F)!.Invoke(miningGuard,miningArgs);
 var history=(System.Collections.IDictionary)guardType.GetField("breakHistory",F)!.GetValue(miningGuard)!;
 var sample=((System.Collections.IEnumerable)history["mine-fixture"]!).Cast<object>().Last();
 Check((int)sample.GetType().GetProperty("BlockId")!.GetValue(sample)! ==1,"Mining burst uses loaded world ID rather than supplied selection ID");
 Console.WriteLine($"PASS {count} compiled combat/impulse/OreShield checks; doors, custom shapes, loaded-only probes, dimensions, bounds and missing replacement rejection. No chunk loader exists in fixture.");
 }
}
