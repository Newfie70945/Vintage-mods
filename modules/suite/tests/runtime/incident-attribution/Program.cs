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
var actualWork=FreshArchive();var workRequests=(IList)Get(Get(actualWork,"WebServer"),"RecentSlowRequests");var work=Activator.CreateInstance(workRequests.GetType().GetGenericArguments()[0])!;Set(work,"UnixMilliseconds",9700L);Set(work,"MainThreadWorkMilliseconds",300d);var window=Make("BlockLogger.WebDashboard.WebMainThreadWorkDiagnostic");Set(window,"StartUnixMilliseconds",9700L);Set(window,"EndUnixMilliseconds",10000L);Set(window,"WallMilliseconds",300d);((IList)Get(work,"WorkWindows")).Add(window);workRequests.Add(work);Refresh(actualWork,noProfile);Check((string)Get(actualWork,"PrimaryCategory")=="Web dashboard","Observed overlapping web work ignored");
var small=FreshArchive();Set(small,"GcPauseMs",600d);var tiny=Make("BlockLogger.ServerHealth.IncidentProfilerEvidence");Set(tiny,"CompletedUnixMilliseconds",20000L);Set(tiny,"State","Measured tick overlap");var tinyTick=Make("BlockLogger.ServerHealth.ServerProfileSlowTick");Set(tinyTick,"TickMs",1000d);Set(tinyTick,"LeadingWorkMs",2d);Set(tinyTick,"LeadingMarker","tiny marker");((IList)Get(tiny,"Ticks")).Add(tinyTick);Refresh(small,tiny);Check((string)Get(small,"PrimaryCategory")=="GC pause evidence","Tiny profiled marker outranked a substantial measured GC pause");
var pressure=FreshArchive();Set(pressure,"MaxEntityDelta",1000);Set(pressure,"BlockLoggerDropped",100L);Refresh(pressure,noProfile);Check((string)Get(pressure,"AssessmentStatus")=="Correlation only","Pressure counters presented as measured causation");Check(((IList)Get(pressure,"EvidenceGaps")).Count>0,"Missing profile not explained");
var save=FreshArchive();Set(save,"WorldSaveWindowMs",2000d);Refresh(save,noProfile);Check((string)Get(save,"AssessmentStatus")=="Correlation only","Save overlap presented as measured execution");
var mixed=FreshArchive();Set(mixed,"GcPauseMs",600d);Set(mixed,"GcPauseTimingAvailable",true);Refresh(mixed,profile);Check((string)Get(mixed,"AssessmentStatus")=="Multiple measured contributors","Competing measured evidence hidden");Check(((string)Get(mixed,"NextAction")).Contains("Multiple measured contributors"),"Mixed evidence lacks specific guidance");
Set(tinyTick,"LeadingWorkMs",20d);var minor=FreshArchive();Set(minor,"WorstDelayMs",5000d);Refresh(minor,tiny);Check((string)Get(minor,"PrimaryCategory")=="Insufficient evidence","20ms marker blamed for 5s stall");
var net=FreshArchive();var eventRow=Make("BlockLogger.ServerHealth.SlowNetworkHandler");Set(eventRow,"Channel","fixture-network");Set(eventRow,"MessageType","FixtureRequest");Set(eventRow,"Handler","Fixture.Handle");Set(eventRow,"Assembly","FixtureMod");Set(eventRow,"WallMs",600d);((IList)Get(net,"NetworkHandlers")).Add(eventRow);Refresh(net,noProfile);Check(((string)Get(net,"PrimaryCategory")).StartsWith("Network handler: fixture-network"),"Measured channel evidence not ranked");Check((string)Get(net,"AssessmentStatus")=="Measured contributor","Handler evidence treated as counters");Set(eventRow,"GcPauseMs",590d);var gcNet=FreshArchive();Set(gcNet,"GcPauseMs",590d);((IList)Get(gcNet,"NetworkHandlers")).Add(eventRow);Refresh(gcNet,noProfile);Check((string)Get(gcNet,"PrimaryCategory")=="GC pause evidence","GC-paused handler falsely blamed for its whole wall interval");
Console.WriteLine($"PASS {count} shipping archive reassessment, late evidence, GC counters/pauses, ownership confidence and unrelated-latency checks");

