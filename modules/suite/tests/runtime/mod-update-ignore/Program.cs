using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
string dll=Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!);
AssemblyLoadContext.Default.Resolving+=(ctx,name)=>{
 foreach(var root in new[]{Path.GetDirectoryName(dll)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){
  var p=Path.Combine(root,name.Name+".dll");if(File.Exists(p))return ctx.LoadFromAssemblyPath(p);
 }return null;
};
var asm=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
var analysis=asm.GetType("BlockLogger.ModUpdates.ModUpdateAnalysis",true)!;
var recordType=asm.GetType("BlockLogger.ModUpdates.ModUpdateIgnoreRecord",true)!;
void Check(bool c,string d){if(!c)throw new Exception(d);}
object Record(string status,string game,string installed,string recommended){
 var r=Activator.CreateInstance(recordType)!;
 recordType.GetProperty("ModId")!.SetValue(r,"mod");recordType.GetProperty("Status")!.SetValue(r,status);
 recordType.GetProperty("GameVersion")!.SetValue(r,game);recordType.GetProperty("InstalledVersion")!.SetValue(r,installed);
 recordType.GetProperty("RecommendedVersion")!.SetValue(r,recommended);return r;
}
bool Active(object? r,string game,string installed,string recommended,string status)=>(bool)analysis.GetMethod("IsIgnoreActive")!.Invoke(null,[r,game,installed,recommended,status])!;
string Compare(string need,string have)=>(string)analysis.GetMethod("CompareToRequirement")!.Invoke(null,[need,have])!;

var update=Record("Update","1.22.7","1.0.0","1.2.0");
Check(Active(update,"1.22.7","1.0.0","1.2.0","Update"),"ignore holds while nothing changed");
Check(!Active(update,"1.22.8","1.0.0","1.2.0","Update"),"game version change ends the ignore");
Check(!Active(update,"1.22.7","1.1.0","1.2.0","Update"),"installed version change ends the ignore");
Check(!Active(update,"1.22.7","1.0.0","1.3.0","Update"),"a newer offered release ends the ignore");
Check(!Active(update,"1.22.7","1.0.0","1.2.0","NotFound"),"status change ends the ignore");
Check(!Active(null,"1.22.7","1.0.0","1.2.0","Update"),"no record means not ignored");
var notFound=Record("NotFound","1.22.7","1.0.0","");
Check(Active(notFound,"1.22.7","1.0.0","","NotFound"),"no-release ignore ignores recommended version");
Check(Active(notFound,"1.22.7","1.0.0","9.9.9","NotFound"),"recommended version is irrelevant for NotFound");
Check((bool)analysis.GetMethod("IsIgnorable")!.Invoke(null,["Update"])!&&!(bool)analysis.GetMethod("IsIgnorable")!.Invoke(null,["Current"])!&&!(bool)analysis.GetMethod("IsIgnorable")!.Invoke(null,["Staged"])!,"only outdated statuses are ignorable");

Check(Compare("1.0.0","1.0.0")=="Ok"&&Compare("1.0.0","1.2.0")=="Ok","at or above minimum is ok");
Check(Compare("1.3.0","1.2.0")=="TooOld","below minimum is too old");
Check(Compare("*","0.0.1")=="Ok"&&Compare("","1.0.0")=="Ok","wildcard/empty needs nothing");
Check(Compare("abc","1.0.0")=="Unknown","unparsable requirement is unknown, not a false alarm");

var declared=new Dictionary<string,string>{["game"]="1.21.0",["libmod"]="1.0.0",["gone"]="1.0.0",["off"]="2.0.0",["old"]="3.0.0"};
var installed=new Dictionary<string,string>{["libmod"]="1.4.0",["off"]="2.1.0",["old"]="2.0.0"};
var enabled=new Dictionary<string,string>{["libmod"]="1.4.0",["old"]="2.0.0",["game"]="1.22.7"};
var deps=((IEnumerable)analysis.GetMethod("ResolveDependencies")!.Invoke(null,[declared,installed,enabled])!).Cast<object>().ToDictionary(
 d=>(string)d.GetType().GetProperty("ModId")!.GetValue(d)!,d=>(string)d.GetType().GetProperty("Status")!.GetValue(d)!);
Check(!deps.ContainsKey("game"),"game is not listed as a mod dependency");
Check(deps["libmod"]=="Ok"&&deps["gone"]=="Missing"&&deps["off"]=="Disabled"&&deps["old"]=="TooOld","dependency states resolved");

var all=new Dictionary<string,IReadOnlyDictionary<string,string>>{["a"]=new Dictionary<string,string>{["lib"]="1.0.0",["game"]="1.22.0"},["b"]=new Dictionary<string,string>{["lib"]="1.0.0"},["lib"]=new Dictionary<string,string>()};
var rev=(IDictionary)analysis.GetMethod("ReverseDependencies")!.Invoke(null,[all])!;
Check(((IList)rev["lib"]!).Count==2&&!rev.Contains("game"),"reverse dependencies list dependents and skip game");

var g=analysis.GetMethod("DescribeGameRequirement")!.Invoke(null,["1.21.0","1.22.7"])!;
Check((string)g.GetType().GetField("Item2")!.GetValue(g)!=null&&(string)g.GetType().GetField("Item2")!.GetValue(g)=="Compatible","older minimum is compatible");
var newer=analysis.GetMethod("DescribeGameRequirement")!.Invoke(null,["1.23.0","1.22.7"])!;
Check((string)newer.GetType().GetField("Item2")!.GetValue(newer)!=null&&(string)newer.GetType().GetField("Item2")!.GetValue(newer)=="RequiresNewerGame","newer minimum flagged");
Console.WriteLine("PASS: mod update ignore expiry, dependency states, reverse dependencies and game requirement");
