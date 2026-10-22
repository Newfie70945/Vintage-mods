using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
string game=Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY")!);
string path=Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!);
AssemblyLoadContext.Default.Resolving+=(c,n)=>{foreach(var r in new[]{Path.GetDirectoryName(path)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){string p=Path.Combine(r,n.Name+".dll");if(File.Exists(p))return c.LoadFromAssemblyPath(p);}return null;};
var asm=AssemblyLoadContext.Default.LoadFromAssemblyPath(path);var lib=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game,"VintagestoryLib.dll"));var api=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game,"VintagestoryAPI.dll"));
const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
var ct=asm.GetType("BlockLogger.ServerHealth.NetworkHandlerTimings")!;var collector=Activator.CreateInstance(ct,true)!;
object? Call(string n,params object?[] a)=>ct.GetMethod(n,flags)!.Invoke(collector,a);object Field(string n)=>ct.GetField(n,flags)!.GetValue(collector)!;
var nt=lib.GetType("Vintagestory.Server.NetworkChannel")!;var dto=asm.GetType("BlockLogger.PlayerList.PublicPlayerListRequest")!;var pt=lib.GetType("Packet_CustomPacket")!;
var profilerType=api.GetType("Vintagestory.API.Common.FrameProfilerUtil")!;var profiler=Activator.CreateInstance(profilerType,[new Action<string>(_=>{})]);lib.GetType("Vintagestory.Server.ServerMain")!.GetField("FrameProfiler")!.SetValue(null,profiler);
var handlerType=api.GetType("Vintagestory.API.Server.NetworkClientMessageHandler`1")!.MakeGenericType(dto);var handler=Delegate.CreateDelegate(handlerType,typeof(Fixture).GetMethod(nameof(Fixture.Handle))!);
object Channel(string name){var c=Activator.CreateInstance(nt,[null,42,name])!;nt.GetMethods().Single(m=>m.Name=="RegisterMessageType"&&m.IsGenericMethodDefinition&&m.DeclaringType==nt).MakeGenericMethod(dto).Invoke(c,null);nt.GetMethod("SetMessageHandler")!.MakeGenericMethod(dto).Invoke(c,[handler]);return c;}
var packet=Activator.CreateInstance(pt)!;pt.GetField("MessageId")!.SetValue(packet,0);pt.GetField("Data")!.SetValue(packet,Array.Empty<byte>());
var onPacket=nt.GetMethod("OnPacket")!;object?[] invokeArgs=[packet,null];var a=Channel("fixture-a");var b=Channel("fixture-b");
void Dispatch(object c)=>onPacket.Invoke(c,invokeArgs);
IList Match(){long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();return (IList)Call("Match",now-1000,now,1000d)!;}
object Get(object x,string n)=>x.GetType().GetProperty(n)!.GetValue(x)!;
int checks=0;void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
try{
 Dispatch(a); // Warm protobuf/JIT before measuring the patched dispatcher.
 Call("Start");Check(((string)ct.GetProperty("Status",flags)!.GetValue(collector)!).StartsWith("Active"),"Timing hook unavailable");
 Fixture.Delay=35;Dispatch(a);Dispatch(b);var rows=Match();Check(rows.Count==2,"Slow handler events missing");Check(rows.Cast<object>().Select(x=>(string)Get(x,"Channel")).Distinct().Count()==2,"Same message IDs conflated channels");
 var row=rows[0]!;Check((string)Get(row,"MessageType")==dto.FullName,"Message type not resolved");Check(((string)Get(row,"Handler")).Contains("Fixture.Handle"),"Mod delegate hidden behind engine wrapper");Check((string)Get(row,"Assembly")==typeof(Fixture).Assembly.GetName().Name,"Handler assembly incorrect");Check((int)Get(row,"PayloadBytes")==0,"Payload size changed");Check((double)Get(row,"WallMs")>=25,"Wall duration not captured");
 Check(((IList)Call("Match",1L,100L,0d)!).Count==0,"Unrelated handler attached to historical incident");
 Fixture.Throw=true;try{Dispatch(a);throw new Exception("Handler exception swallowed");}catch(TargetInvocationException e){Check(e.InnerException is FixtureFailure,"Original handler exception replaced");}Fixture.Throw=false;Check(Match().Cast<object>().Any(x=>(string)Get(x,"Error")==nameof(FixtureFailure)),"Failed slow interval not recorded");
 Call("Stop");Check(Match().Count==0,"Disabled collector retained live events");Check(((IDictionary)Field("identities")).Count==0,"Disabled collector retained channel cache");
 Fixture.Delay=0;for(int i=0;i<1000;i++)Dispatch(a);Check(Match().Count==0,"Disabled collector recorded packets");
 Call("Start");for(int i=0;i<100;i++)Dispatch(a);Check(Match().Count==0,"Fast requests retained as slow evidence");
 // Exercise bounded retention and generation checks without artificial sleeps.
 var before=ct.GetMethod("Before",flags)!;object?[] stateArgs=[null];before.Invoke(null,stateArgs);var state=stateArgs[0]!;
 for(int i=0;i<300;i++)Call("Record",a,packet,state,30d,null);
 Check(((ICollection)Field("events")).Count==256,"Slow event ring unbounded");Check(Match().Count==8,"Incident payload not bounded");
 Call("Stop");Call("Start");Call("Record",a,packet,state,30d,null);Check(Match().Count==0,"Stale packet completion crossed enable generation");
 before.Invoke(null,stateArgs);var fresh=stateArgs[0]!;Call("Record",a,packet,fresh,30d,null);var expired=((IEnumerable)Field("events")).Cast<object>().First();expired.GetType().GetProperty("EndUnixMilliseconds")!.SetValue(expired,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-120001);Match();Check(((ICollection)Field("events")).Count==0,"Expired live timing record retained on read");
 for(int i=0;i<550;i++)Call("Record",Channel("cache-"+i),packet,fresh,30d,null);Check(((IDictionary)Field("identities")).Count<=512,"Channel identity cache unbounded");Call("Stop");Call("Start");
 // Local microbenchmark only: no world/player workload or TPS claim.
 (double Ms,long Bytes) Bench(){long alloc=GC.GetAllocatedBytesForCurrentThread();var w=Stopwatch.StartNew();for(int i=0;i<20000;i++)Dispatch(a);return(w.Elapsed.TotalMilliseconds,GC.GetAllocatedBytesForCurrentThread()-alloc);}
 Call("Stop");var off=Bench();Call("Start");var on=Bench();
 Check(on.Bytes<=off.Bytes+160000,"Fast-packet diagnostics introduced per-packet recording allocations");
 Console.WriteLine($"PASS {checks} official dispatcher checks: channel/message/owner, real wrapped handlers, exception preservation, inactive path, bounds and stale generations.");
 Console.WriteLine($"LOCAL 20000 dispatches: disabled {off.Ms:0.0}ms / {off.Bytes}B; enabled {on.Ms:0.0}ms / {on.Bytes}B. This is not a server workload benchmark.");
}finally{Call("Dispose");}
public sealed class FixtureFailure:Exception{}
public static class Fixture{public static int Delay;public static bool Throw;public static void Handle(object? player,object? packet){if(Delay>0)Thread.Sleep(Delay);if(Throw)throw new FixtureFailure();}}
