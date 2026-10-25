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
Type T(string name)=>asm.GetType("BlockLogger.WebDashboard."+name,true)!;
var interpret=T("IncidentAuditInterpreter").GetMethod("Interpret")!;
void Check(bool condition,string detail){if(!condition)throw new Exception(detail);}
object New(string type,params (string Name,object Value)[] props){
 var t=T(type);var o=Activator.CreateInstance(t)!;
 foreach(var (n,v) in props)t.GetProperty(n)!.SetValue(o,v);return o;
}
object ListOf(string type,params object[] items){
 var l=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T(type)))!;
 foreach(var i in items)l.Add(i);return l;
}
object Player(string name,int total,int breaks=0,int places=0)=>New("IncidentAuditPlayer",("Uid",name+"-uid"),("Name",name),("Total",total),("Breaks",breaks),("Places",places),("Chunks",3));
object Area(int events,int players=2)=>New("IncidentAuditArea",("Dimension","0"),("ChunkX",4),("ChunkZ",-2),("BlockX",144),("BlockZ",-48),("Events",events),("Players",players));
object Count(string name,long count,string detail="")=>New("IncidentAuditCount",("Name",name),("Detail",detail),("Count",count));
// A 60-second window unless a test says otherwise.
object Audit(int total,int playerEvents,object? players=null,object? areas=null,object? actions=null,object? entities=null,long baseline=0,int baselineMinutes=0,bool available=true)=>
 New("IncidentAuditActivity",("Checked",true),("Available",available),("WindowStartUnixMilliseconds",1_000_000L),("WindowEndUnixMilliseconds",1_060_000L),
  ("TotalEvents",total),("PlayerEvents",playerEvents),("DistinctPlayers",2),("BaselineEvents",baseline),("BaselineMinutes",baselineMinutes),
  ("Players",players??ListOf("IncidentAuditPlayer")),("Areas",areas??ListOf("IncidentAuditArea")),
  ("Actions",actions??ListOf("IncidentAuditCount")),("Entities",entities??ListOf("IncidentAuditCount")));
List<(string Kind,string Confidence)> Run(object audit){
 interpret.Invoke(null,new[]{audit});
 var rows=new List<(string,string)>();
 foreach(var f in (IEnumerable)audit.GetType().GetProperty("Findings")!.GetValue(audit)!){
  string P(string n)=>(string)f.GetType().GetProperty(n)!.GetValue(f)!;rows.Add((P("Kind"),P("Confidence")));
 }return rows;
}

// Unavailable audit data never produces findings.
Check(Run(Audit(500,400,available:false)).Count==0,"Unavailable audit");

// One player dominating logged actions.
var burst=Run(Audit(400,400,players:ListOf("IncidentAuditPlayer",Player("Alex",360,breaks:300,places:60),Player("Sam",40))));
Check(burst.Any(f=>f.Kind=="Player"&&f.Confidence=="Low"),"Dominant player at 6/s is a low-confidence finding");
var fast=Run(Audit(1300,1300,players:ListOf("IncidentAuditPlayer",Player("Alex",1200,breaks:1200),Player("Sam",100))));
Check(fast.Any(f=>f.Kind=="Player"&&f.Confidence=="Medium"),"Dominant player at 20/s is a medium-confidence finding");
var even=Run(Audit(400,400,players:ListOf("IncidentAuditPlayer",Player("Alex",200),Player("Sam",200))));
Check(!even.Any(f=>f.Kind=="Player"),"Evenly split activity must not blame one player");
var small=Run(Audit(80,80,players:ListOf("IncidentAuditPlayer",Player("Alex",75),Player("Sam",5))));
Check(!small.Any(f=>f.Kind=="Player"),"Below the burst floor there is no finding");

// Activity concentrated in one chunk.
Check(Run(Audit(300,300,areas:ListOf("IncidentAuditArea",Area(220),Area(40)))).Any(f=>f.Kind=="Area"),"Concentrated area");
Check(!Run(Audit(300,300,areas:ListOf("IncidentAuditArea",Area(110),Area(100),Area(90)))).Any(f=>f.Kind=="Area"),"Spread activity is not concentrated");

// Entity churn from logged rows or bucketed aggregates.
Check(Run(Audit(600,0,actions:ListOf("IncidentAuditCount",Count("EntitySpawn",500)))).Any(f=>f.Kind=="Entities"&&f.Confidence=="Low"),"Entity spawns");
Check(Run(Audit(0,0,entities:ListOf("IncidentAuditCount",Count("game:drifter-normal",1500,"EntitySpawn")))).Any(f=>f.Kind=="Entities"&&f.Confidence=="Medium"),"Bucketed entity churn");
Check(!Run(Audit(600,0,actions:ListOf("IncidentAuditCount",Count("BlockBreak",500)))).Any(f=>f.Kind=="Entities"),"Block actions are not entity churn");

// Surge against the preceding baseline: 300 events in 60s vs 60 per minute before it.
Check(Run(Audit(300,300,baseline:600,baselineMinutes:10)).Any(f=>f.Kind=="Surge"),"5x surge");
Check(!Run(Audit(300,300,baseline:3000,baselineMinutes:10)).Any(f=>f.Kind=="Surge"),"Same rate as baseline is not a surge");
Check(Run(Audit(300,300,baseline:0,baselineMinutes:10)).Any(f=>f.Kind=="Surge"),"Activity after a silent baseline");

// Quiet windows are labelled, but only when nothing else was found.
Check(Run(Audit(5,5)).Any(f=>f.Kind=="Quiet"),"Quiet window");
Check(Run(Audit(50,50)).Count==0,"A mid-sized window with no pattern says nothing");

Console.WriteLine("PASS: player burst, area, entity churn, surge and quiet-window interpretation against shipping assembly");