// Leave history/API fields unset: this path must work without touching them.
var headerServiceType=T("BlockLogger.ServerHealth.ServerHealthService");
var headerService=RuntimeHelpers.GetUninitializedObject(headerServiceType);
void HeaderField(string name,object value)=>headerServiceType.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(headerService,value);
HeaderField("sync",new object());HeaderField("cachedSmartAnalysis",Make("BlockLogger.ServerHealth.ServerSmartAnalysis"));
HeaderField("latestSample",Make("BlockLogger.ServerHealth.ServerHealthSample"));
var header=headerServiceType.GetMethod("GetHeaderState")!.Invoke(headerService,null)!;
Check(header.GetType().GetProperty("current")!.GetValue(header)!=null,"Compact header depends on full history or game API");
Console.WriteLine("PASS compact header works without history collections, diagnostic analysis or game API.");

// Graph reads must work with no game API, Smart Diagnosis or database service.
var graph=RuntimeHelpers.GetUninitializedObject(headerServiceType);
void GraphField(string name,object value)=>headerServiceType.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(graph,value);
GraphField("sync",new object());
var sampleType=T("BlockLogger.ServerHealth.ServerHealthSample");
var queue=Activator.CreateInstance(typeof(Queue<>).MakeGenericType(sampleType))!;var enqueue=queue.GetType().GetMethod("Enqueue")!;
long graphNow=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
for(int i=0;i<3000;i++){var row=Make("BlockLogger.ServerHealth.ServerHealthSample");Set(row,"UnixMilliseconds",graphNow-3500000+i*1000L+(i>=1500?7000:0));Set(row,"Players",5);enqueue.Invoke(queue,new[]{row});}
GraphField("samples",queue);
var windows=Activator.CreateInstance(headerServiceType.GetField("worldSaveWindows",BindingFlags.Instance|BindingFlags.NonPublic)!.FieldType)!;GraphField("worldSaveWindows",windows);
var saveWindow=Activator.CreateInstance(T("BlockLogger.ServerHealth.WorldSaveLagWindow"),BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{graphNow-10000,500d},null)!;windows.GetType().GetMethod("Enqueue")!.Invoke(windows,new[]{saveWindow});
var graphSnapshot=headerServiceType.GetMethod("GetTimelineSnapshot")!.Invoke(graph,new object[]{600,3600})!;
Check((int)Get(graphSnapshot,"RawSampleCount")==3000,"Timeline lost raw sample count");
Check(((IList)Get(graphSnapshot,"Samples")).Count<=601,"Timeline graph reduction unbounded");
Check(((IList)Get(graphSnapshot,"WorldSaves")).Count==1,"Timeline lost world-save observations without Operations Center");
Check(((IList)Get(graphSnapshot,"SamplingGaps")).Count==1,"Display stride confused with real sampling gaps");
Check(Get(graphSnapshot,"Samples").GetType().GetGenericArguments()[0].GetProperties().Length==6,"Timeline accidentally returned full diagnostic sample payload");
Console.WriteLine("PASS lightweight timeline read with 3000 raw points, one true gap, compact projected samples and no game API/diagnostic dependencies.");

var untouchedWeb=RuntimeHelpers.GetUninitializedObject(serviceType);
var lastInspection=serviceType.GetMethod("GetLastPatchConflictSnapshot",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(untouchedWeb,null)!;
Check((bool)Get(lastInspection,"requiresManualInspection"),"Page inspection fallback must not execute Harmony inspection or require game API");
Console.WriteLine("PASS automatic page inspection placeholder is read-only and independent of heavy Harmony enumeration.");
