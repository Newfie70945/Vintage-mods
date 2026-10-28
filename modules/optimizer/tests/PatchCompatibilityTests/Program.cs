using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;
using NewfiesOptimizer;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Vintagestory.Server;

string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,n)=>{foreach(var d in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods"),Environment.GetEnvironmentVariable("NSS_COMPAT_SUITE_DIR")??""}){var p=Path.Combine(d,n.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(p));}return null;};
Checks.Run(args);
public class Proxy:DispatchProxy {
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);
 public static T Make<T>(System.Func<MethodInfo,object?[]?,object?> f)where T:class{var p=Create<T,Proxy>();((Proxy)(object)p).Handler=f;return p;}
}
public class TestEntity:Vintagestory.API.Common.Entities.Entity {}
static class Checks {
 static int checks;
 static void Check(bool value,string why){if(!value)throw new Exception(why);checks++;}
 public static bool FakePrefix()=>true;
 public static void FakePostfix(){}
 public static IEnumerable<CodeInstruction> FakeTranspiler(IEnumerable<CodeInstruction> instructions)=>instructions;
 static int spyCalls;
 public static bool CallbackSpy(ref bool __result){spyCalls++;__result=true;return false;}
 record Case(string Module,MethodBase Target,string Owner,string Kind,string Type,string Handler,string File);
 static Case[] Cases(string root,string arl)=>[
  new("idleFirepitFastPath",AccessTools.Method(typeof(BlockEntityFirepit),"canHeatInput"),arl=="3.2.0"?KnownPatchCompatibility.ArlOwner:"attributerenderinglibrary","transpiler","AttributeRenderingLibrary.HarmonyPatches.AttributeRedirectionPatch","Transpiler",Path.Combine(root,"arl-"+arl,"AttributeRenderingLibrary.dll")),
  new("collisionTraversalReuse",TraversalOptimizations.Target(0),"walkableentities.runtime","postfix","WalkableEntities.MovingFrame","Terrain",Path.Combine(root,"walkableentities","WalkableEntities.dll")),
  new("blockUpdateListReuse",AccessTools.Method(typeof(ServerMain),"SendSetBlocksPacket"),"newfies.serveressentials.anticheat.oreshield","prefix","BlockLogger.AntiCheat.OreShieldService","BeforeSetBlocksPacket",Path.Combine(Environment.GetEnvironmentVariable("NSS_COMPAT_SUITE_DIR")??"", "NewfiesServerEssentialsSuite.dll")),
  new("assetCompressionCache",AccessTools.Method(typeof(ServerMain),"SendServerAssets"),"sekelsta.genelib","postfix","Genelib.GenelibSystem","SendServerAssets_Postfix",Path.Combine(root,"genelib","Genelib.dll")),
  new("repulseDelegateReuse",AccessTools.Method(typeof(EntityBehaviorRepulseAgents),"WalkEntity"),"walkableentities.runtime","prefix","WalkableEntities.MovingFrame","Repulse",Path.Combine(root,"walkableentities","WalkableEntities.dll"))
 ];
 static void Patch(Case c,MethodInfo handler){var h=new Harmony(c.Owner);var method=new HarmonyMethod(handler);h.Patch(c.Target,prefix:c.Kind=="prefix"?method:null,postfix:c.Kind=="postfix"?method:null,transpiler:c.Kind=="transpiler"?method:null);}
 static MethodInfo Fake(Case c)=>AccessTools.Method(typeof(Checks),c.Kind=="prefix"?nameof(FakePrefix):c.Kind=="postfix"?nameof(FakePostfix):nameof(FakeTranspiler));
 static MethodInfo SpoofHandler(Case c){var assembly=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("UnauditedCompatibilityFixture"+Guid.NewGuid().ToString("N")),AssemblyBuilderAccess.Run);var type=assembly.DefineDynamicModule("fixture").DefineType(c.Type,TypeAttributes.Public);Type returns=c.Kind=="prefix"?typeof(bool):c.Kind=="postfix"?typeof(void):typeof(IEnumerable<CodeInstruction>);var m=type.DefineMethod(c.Handler,MethodAttributes.Public|MethodAttributes.Static,returns,c.Kind=="transpiler"?[typeof(IEnumerable<CodeInstruction>)]:Type.EmptyTypes);var il=m.GetILGenerator();if(c.Kind=="prefix")il.Emit(OpCodes.Ldc_I4_1);if(c.Kind=="transpiler")il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ret);return type.CreateType()!.GetMethod(c.Handler)!;}
 static OptimizerConfig Config(){var c=new OptimizerConfig();foreach(var p in c.EngineOptimizations.GetType().GetProperties().Where(p=>p.PropertyType==typeof(bool)))p.SetValue(c.EngineOptimizations,false);c.EngineOptimizations.IdleFirepitFastPath=true;c.EngineOptimizations.CollisionTraversalReuse=true;c.EngineOptimizations.BlockUpdateListReuse=true;c.EngineOptimizations.AssetCompressionCache=true;c.EngineOptimizations.RepulseDelegateReuse=true;return c;}
 static (ICoreServerAPI Api,List<Action<float>> Ticks) Api(){var ticks=new List<Action<float>>();var events=Proxy.Make<IServerEventAPI>((m,a)=>{if(m.Name=="RegisterGameTickListener"){ticks.Add((Action<float>)a![0]!);return 1L;}return null;});var logger=Proxy.Make<ILogger>((m,a)=>null);return (Proxy.Make<ICoreServerAPI>((m,a)=>m.Name switch{"get_Event"=>events,"get_Logger"=>logger,"get_Side"=>EnumAppSide.Server,_=>null}),ticks);}
 static void Active(EngineOptimizations e,IEnumerable<Case> cases){foreach(var c in cases){var m=e.Modules().Single(x=>x.Name==c.Module);Check(m.Status=="active",c.Module+": "+m.Status+" "+m.Reason);}}
 public static void Run(string[] args){
  string root=Environment.GetEnvironmentVariable("NOS_COMPAT_FIXTURES")??"",arl=args.FirstOrDefault()??"3.2.0";var cases=Cases(root,arl);
  foreach(var c in cases){Patch(c,Fake(c));try{using var e=new EngineOptimizations(Api().Api,Config());Check(e.Modules().Single(x=>x.Name==c.Module).Status=="conflict","owner spoof must not authorize "+c.Module);}finally{new Harmony(c.Owner).UnpatchAll(c.Owner);}}
  foreach(var c in cases){Patch(c,SpoofHandler(c));try{using var e=new EngineOptimizations(Api().Api,Config());Check(e.Modules().Single(x=>x.Name==c.Module).Status=="conflict","matching owner/type/handler without audited binary must not authorize "+c.Module);}finally{new Harmony(c.Owner).UnpatchAll(c.Owner);}}
  if(root.Length==0){Console.WriteLine($"PASS {checks} compatibility owner-spoof checks. Real published-mod checks require NOS_COMPAT_FIXTURES and NSS_COMPAT_SUITE_DIR.");return;}
  foreach(var c in cases){Check(File.Exists(c.File),"Missing audited binary "+c.File);var a=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(c.File));var handler=AccessTools.Method(a.GetType(c.Type,true),c.Handler);Patch(c,handler);var info=Harmony.GetPatchInfo(c.Target)!;var list=c.Kind=="prefix"?info.Prefixes:c.Kind=="postfix"?info.Postfixes:info.Transpilers;var patch=list.Single(p=>p.owner==c.Owner);Check(KnownPatchCompatibility.Allows(c.Module,c.Target,patch,c.Kind),"audited handler allowed: "+c.Module);Check(!KnownPatchCompatibility.Allows(c.Module,c.Target,patch,"finalizer"),"wrong patch kind rejected");Check(!KnownPatchCompatibility.Allows("unrelated",c.Target,patch,c.Kind),"no cross-module owner exception");}
  try{
   // Compare the real ARL-patched native empty firepit with the fast path.
   var block=new Block{Code=new("game:firepit-cold")};block.VariantStrict["burnstate"]="cold";
   BlockEntityFirepit Fire()=>new(){Api=Api().Api,Block=block,furnaceTemperature=20,inputStackCookingTime=7};
   var burn=BlockTickOptimizations.Target(0).CreateDelegate<Action<BlockEntityFirepit,float>>();var stock=Fire();burn(stock,.1f);
   var primary=Api();using(var e=new EngineOptimizations(primary.Api,Config())){
    Active(e,cases);foreach(var tick in primary.Ticks)tick(1);Active(e,cases);
    var fast=Fire();long before=BlockTickOptimizations.Counters(0).Calls;burn(fast,.1f);Check(fast.inputStackCookingTime==stock.inputStackCookingTime&&fast.furnaceTemperature==stock.furnaceTemperature&&BlockTickOptimizations.Counters(0).Calls>before,"real ARL and native empty firepit stay equivalent");
    var occupied=Fire();occupied.inputSlot.Itemstack=new ItemStack(new Item{ItemId=1,Code=new("test:input")});Check(BlockTickOptimizations.FirepitPrefix(occupied,out _),"occupied firepits retain ARL/native handling");
    byte[] source=new byte[8192];var compress=AssetCompressionOptimizations.Target.CreateDelegate<System.Func<byte[],int,byte[]>>();AssetCompressionOptimizations.Track(source,1048576);var first=compress(source,source.Length);var second=compress(source,source.Length);Check(AssetCompressionOptimizations.Counters.Hits>0&&!ReferenceEquals(first,second)&&Compression.Decompress(second,4,second.Length-4).SequenceEqual(source),"real GeneLib send hook coexists with independent native asset compression results");
   }
   foreach(var c in cases)Check(Harmony.GetPatchInfo(c.Target)!.Owners.Contains(c.Owner),"NOS disposal preserves "+c.Owner);
   var walk=AccessTools.Method(typeof(EntityBehaviorRepulseAgents),"WalkEntity");
   RepulseDelegateOptimizations.Configure(true);
   var cached=RepulseDelegateOptimizations.GetWalk(new EntityBehaviorRepulseAgents(new TestEntity()));
   var spy=new Harmony("test.compat.cached-callback");
   try{spy.Patch(walk,prefix:new HarmonyMethod(typeof(Checks),nameof(CallbackSpy)));Check(cached(new TestEntity())&&spyCalls==1,"cached callback observes a patch installed after delegate creation");}
   finally{spy.UnpatchAll(spy.Id);RepulseDelegateOptimizations.Configure(false);}
   // A known hook never masks an additional unknown hook, even under the same owner.
   foreach(var c in cases){var api=Api();using(var e=new EngineOptimizations(api.Api,Config())){Active(e,cases);Patch(c,Fake(c));foreach(var tick in api.Ticks)tick(1);Check(e.Modules().Single(x=>x.Name==c.Module).Status=="conflict","late extra hook disables "+c.Module);Check(Harmony.GetPatchInfo(c.Target)!.Owners.Contains(c.Owner),"late stand-down preserves foreign patch");}new Harmony(c.Owner).Unpatch(c.Target,Fake(c));}
  }finally{foreach(var id in cases.Select(c=>c.Owner).Distinct())new Harmony(id).UnpatchAll(id);}
  Console.WriteLine($"PASS {checks} real-published-patch activation, ARL {arl} firepit/native behavior, asset bytes, owner/kind/module identity, late-conflict and teardown checks.");
 }
}
