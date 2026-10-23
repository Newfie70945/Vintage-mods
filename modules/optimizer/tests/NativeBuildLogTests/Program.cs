using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using Vintagestory.Server;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var d in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var p=Path.Combine(d,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
Checks.Run();
public class Proxy:DispatchProxy
{
 public System.Func<MethodInfo,object?[]?,object?> Handler=null!;
 protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);
 public static T Make<T>(System.Func<MethodInfo,object?[]?,object?> h)where T:class{var p=Create<T,Proxy>();((Proxy)(object)p).Handler=h;return p;}
}
public class FixturePlayer:ServerPlayer,IPlayer
{
 public FixturePlayer():base(null!,null!){}
 public new string PlayerName=>"Fixture";
}
static class Checks
{
 static int checks;static void Assert(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 delegate void Break(IServerPlayer? player,int id,BlockSelection selection);
 public static void Run()
 {
  Assert(NativeBuildLogOptimizations.ValidateContract(out var why),why);
  var target=NativeBuildLogOptimizations.Target;var owner=RuntimeHelpers.GetUninitializedObject(target.DeclaringType!);
  int lookups=0,logs=0;bool loggerThrows=false;Block? result=new Block{Code=new AssetLocation("game:rock-granite")};string logged="";
  var world=Proxy.Make<IServerWorldAccessor>((m,a)=>{if(m.Name=="GetBlock"){lookups++;return result;}return null;});
  var logger=Proxy.Make<ILogger>((m,a)=>{if(m.Name=="Build"){logs++;if(loggerThrows)throw new InvalidOperationException("log-sentinel");logged=string.Format((string)a![0]!,((object[])a[1]!));}return null;});
  var api=Proxy.Make<ICoreServerAPI>((m,a)=>m.Name=="get_World"?world:m.Name=="get_Logger"?logger:null);
  AccessTools.Field(target.DeclaringType,"sapi").SetValue(owner,api);
  var call=target.CreateDelegate<Break>(owner);var player=(FixturePlayer)RuntimeHelpers.GetUninitializedObject(typeof(FixturePlayer));
  var selection=new BlockSelection{Position=new BlockPos(1,2,3)};var own=new Harmony("test.nos.native-buildlog");
  NativeBuildLogOptimizations.Configure(false);call(player,1,selection);string native=logged;Assert(lookups==1&&logs==1,"native healthy fixture performs one lookup/log");
  own.Patch(target,prefix:new HarmonyMethod(typeof(NativeBuildLogOptimizations),nameof(NativeBuildLogOptimizations.Prefix)),transpiler:new HarmonyMethod(typeof(NativeBuildLogOptimizations),nameof(NativeBuildLogOptimizations.Transpiler)),finalizer:new HarmonyMethod(typeof(NativeBuildLogOptimizations),nameof(NativeBuildLogOptimizations.Finalizer)));
  NativeBuildLogOptimizations.Configure(true);lookups=logs=0;call(player,1,selection);Assert(logged==native&&lookups==1&&logs==1,"healthy formatting, lookup count and logging remain native-equivalent");
  lookups=logs=0;call(player,0,selection);Assert(lookups==0&&logs==1&&logged.Contains("Air"),"native air branch remains");
  result!.Code=null;call(player,42,selection);Assert(logged.Contains("Unknown block ID 42"),"missing code retains numeric ID in audit log");
  result=null;call(player,43,selection);Assert(logged.Contains("Unknown block ID 43"),"missing definition still logged");
  call(null,44,selection);Assert(logged.Contains("<system>")&&logged.Contains("Unknown block ID 44"),"null system actor retains position and ID without exceptions");
  Assert(logged.Contains(selection.Position.ToString()),"position preserved");
  result=new Block{Code=new AssetLocation("game:rock-granite")};loggerThrows=true;long failures=NativeBuildLogOptimizations.Counters.Failures;logs=0;
  try{call(player,1,selection);throw new Exception("swallowed");}catch(InvalidOperationException e){Assert(e.Message=="log-sentinel"&&logs==1,"logger failure preserved once; no replay");}
  loggerThrows=false;Assert(NativeBuildLogOptimizations.Counters.Failures==failures+1,"failure trips guard gate");result=null;
  try{call(player,45,selection);throw new Exception("gate still on");}catch(NullReferenceException){Assert(true,"failed guard uses original null behavior until reapplied");}
  NativeBuildLogOptimizations.Configure(true);call(player,45,selection);Assert(logged.Contains("45"),"explicit reapply retries guard");
  try{NativeBuildLogOptimizations.Transpiler([new CodeInstruction(System.Reflection.Emit.OpCodes.Ret)]).ToArray();throw new Exception("bad IL accepted");}catch(InvalidOperationException){Assert(true,"unexpected native layout rejected");}
  NativeBuildLogOptimizations.Configure(false);own.UnpatchAll(own.Id);
  Console.WriteLine($"PASS {checks} native build-log behavior, compatibility and failure checks.");
 }
}
