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
Type T(string name)=>asm.GetType("BlockLogger.ServerHealth."+name,true)!;
var analyzer=T("SlowTickAnalyzer");
var annotate=analyzer.GetMethod("Annotate")!;
var summarize=analyzer.GetMethod("Summarize")!;
void Check(bool condition,string detail){if(!condition)throw new Exception(detail);}
object Get(object o,string name)=>o.GetType().GetProperty(name)!.GetValue(o)!;
string Str(object o,string name)=>(string)Get(o,name);
object Contributor(string marker,double ms)=>Make("IncidentTickContributor",("Marker",marker),("WorkMs",ms));
object Make(string type,params (string Name,object Value)[] props){
 var t=T(type);var o=Activator.CreateInstance(t)!;
 foreach(var (n,v) in props)t.GetProperty(n)!.SetValue(o,v);return o;
}
object Tick(double tickMs,double instrumented,string marker,double leading,double[]? work=null,double gc=0,string modId="",string modName="",string confidence="Low",bool core=false){
 var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("IncidentTickContributor")))!;
 foreach(var (w,i) in (work??new[]{leading}).Select((w,i)=>(w,i)))list.Add(Contributor($"server/marker{i}",w));
 return Make("ServerProfileSlowTick",("UnixMilliseconds",1_700_000_000_000L),("TickMs",tickMs),("InstrumentedLeafMs",instrumented),
  ("LeadingMarker",marker),("LeadingWorkMs",leading),("GcPauseMs",gc),("ModId",modId),("ModName",modName),("Confidence",confidence),("IsCore",core),("Contributors",list));
}
object Annotated(object tick){annotate.Invoke(null,new[]{tick});return tick;}

// A single marker dominating the tick, owned by a confidently attributed mod.
var dominant=Annotated(Tick(200,190,"server/tick/Foo",150,new[]{150d,20d},modId:"foo",modName:"Foo Mod",confidence:"High"));
Check(Str(dominant,"Verdict")=="Single dominant cause","Dominant verdict: "+Str(dominant,"Verdict"));
Check(Str(dominant,"Owner")=="Foo Mod"&&Str(dominant,"OwnerKind")=="Mod","Mod owner");
Check(Str(dominant,"Why").Contains("Foo Mod")&&Str(dominant,"Why").Contains("75%"),"Why names the owner and share: "+Str(dominant,"Why"));
Check(Math.Abs((double)Get(dominant,"UnaccountedMs")-10)<0.001,"Unaccounted time");

// Engine work and low-confidence guesses are labelled honestly.
Check(Str(Annotated(Tick(120,110,"engine/chunks",100,core:true)),"OwnerKind")=="Engine","Engine owner");
var guessed=Annotated(Tick(120,110,"server/tick/Bar",100,modId:"bar",modName:"Bar",confidence:"Low"));
Check(Str(guessed,"OwnerKind")=="Unattributed"&&Str(guessed,"Owner")=="Unattributed","Low-confidence ownership is not named");

// A GC pause is called out, and a marker it may have inflated is not blamed on its own.
var gc=Annotated(Tick(300,100,"server/tick/Foo",100,gc:200,modId:"foo",modName:"Foo Mod",confidence:"High"));
Check(Str(gc,"Verdict")=="GC pause"&&Str(gc,"Why").Contains("200.0 ms"),"GC verdict: "+Str(gc,"Why"));
var inflated=Annotated(Tick(300,290,"server/tick/Foo",200,gc:200,modId:"foo",modName:"Foo Mod",confidence:"High"));
Check(Str(inflated,"Verdict")=="GC pause"&&Str(inflated,"Why").Contains("inflated"),"GC inside a dominant marker: "+Str(inflated,"Why"));
Check(Str(Annotated(Tick(300,290,"server/tick/Foo",200,gc:10)),"Verdict")!="GC pause","A small pause is not a GC verdict");

// Several contributors, unmarked time and a spread tick.
Check(Str(Annotated(Tick(100,80,"server/a",40,new[]{40d,25d,10d})),"Verdict")=="Several contributors","Several contributors");
var unmarked=Annotated(Tick(100,20,"server/a",15,new[]{15d}));
Check(Str(unmarked,"Verdict")=="Mostly unmarked time"&&Str(unmarked,"Why").Contains("80.0 ms"),"Unmarked time: "+Str(unmarked,"Why"));
Check(Str(Annotated(Tick(100,80,"server/a",20,new[]{20d,15d,10d,5d,5d,5d})),"Verdict")=="Spread across many markers","Spread");
Check(Str(Annotated(Tick(0,0,"",0)),"Verdict")=="","A zero-length tick gets no verdict");

// Roll-up across ticks.
T("ServerProfileSlowTick");
object Ticks(params object[] ticks){var l=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("ServerProfileSlowTick")))!;foreach(var t in ticks)l.Add(t);return l;}
var summary=summarize.Invoke(null,new[]{Ticks(
 Tick(200,190,"server/tick/Foo",150,new[]{150d,20d},modId:"foo",modName:"Foo Mod",confidence:"High"),
 Tick(180,170,"server/tick/Foo",140,new[]{140d,20d},modId:"foo",modName:"Foo Mod",confidence:"High"),
 Tick(90,40,"server/tick/Other",30,new[]{30d}))})!;
Check((int)Get(summary,"TotalTicks")==3&&Math.Abs((double)Get(summary,"WorstTickMs")-200)<0.001,"Totals");
var owners=((IEnumerable)Get(summary,"Owners")).Cast<object>().ToList();
Check(Str(owners[0],"Owner")=="Foo Mod"&&(int)Get(owners[0],"Ticks")==2,"Largest owner first");
Check(((IEnumerable)Get(summary,"Verdicts")).Cast<object>().Sum(v=>(int)Get(v,"Count"))==3,"Every tick has a verdict");
Check(Str(summary,"Headline").Contains("3 slow tick(s)")&&Str(summary,"Headline").Contains("Foo Mod led 2"),"Headline: "+Str(summary,"Headline"));
Check(Str(summarize.Invoke(null,new object?[]{null})!,"Headline").Contains("No slow ticks"),"Empty input");

Console.WriteLine("PASS: slow-tick cause classification, ownership honesty, GC handling and owner roll-up against shipping assembly");
