using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
string dll=Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!);
AssemblyLoadContext.Default.Resolving+=(ctx,name)=>{foreach(string folder in new[]{Path.GetDirectoryName(dll)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){string f=Path.Combine(folder,name.Name+".dll");if(File.Exists(f))return ctx.LoadFromAssemblyPath(f);}return null;};
var asm=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);var service=asm.GetType("BlockLogger.ServerHealth.ServerHealthService",true)!;
var identity=service.GetNestedType("ModIdentity",BindingFlags.NonPublic)!;var token=service.GetNestedType("ModToken",BindingFlags.NonPublic)!;
var catalog=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(identity))!;
for(int mod=0;mod<10;mod++){
 object row=Activator.CreateInstance(identity,true)!;identity.GetField("ModId")!.SetValue(row,"fixture"+mod);identity.GetField("Name")!.SetValue(row,"Fixture "+mod);
 var tokens=(IList)identity.GetProperty("Tokens")!.GetValue(row)!;
 for(int t=0;t<50;t++)tokens.Add(Activator.CreateInstance(token,["HandlerForFixture"+mod+"Type"+t,90,"Medium"])!);
 tokens.Add(Activator.CreateInstance(token,["fixture"+mod+":",120,"High"])!);catalog.Add(row);
}
object instance=RuntimeHelpers.GetUninitializedObject(service);service.GetField("modIdentities",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(instance,catalog);
var method=service.GetMethod("ResolveModAttributionCore",BindingFlags.NonPublic|BindingFlags.Instance)!;
var paths=Enumerable.Range(0,100).Select(i=>i%3==0?"server/Handler-For-Fixture"+(i%10)+"-Type"+(i%50)+"/tick" :i%3==1?"server/generic-very-long-unknown-marker-"+i:"server/fixture"+(i%10)+":work").Concat(new[]{"Vintagestory.Server.ServerMain.Tick","server/Älä-İSTANBUL/123","abc",new string('A',600)+"/work"}).ToArray();
var arguments=paths.Select(p=>new object[]{p}).ToArray();
string Run(){var rows=new StringBuilder();foreach(var arg in arguments){object result=method.Invoke(instance,arg)!;rows.Append(JsonSerializer.Serialize(result));}return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rows.ToString())));}
string signature=Run();
// Compare the new normalization with the old character semantics, including Unicode.
var normalize=service.GetMethod("NormalizeToken",BindingFlags.Static|BindingFlags.NonPublic)!;
foreach(var text in paths){string expected=new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());if((string)normalize.Invoke(null,[text])! !=expected)throw new Exception("Normalization changed");}
for(int i=0;i<5;i++)foreach(var arg in arguments)method.Invoke(instance,arg);
const int iterations=20;long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
for(int i=0;i<iterations;i++)foreach(var arg in arguments)method.Invoke(instance,arg);
double ms=Stopwatch.GetElapsedTime(start).TotalMilliseconds;bytes=GC.GetAllocatedBytesForCurrentThread()-bytes;
if(Run()!=signature)throw new Exception("Attribution drift");
var output=new{paths=paths.Length,tokens=510,iterations,resultSha256=signature,millisecondsPerPath=ms/(iterations*paths.Length),bytesPerPath=bytes/(double)(iterations*paths.Length),scope="Built Suite attribution matcher, synthetic catalog and marker paths. Reflection argument arrays preallocated. Normalization output checked against previous character semantics; not production capture overhead."};
string json=JsonSerializer.Serialize(output,new JsonSerializerOptions{WriteIndented=true});Console.WriteLine(json);
if(Environment.GetEnvironmentVariable("NSS_MEASUREMENTS") is string path)File.WriteAllText(path,json+"\n");
