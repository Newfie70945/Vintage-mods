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
var archive=asm.GetType("BlockLogger.WebDashboard.PerformanceIncidentArchive",true)!;
var analyzer=asm.GetType("BlockLogger.WebDashboard.IncidentRecurrenceAnalyzer",true)!;
var analyze=analyzer.GetMethod("Analyze")!;
const long Minute=60_000;
void Check(bool condition,string detail){if(!condition)throw new Exception(detail);}
object Incident(long start,double delay=150,double save=0,string category="",string mod="",string modConfidence="Low"){
 var o=Activator.CreateInstance(archive)!;
 void Set(string name,object value)=>archive.GetProperty(name)!.SetValue(o,value);
 Set("StartUnixMilliseconds",start);Set("EndUnixMilliseconds",start+5_000);Set("WorstDelayMs",delay);
 Set("WorldSaveWindowMs",save);Set("PrimaryCategory",category);Set("LikelyModId",mod);Set("ModConfidence",modConfidence);
 return o;
}
List<(string Kind,string Title,string Confidence)> Run(params object[] incidents){
 var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(archive))!;
 foreach(var i in incidents)list.Add(i);
 var report=analyze.Invoke(null,new object[]{list})!;
 var rows=new List<(string,string,string)>();
 foreach(var f in (IEnumerable)report.GetType().GetProperty("Findings")!.GetValue(report)!){
  string P(string n)=>(string)f.GetType().GetProperty(n)!.GetValue(f)!;rows.Add((P("Kind"),P("Title"),P("Confidence")));
 }return rows;
}
long t0=1_700_000_000_000;

Check(Run().Count==0,"No incidents");
Check(Run(Incident(t0),Incident(t0+30*Minute)).Count==0,"Fewer than three incidents must not produce patterns");

// Steady 30-minute cadence with about a minute of jitter.
long[] jitter={0,Minute/2,-Minute/3,Minute,-Minute/2,Minute/4,0};
var steady=Enumerable.Range(0,7).Select(i=>Incident(t0+i*30*Minute+jitter[i])).ToArray();
var interval=Run(steady).FirstOrDefault(f=>f.Kind=="Interval");
Check(interval.Kind=="Interval","Steady cadence must be detected");
Check(System.Text.RegularExpressions.Regex.IsMatch(interval.Title,@"every 30(\.\d)? min"),"Interval title names the cadence: "+interval.Title);
Check(interval.Confidence!="Low","Six consistent gaps must not be low confidence");

// Irregular gaps must not look periodic.
long at=t0;var irregular=new List<object>{Incident(at)};
foreach(var gapMinutes in new[]{5,47,9,120,3,61})irregular.Add(Incident(at+=gapMinutes*Minute));
Check(!Run(irregular.ToArray()).Any(f=>f.Kind=="Interval"),"Irregular gaps must not be reported as an interval");

// Fragments of one slowdown (gaps under 30s) must not create a fake short interval.
var burst=Enumerable.Range(0,8).Select(i=>Incident(t0+i*10_000L)).ToArray();
Check(!Run(burst).Any(f=>f.Kind=="Interval"),"Same-episode fragments are not a cadence");

// World-save overlap.
var saves=Enumerable.Range(0,5).Select(i=>Incident(t0+i*Minute*(7+i*3),save:i<4?2_000:0)).ToArray();
Check(Run(saves).Any(f=>f.Kind=="Save"),"Repeated save overlap must be reported");
Check(!Run(Enumerable.Range(0,5).Select(i=>Incident(t0+i*Minute*(7+i*3))).ToArray()).Any(f=>f.Kind=="Save"),"No save overlap, no save finding");

// Repeated measured mod; unmeasured (Low) ownership is ignored.
var mods=new[]{Incident(t0,mod:"examplemod",modConfidence:"High"),Incident(t0+11*Minute,mod:"examplemod",modConfidence:"Medium"),
 Incident(t0+29*Minute,mod:"examplemod",modConfidence:"High"),Incident(t0+31*Minute)};
var mod=Run(mods).FirstOrDefault(f=>f.Kind=="Mod");
Check(mod.Kind=="Mod"&&mod.Confidence=="High","Repeated measured mod ownership");
var guessed=new[]{Incident(t0,mod:"examplemod"),Incident(t0+11*Minute,mod:"examplemod"),Incident(t0+29*Minute,mod:"examplemod")};
Check(!Run(guessed).Any(f=>f.Kind=="Mod"),"Low-confidence ownership must not be repeated as a finding");

// Repeated assessment; "Insufficient evidence" never counts as a pattern.
var repeated=Enumerable.Range(0,4).Select(i=>Incident(t0+i*Minute*(9+i*5),category:"GC pause evidence")).ToArray();
Check(Run(repeated).Any(f=>f.Kind=="Category"),"Repeated assessment");
var unknown=Enumerable.Range(0,4).Select(i=>Incident(t0+i*Minute*(9+i*5),category:"Insufficient evidence")).ToArray();
Check(!Run(unknown).Any(f=>f.Kind=="Category"),"Insufficient evidence is not a pattern");

// Worsening trend.
double[] delays={110,120,100,130,260,300,280,320};
var worse=delays.Select((d,i)=>Incident(t0+i*Minute*(11+i*4),delay:d)).ToArray();
Check(Run(worse).Any(f=>f.Kind=="Trend"),"Worsening trend");
var flat=delays.Select((d,i)=>Incident(t0+i*Minute*(11+i*4),delay:120)).ToArray();
Check(!Run(flat).Any(f=>f.Kind=="Trend"),"Flat delays are not a trend");

Console.WriteLine("PASS: cadence, save overlap, repeated mod/assessment and trend detection against shipping assembly");
