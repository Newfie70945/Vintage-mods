using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
string dll=Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!);
AssemblyLoadContext.Default.Resolving+=(context,name)=>{foreach(var root in new[]{Path.GetDirectoryName(dll)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){string p=Path.Combine(root,name.Name+".dll");if(File.Exists(p))return context.LoadFromAssemblyPath(p);}return null;};
var asm=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
var service=asm.GetType("BlockLogger.ModUpdates.ModUpdateCheckerService",true)!;
const BindingFlags all=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
int checks=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
var folder=Path.Combine(Path.GetTempPath(),"nss-updater-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
try{
 object Validate(string manifest,string id="floralzonescaribbeanregion",string version="1.0.30",string? extra=null){string p=Path.Combine(folder,Guid.NewGuid()+".zip");using(var z=ZipFile.Open(p,ZipArchiveMode.Create)){using(var w=new StreamWriter(z.CreateEntry("modinfo.json").Open(),new UTF8Encoding(true)))w.Write(manifest);if(extra!=null){using var w=new StreamWriter(z.CreateEntry(extra).Open());w.Write("x");}}return service.GetMethod("ValidateDownloadedMod",all)!.Invoke(null,[p,id,version])!;}
 bool Success(object result)=>(bool)result.GetType().GetField("Item1")!.GetValue(result)!;
 string manifest="""{"Type":"content","ModID":"floralzonescaribbeanregion","Version":"1.0.30","Dependencies":{"game":"1.22.0"}}""";
 Check(Success(Validate(manifest)),"PascalCase Floral Zones manifests and UTF8 BOM accepted");
 Check(Success(Validate("""{/* comment */"modid":"floralzonescaribbeanregion","version":"1.0.30",}""")),"game-compatible comments and trailing comma accepted");
 Check(!Success(Validate(manifest.Replace("1.0.30","1.0.29"))),"wrong package version rejected");
 Check(!Success(Validate(manifest.Replace("floralzonescaribbeanregion","other"))),"wrong mod rejected");
 Check(!Success(Validate(manifest.Replace("\"Type\":", "\"modid\":\"evil\",\"Type\":"))),"case-conflicting duplicate identity rejected");
 Check(!Success(Validate(manifest,extra:"../bad")),"path traversal still rejected");
 Check(!Success(Validate(manifest.Replace("\"game\":\"1.22.0\"","\"game\":\"1.22.0\",\"GAME\":\"1.0.0\""))),"ambiguous dependency rejected");
 var handler=new FakeHandler();var instance=Activator.CreateInstance(service,[null,"1.22.7"])!;
 ((HttpClient)service.GetField("http",all)!.GetValue(instance)!).Dispose();service.GetField("http",all)!.SetValue(instance,new HttpClient(handler));
 async Task<object> Download(string id="fatemplar",string installed="1.0.0"){var t=(Task)service.GetMethod("GetRecommendedDownloadAsync",all)!.Invoke(instance,[id,installed])!;await t;return t.GetType().GetProperty("Result")!.GetValue(t)!;}
 var exact=await Download();Check(!Success(exact)&&handler.GameTags.SequenceEqual(new[]{"1.22.7"}),"exact tags default; no implicit compatibility broadening");
 handler.GameTags.Clear();service.GetProperty("AllowSameMinorGameUpdates")!.SetValue(instance,true);
 var relaxed=await Download();Check(Success(relaxed),"opt-in can find earlier same-minor patch tags");
 Check((string)relaxed.GetType().GetField("Item2")!.GetValue(relaxed)! == "1.3.0","highest compatible version selected across patch tags");
 Check(((string)relaxed.GetType().GetField("Item4")!.GetValue(relaxed)!).Contains("latest.zip"),"new release paired with its own attachment");
 Check(handler.GameTags.All(t=>t.StartsWith("1.22.")),"fallback never crosses major/minor version");
 var foreign=await Download("foreign");Check(!Success(foreign),"foreign download host rejected");
 var retracted=await Download("retracted");Check(!Success(retracted),"retraction response never becomes download");
 var config=Activator.CreateInstance(asm.GetType("BlockLogger.WebDashboard.WebDashboardConfig",true)!)!;
 Check(!(bool)config.GetType().GetProperty("AutomaticIncidentProfilingEnabled")!.GetValue(config)!,"automatic engine bursts default off");
 Check(!(bool)config.GetType().GetProperty("AllowSameMinorGameModUpdates")!.GetValue(config)!,"broader mod compatibility defaults off");
 string? realPackages=Environment.GetEnvironmentVariable("NSS_MOD_PACKAGE_FIXTURES");
 if(realPackages!=null)foreach(string file in Directory.EnumerateFiles(realPackages,"*.zip")){
  using var archive=ZipFile.OpenRead(file);var root=archive.Entries.Single(e=>e.FullName.Equals("modinfo.json",StringComparison.OrdinalIgnoreCase));
  using var reader=new StreamReader(root.Open());var fields=JsonDocument.Parse(reader.ReadToEnd()).RootElement.EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.Clone(),StringComparer.OrdinalIgnoreCase);
  object validation=service.GetMethod("ValidateDownloadedMod",all)!.Invoke(null,[file,fields["modid"].GetString()!,fields["version"].GetString()!])!;
  Check(Success(validation),"real package validation: "+Path.GetFileName(file)+" "+validation);
 }
 ((IDisposable)instance).Dispose();
 if(Environment.GetEnvironmentVariable("NSS_LIVE_MODDB")=="1"){
  var live=Activator.CreateInstance(service,[null,"1.22.7"])!;
  try{
   foreach(var (id,installed,broader) in new[]{("floralzonescaribbeanregion","1.0.29",false),("fagothic","2.1.1",false),("fahussar","1.1.5",true)}){
    service.GetProperty("AllowSameMinorGameUpdates")!.SetValue(live,broader);
    var task=(Task)service.GetMethod("GetRecommendedDownloadAsync",all)!.Invoke(live,[id,installed])!;await task;
    object selected=task.GetType().GetProperty("Result")!.GetValue(task)!;Check(Success(selected),"live release resolution: "+id+" "+selected);
    string version=(string)selected.GetType().GetField("Item2")!.GetValue(selected)!;
    string url=(string)selected.GetType().GetField("Item4")!.GetValue(selected)!;
    string path=Path.Combine(folder,id+".zip");var download=(Task)service.GetMethod("DownloadWithLimitAsync",all)!.Invoke(live,[url,path,256L*1024*1024])!;await download;
    object validation=service.GetMethod("ValidateDownloadedMod",all)!.Invoke(null,[path,id,version])!;
    Check(Success(validation),"live ZIP identity/version validation: "+id+" "+validation);
    Console.WriteLine($"LIVE PASS {id}: {installed} -> {version}; earlier-patch opt-in={broader}; download+manifest checked, no installation.");
   }
  }finally{((IDisposable)live).Dispose();}
 }
 Console.WriteLine($"{checks} updater/package/profiler-default checks passed against built Suite.");
}finally{Directory.Delete(folder,true);}
public sealed class FakeHandler:HttpMessageHandler
{
 public readonly List<string> GameTags=[];
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
 {
  var query=request.RequestUri!.Query.TrimStart('?').Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>Uri.UnescapeDataString(x[1]));
  string gv=query["gv"],id=query["ids"].Split('@')[0];GameTags.Add(gv);
  object info=gv=="1.22.7"?new{errorCode=4041}:
   id=="retracted"?new{errorCode=4102}:gv=="1.22.2"?new{recommendedUpgrade="1.3.0",fileName="latest.zip",fileUrl=id=="foreign"?"https://evil.example/latest.zip":"/download/latest.zip"}:
   new{recommendedUpgrade="1.2.0",fileName="old.zip",fileUrl="/download/old.zip"};
  return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{data=new Dictionary<string,object>{{id,info}}}),Encoding.UTF8,"application/json")});
 }
}
