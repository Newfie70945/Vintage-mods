using System.Collections;
using System.Diagnostics;
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
var timerType=asm.GetType("BlockLogger.Services.SuiteBurstTimer",true)!;
var registry=asm.GetType("BlockLogger.Services.SuiteTimingRegistry",true)!;
var add=timerType.GetMethod("Add",new[]{typeof(long),typeof(long)})!;
var flush=timerType.GetMethod("Flush")!;
void Check(bool condition,string detail){if(!condition)throw new Exception(detail);}
object Timer(string metric,double window=1.0)=>Activator.CreateInstance(timerType,new object[]{metric,window})!;
void Add(object t,long elapsed,long now)=>add.Invoke(t,new object[]{elapsed,now});
(long Count,double Last,double Max)? Metric(string name){
 foreach(var row in (IEnumerable)registry.GetMethod("GetSnapshot")!.Invoke(null,null)!){
  var t=row.GetType();
  if((string)t.GetProperty("Name")!.GetValue(row)! == name)
   return ((long)t.GetProperty("TotalCount")!.GetValue(row)!,(double)t.GetProperty("LastMilliseconds")!.GetValue(row)!,(double)t.GetProperty("MaxMilliseconds")!.GetValue(row)!);
 }
 return null;
}
long freq=Stopwatch.Frequency;
long Ms(double ms)=>(long)(ms*freq/1000.0);

// Many small calls inside one window produce nothing until the window closes, then one total.
var a=Timer("test.burst.a");
long t0=1_000_000;
for(int i=0;i<100;i++)Add(a,Ms(.5),t0+i*(freq/200));              // 100 calls of 0.5 ms across half a second
Check(Metric("test.burst.a")==null,"Nothing is recorded before the window closes");
Add(a,Ms(.5),t0+freq);                                            // first call after the window closed
var first=Metric("test.burst.a");
Check(first is{Count:1}&&Math.Abs(first.Value.Last-50.5)<.05,"One record with the summed time: "+first);

// The next window is independent, and the maximum tracks the worst burst.
for(int i=0;i<10;i++)Add(a,Ms(.1),t0+freq+freq/10+i*(freq/100));
Add(a,Ms(.1),t0+3*freq);
var second=Metric("test.burst.a");
Check(second is{Count:2}&&Math.Abs(second.Value.Last-1.1)<.05&&Math.Abs(second.Value.Max-50.5)<.05,"Second window and worst-burst maximum: "+second);

// Flush records a partial window once and never records an empty one.
var b=Timer("test.burst.b");
Add(b,Ms(.2),t0);Add(b,Ms(.2),t0+freq/10);Add(b,Ms(.2),t0+freq/5);
flush.Invoke(b,null);
var flushed=Metric("test.burst.b");
Check(flushed is{Count:1}&&Math.Abs(flushed.Value.Last-.6)<.02,"Flush records the partial window: "+flushed);
flush.Invoke(b,null);
Check(Metric("test.burst.b") is{Count:1},"Flushing again with no new calls records nothing");

// Negative durations (a clock oddity) count as zero and never reduce the total.
var c=Timer("test.burst.c");
Add(c,-5000,t0);Add(c,Ms(1),t0+freq/10);flush.Invoke(c,null);
Check(Metric("test.burst.c") is{Count:1}&&Math.Abs(Metric("test.burst.c")!.Value.Last-1)<.02,"Negative duration is ignored");

// A flush after a long idle gap starts a fresh window instead of mixing old and new work.
var d=Timer("test.burst.d");
Add(d,Ms(2),t0);flush.Invoke(d,null);Add(d,Ms(3),t0+10*freq);flush.Invoke(d,null);
var idle=Metric("test.burst.d");
Check(idle is{Count:2}&&Math.Abs(idle.Value.Last-3)<.02&&Math.Abs(idle.Value.Max-3)<.02,"Separate windows after an idle gap: "+idle);

Console.WriteLine("PASS: burst timer sums small calls per window, flushes partial windows once, ignores negative durations and keeps windows independent");
