using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
string game=Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY")!);
string path=Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!);
AssemblyLoadContext.Default.Resolving+=(context,name)=>{
 foreach(string root in new[]{Path.GetDirectoryName(path)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){string file=Path.Combine(root,name.Name+".dll");if(File.Exists(file))return context.LoadFromAssemblyPath(file);}return null;
};
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
Type T(string name)=>assembly.GetType(name)!;
object Make(string name)=>Activator.CreateInstance(T(name))!;
void Set(object target,string name,object value)=>target.GetType().GetProperty(name)!.SetValue(target,value);
object Get(object target,string name)=>target.GetType().GetProperty(name)!.GetValue(target)!;
var serviceType=T("BlockLogger.WebDashboard.WebDashboardService");object service=RuntimeHelpers.GetUninitializedObject(serviceType);
var refresh=serviceType.GetMethod("RefreshArchivedIncidentEvidence",BindingFlags.Instance|BindingFlags.NonPublic)!;
object FreshArchive(){var a=Make("BlockLogger.WebDashboard.PerformanceIncidentArchive");Set(a,"PrimaryCategory","Garbage collection");Set(a,"LikelyModId","old-unmatched-mod");Set(a,"Gen2Collections",10);Set(a,"StartUnixMilliseconds",10000L);Set(a,"EndUnixMilliseconds",10000L);Set(a,"WorstDelayMs",1000d);return a;}
bool Refresh(object a,object e)=>(bool)refresh.Invoke(service,[a,e])!;
int count=0;void Check(bool ok,string why){if(!ok)throw new Exception(why);count++;}
var archive=FreshArchive();var noProfile=Make("BlockLogger.ServerHealth.IncidentProfilerEvidence");
Check(Refresh(archive,noProfile),"Legacy archive not reassessed");Check((string)Get(archive,"PrimaryCategory")=="Insufficient evidence","Collection counts still dominate verdict");Check((string)Get(archive,"OriginalPrimaryCategory")=="Garbage collection","Original assessment lost");Check((string)Get(archive,"LikelyModId")=="","Legacy unmatched mod attribution kept");Check(!Refresh(archive,noProfile),"Unchanged archive rewritten every poll");
var profile=Make("BlockLogger.ServerHealth.IncidentProfilerEvidence");Set(profile,"CompletedUnixMilliseconds",20000L);Set(profile,"State","Measured tick overlap");Set(profile,"CaptureKind","Manual");var tick=Make("BlockLogger.ServerHealth.ServerProfileSlowTick");Set(tick,"TickMs",800d);Set(tick,"LeadingWorkMs",500d);Set(tick,"LeadingMarker","fixture:AiTask");Set(tick,"ModId","fixture");Set(tick,"ModName","Fixture AI");Set(tick,"Confidence","High");((IList)Get(profile,"Ticks")).Add(tick);
Check(Refresh(archive,profile),"Late profile did not enrich existing archive");Check(((string)Get(archive,"PrimaryCategory")).Contains("Fixture AI"),"Measured contributor missing from verdict");Check((string)Get(archive,"LikelyModId")=="fixture","Owner candidate not retained");Check((string)Get(archive,"Confidence")=="Medium","Ownership mistaken for proven causation");Check(!Refresh(archive,noProfile),"Missing later data erased measured evidence");
var followup=Make("BlockLogger.ServerHealth.IncidentProfilerEvidence");Set(followup,"CompletedUnixMilliseconds",30000L);Set(followup,"State","After-spike capture");Check(!Refresh(archive,followup),"After-spike capture overwrote direct evidence");
var gc=FreshArchive();Set(gc,"GcPauseMs",600d);Refresh(gc,noProfile);Check((string)Get(gc,"PrimaryCategory")=="GC pause evidence","Large measured pause not surfaced");
var wait=FreshArchive();var db=Get(wait,"Database");Set(db,"P95QueryMilliseconds",10000d);Refresh(wait,noProfile);Check((string)Get(wait,"PrimaryCategory")=="Insufficient evidence","Rolling DB latency treated as incident cause");
var queued=FreshArchive();var web=Get(queued,"WebServer");var requests=(IList)Get(web,"RecentSlowRequests");var request=Activator.CreateInstance(requests.GetType().GetGenericArguments()[0])!;
Set(request,"UnixMilliseconds",10000L);Set(request,"TotalMilliseconds",20001d);Set(request,"MainThreadWaitMilliseconds",20000d);Set(request,"QueueWaitMilliseconds",20000d);Set(request,"MainThreadWorkMilliseconds",1d);requests.Add(request);Refresh(queued,noProfile);Check((string)Get(queued,"PrimaryCategory")=="Insufficient evidence","Waiting for stalled thread mistaken for causative web work");
var actualWork=FreshArchive();var workRequests=(IList)Get(Get(actualWork,"WebServer"),"RecentSlowRequests");var work=Activator.CreateInstance(workRequests.GetType().GetGenericArguments()[0])!;Set(work,"UnixMilliseconds",10000L);Set(work,"MainThreadWorkMilliseconds",300d);workRequests.Add(work);Refresh(actualWork,noProfile);Check((string)Get(actualWork,"PrimaryCategory")=="Web dashboard","Observed overlapping web work ignored");
var small=FreshArchive();Set(small,"GcPauseMs",600d);var tiny=Make("BlockLogger.ServerHealth.IncidentProfilerEvidence");Set(tiny,"CompletedUnixMilliseconds",20000L);Set(tiny,"State","Measured tick overlap");var tinyTick=Make("BlockLogger.ServerHealth.ServerProfileSlowTick");Set(tinyTick,"TickMs",1000d);Set(tinyTick,"LeadingWorkMs",2d);Set(tinyTick,"LeadingMarker","tiny marker");((IList)Get(tiny,"Ticks")).Add(tinyTick);Refresh(small,tiny);Check((string)Get(small,"PrimaryCategory")=="GC pause evidence","Tiny profiled marker outranked a substantial measured GC pause");
Console.WriteLine($"PASS {count} shipping archive reassessment, late evidence, GC counters/pauses, ownership confidence and unrelated-latency checks");
