using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

string game=Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY")!);
string path=Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!);
AssemblyLoadContext.Default.Resolving+=(context,name)=>
{
    foreach(string root in new[]{Path.GetDirectoryName(path)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")})
    {string file=Path.Combine(root,name.Name+".dll");if(File.Exists(file))return context.LoadFromAssemblyPath(file);}
    return null;
};
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
var type=assembly.GetType("BlockLogger.ServerHealth.ServerHealthService")!;
object service=RuntimeHelpers.GetUninitializedObject(type);
FieldInfo Field(string name)=>type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
void Set(string name,object value)=>Field(name).SetValue(service,value);
Set("sync",new object());Set("profileRunning",true);Set("profileStartedUtc",DateTime.UtcNow);Set("profileDurationSeconds",30);Set("profileTickDurationsMs",new List<double>{100,100});
var accumulators=(IDictionary)Activator.CreateInstance(Field("profileAccumulators").FieldType)!;
var cache=(IDictionary)Activator.CreateInstance(Field("profileAttributionCache").FieldType)!;
var accType=type.GetNestedType("ProfileAccumulator",BindingFlags.NonPublic)!;
var ownerType=type.GetNestedType("ModAttribution",BindingFlags.NonPublic)!;
for(int i=0;i<20;i++)
{
    object acc=Activator.CreateInstance(accType)!;accType.GetField("TotalMs")!.SetValue(acc,(double)i);accType.GetField("MaxSingleMs")!.SetValue(acc,(double)i/2);accType.GetField("Calls")!.SetValue(acc,(long)i+1);
    string marker="fixture:"+i;accumulators.Add(marker,acc);cache.Add(marker,Activator.CreateInstance(ownerType,"fixture","Fixture mod","High",true,false)!);
}
Set("profileAccumulators",accumulators);Set("profileAttributionCache",cache);
var method=type.GetMethod("GetTickContributors")!;
object Call()=>method.Invoke(service,null)!;
object Get(object target,string name)=>target.GetType().GetProperty(name)!.GetValue(target)!;
void Check(bool ok,string message){if(!ok)throw new Exception(message);}
object live=Call();var rows=(IList)Get(live,"Rows");Check(rows.Count==12,"Contributor payload exceeded cap");Check((string)Get(rows[0]!,"Code")=="fixture:19","Wrong live rank");Check((int)Get(live,"SampledTicks")==2,"Wrong live sample count");Check((string)Get(rows[0]!,"ModId")=="fixture","Ownership missing");
Check((double)Get(rows[0]!,"PercentOfProfileTickTime")==9.5,"Share denominator wrong");
Check((bool)Field("profileRunning").GetValue(service)!,"Read stopped the capture");
Set("lastHotspots",rows); // IList is the actual typed List<ServerProfilerHotspot>.
object summary=Activator.CreateInstance(Field("lastProfile").FieldType)!;summary.GetType().GetProperty("TickCount")!.SetValue(summary,77);Set("lastProfile",summary);Set("profileRunning",false);
object retained=Call();Check(!(bool)Get(retained,"Running"),"Retained capture labelled live");Check((int)Get(retained,"SampledTicks")==77,"Retained tick count wrong");Check(((IList)Get(retained,"Rows")).Count==12,"Retained rows missing");
Check(accumulators.Count==20,"Read mutated timing accumulators");
Console.WriteLine("PASS shipping assembly: bounded live rank, owner/share/sample counts, retained state and non-mutating reads without a game API/profiler.");
