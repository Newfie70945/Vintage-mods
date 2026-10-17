using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
string dll=Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!);
AssemblyLoadContext.Default.Resolving+=(ctx,name)=>{
 foreach(var root in new[]{Path.GetDirectoryName(dll)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){
  var p=Path.Combine(root,name.Name+".dll");if(File.Exists(p))return ctx.LoadFromAssemblyPath(p);
 }return null;
};
var asm=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
var service=asm.GetType("BlockLogger.ServerHealth.ServerHealthService",true)!;
var identity=service.GetNestedType("ModIdentity",BindingFlags.NonPublic)!;
var token=service.GetNestedType("ModToken",BindingFlags.NonPublic)!;
object instance=RuntimeHelpers.GetUninitializedObject(service);
var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(identity))!;
void Add(string id,string marker){
 var obj=Activator.CreateInstance(identity,true)!;identity.GetField("ModId")!.SetValue(obj,id);identity.GetField("Name")!.SetValue(obj,id);
 ((IList)identity.GetProperty("Tokens")!.GetValue(obj)!).Add(Activator.CreateInstance(token,new object[]{marker,115,"High"})!);list.Add(obj);
}
service.GetField("modIdentities",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(instance,list);
var resolve=service.GetMethod("ResolveModAttributionCore",BindingFlags.NonPublic|BindingFlags.Instance)!;
object Resolve(string path)=>resolve.Invoke(instance,new object[]{path})!;
object Value(object o,string key)=>o.GetType().GetProperty(key)!.GetValue(o)!;
void Check(bool condition,string detail){if(!condition)throw new Exception(detail);}
Add("example","Example.Ai.WanderTask");
var known=Resolve("server/Example.Ai.WanderTask/update");Check((string)Value(known,"ModId")=="example","Concrete handler ownership");
var unknown=Resolve("server/simulation/unnamed-callback");Check(!(bool)Value(unknown,"IsAttributed"),"Generic callbacks must stay unknown");
Add("other","Example.Ai.WanderTask");var ambiguous=Resolve("Example.Ai.WanderTask");Check(!(bool)Value(ambiguous,"IsAttributed"),"Shared assembly/class must remain ambiguous");
var core=Resolve("Vintagestory.Server.ServerMain.Tick");Check((bool)Value(core,"IsCore"),"Engine ownership");
var clear=service.GetMethod("ClearProfileSlowTicks")!;
service.GetField("sync",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(instance,new object());
foreach(var (field,typeName) in new[]{("lastProfileSlowTicks","ServerProfileSlowTick"),("lastSlowCauses","ServerProfileSlowCause")}){
 var t=asm.GetType("BlockLogger.ServerHealth."+typeName,true)!;var rows=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(t))!;rows.Add(Activator.CreateInstance(t)!);service.GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(instance,rows);
}
Check((int)clear.Invoke(instance,null)! == 1,"Clear worst ticks");
Check(((IList)service.GetField("lastSlowCauses",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(instance)!).Count==0,"Clear cause summaries with tick rows");
Console.WriteLine("PASS: handler ownership, unknown/shared/core markers and paired history clearing against shipping assembly");
