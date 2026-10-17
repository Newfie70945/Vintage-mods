using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
string suitePath=Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!,motdPath=Environment.GetEnvironmentVariable("MOTD_ASSEMBLY")!;
AssemblyLoadContext.Default.Resolving+=(context,name)=>{foreach(string root in new[]{Path.GetDirectoryName(suitePath)!,Path.GetDirectoryName(motdPath)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){string file=Path.Combine(root,name.Name+".dll");if(File.Exists(file))return context.LoadFromAssemblyPath(file);}return null;};
var suite=AssemblyLoadContext.Default.LoadFromAssemblyPath(suitePath);var motd=AssemblyLoadContext.Default.LoadFromAssemblyPath(motdPath);
const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
void Check(bool value,string message){if(!value)throw new Exception(message);}
object New(Assembly assembly,string name)=>Activator.CreateInstance(assembly.GetType(name,true)!)!;
object Get(object value,string name)=>value.GetType().GetProperty(name,Flags)?.GetValue(value)??value.GetType().GetField(name,Flags)!.GetValue(value)!;
void Set(object value,string name,object? data){var p=value.GetType().GetProperty(name,Flags);if(p!=null)p.SetValue(value,data);else value.GetType().GetField(name,Flags)!.SetValue(value,data);}
object? Call(object value,string name,params object?[] args)=>value.GetType().GetMethod(name,Flags)!.Invoke(value,args);
var store=New(suite,"BlockLogger.WebDashboard.OperationsCenterStore");var alerts=(IList)Get(store,"Alerts");var actions=(IList)Get(store,"Actions");
object Alert(string id){var x=New(suite,"BlockLogger.WebDashboard.ServerAlertRecord");Set(x,"Id",id);Set(x,"Key","key-"+id);Set(x,"Title","Alert "+id);Set(x,"RecommendedAction","Investigate");return x;}
object ActionRow(string id,string source){var x=New(suite,"BlockLogger.WebDashboard.ServerActionRecord");Set(x,"Id",id);Set(x,"SourceKey",source);return x;}
alerts.Add(Alert("reviewed"));alerts.Add(Alert("new-arrival"));actions.Add(ActionRow("alert:reviewed","key-reviewed"));actions.Add(ActionRow("smart-1","smart-1"));
var clear=suite.GetType("BlockLogger.WebDashboard.ActionCenterWorkflow",true)!.GetMethod("Clear")!;
int Clear(string target,params string[] ids)=>(int)clear.Invoke(null,new object[]{store,target,ids})!;
Check(Clear("alerts","reviewed","reviewed")==1,"Duplicate reviewed IDs over-counted");Check(((IList)Get(store,"Alerts")).Count==1&&((string)Get(((IList)Get(store,"Alerts"))[0]!,"Id"))=="new-arrival","New unreviewed alert was cleared");
Check(((IList)Get(store,"Actions")).Count==1,"Linked recommendation survived alert removal");Check(Clear("actions","smart-1")==1&&Clear("actions","smart-1")==0,"Clear must be idempotent");
Check((string)Get(((IList)Get(store,"Actions"))[0]!,"Status")=="Cleared","Action remains visible");
var service=RuntimeHelpers.GetUninitializedObject(suite.GetType("BlockLogger.WebDashboard.WebDashboardService",true)!);Set(service,"operationsCenterStore",store);Set(service,"config",New(suite,"BlockLogger.WebDashboard.WebDashboardConfig"));
var snapshot=New(suite,"BlockLogger.ServerHealth.ServerHealthSnapshot");var memory=New(suite,"BlockLogger.WebDashboard.MemoryGcAnalysis");
Call(service,"RebuildActionQueue",snapshot,memory);var rows=(IList)Get(store,"Actions");Check(rows.Count==1,"Unexpected action queue fixture");var current=rows[0]!;Clear("actions",(string)Get(current,"Id"));Call(service,"RebuildActionQueue",snapshot,memory);Check((string)Get(((IList)Get(store,"Actions"))[0]!,"Status")=="Cleared","Cleared active condition immediately resurfaced");
((IList)Get(store,"Alerts")).Clear();Call(service,"RebuildActionQueue",snapshot,memory);Check(((IList)Get(store,"Actions")).Count==0,"Cleared condition did not leave queue");((IList)Get(store,"Alerts")).Add(Alert("recurrence"));Call(service,"RebuildActionQueue",snapshot,memory);Check((string)Get(((IList)Get(store,"Actions"))[0]!,"Status")=="Open","Recurrent condition was permanently hidden");
Console.WriteLine("PASS: compiled Action Center clear is ID-scoped, idempotent and preserves new arrivals; linked recommendations clear; active dismissed recommendations stay hidden, disappear on recovery and return on recurrence.");
var server=New(motd,"NewfiesCommunityMOTD.MotdServerSystem");var config=New(motd,"NewfiesCommunityMOTD.MotdConfig");Set(server,"cfg",config);Set(config,"MergeLocalAndDiscord",true);Set(config,"DiscordMaxItemsPerFeed",2);
var itemType=motd.GetType("NewfiesCommunityMOTD.MotdFeedItem",true)!;var feed=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;
object Item(string id,long time,string title){var x=Activator.CreateInstance(itemType)!;Set(x,"Id",id);Set(x,"UnixTime",time);Set(x,"Title",title);Set(x,"BodyVtml","Body");Set(x,"Author","Author");Set(x,"Url","javascript:bad");return x;}
feed.Add(Item("old",1,"Old"));feed.Add(Item("fresh",3,"<Fresh>"));feed.Add(Item("fresh",2,"Duplicate"));feed.Add(Item("mid",2,"Middle"));
string Build(string local)=>(string)Call(server,"BuildFeed","Announcements",local,true,feed)!;
var ids=(IList)Call(server,"FeedIds",feed,true,"Local note")!;string rendered=Build("Local note");Check(ids.Count==3&&((string)ids[0]!).StartsWith("local:")&&(string)ids[1]! =="fresh"&&(string)ids[2]! =="mid","Feed IDs don't follow rendered selection/order");Check(rendered.Contains("Local note")&&rendered.Contains("Fresh")&&!rendered.Contains("<Fresh>")&&!rendered.Contains("Duplicate")&&!rendered.Contains("javascript:"),"Feed merge/escaping/link filtering failed");Check(rendered.IndexOf("Fresh")<rendered.IndexOf("Middle"),"Feed is not newest first");Check(rendered.Contains("UTC"),"Feed timezone ambiguous");
string key=(string)ids[0]!;var changed=(IList)Call(server,"FeedIds",feed,true,"Changed note")!;Check((string)changed[0]! !=key,"Local content changes don't generate unread identity");Set(config,"MergeLocalAndDiscord",false);Check(!Build("Local note").Contains("Local note")&&((IList)Call(server,"FeedIds",feed,true,"Local note")!).Count==2,"Merge toggle ignored");feed.Clear();Check(Build("Local note").Contains("Local note"),"Local fallback lost when Discord has no entries");
feed.Add(Item("",5,"Bracket ] title"));
var unusualIds=(IList)Call(server,"FeedIds",feed,true,null)!;Check(((string)unusualIds[0]!).StartsWith("entry:")&&!((string)unusualIds[0]!).Contains(']'),"Fallback feed ID broke unread marker syntax");
var parser=AssemblyLoadContext.Default.Assemblies.Single(x=>x.GetName().Name=="VintagestoryAPI").GetType("Vintagestory.API.Common.VtmlParser",true)!;
var tokenize=parser.GetMethods(BindingFlags.Public|BindingFlags.Static).Single(x=>x.Name=="Tokenize");
foreach(string heading in new[]{"Announcements","Updates","Events"}){
 string document=(string)Call(server,"BuildFeed",heading,"<strong>Local content</strong>",true,feed)!;
 var parameters=tokenize.GetParameters().Select(x=>x.ParameterType==typeof(string)?(object)document:null).ToArray();
 Check(((Array)tokenize.Invoke(null,parameters)!).Length>0,"Game VTML parser rejected feed");
 if(heading=="Events")Check(!document.Contains("MOTDNEW"),"Events leaked unread marker");
 else Check(document.Contains("size=\"22\"")&&document.Contains("size=\"16\"")&&document.Contains("#96A8B8"),"Feed title/body hierarchy missing");
}
Console.WriteLine("PASS: compiled MOTD newest-first deduplicated feeds, stable/changed local unread IDs, matching visible IDs, merge-off/local fallback, escaped metadata and rejected unsafe links. No live server or Discord connection.");
