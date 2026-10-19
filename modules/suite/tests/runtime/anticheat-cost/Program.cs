using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")??throw new Exception("Set VINTAGE_STORY"),dll=Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY")??throw new Exception("Set NSS_ASSEMBLY"));
var roots=new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods"),Path.GetDirectoryName(dll)!};
AssemblyLoadContext.Default.Resolving+=(context,name)=>{foreach(string root in roots){string file=Path.Combine(root,name.Name+".dll");if(File.Exists(file))return context.LoadFromAssemblyPath(file);}return null;};
var mod=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);Type T(string name)=>mod.GetType("BlockLogger.AntiCheat."+name,true)!;
const BindingFlags fields=BindingFlags.NonPublic|BindingFlags.Instance;
object config=Activator.CreateInstance(T("AntiCheatConfig"))!;bool familyRisk=Environment.GetEnvironmentVariable("NSS_FAMILY_RISK")=="1";config.GetType().GetProperty("PerFamilyRiskEnabled")!.SetValue(config,familyRisk);((IList)config.GetType().GetProperty("RequiredClientModIds")!.GetValue(config)!).Clear();
object service=RuntimeHelpers.GetUninitializedObject(T("AntiCheatService"));
void Set(string key,object value)=>service.GetType().GetField(key,fields)!.SetValue(service,value);
Set("config",config);Set("evaluator",Activator.CreateInstance(T("AntiCheatEvaluator"),config)!);Set("canonicalModLock",new object());Set("coreAssemblyLock",new object());
foreach(string key in new[]{"serverCanonicalMods","serverCoreAssemblies"}){var field=service.GetType().GetField(key,fields)!;var genericTypes=field.FieldType.GetGenericArguments();field.SetValue(service,Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(genericTypes))!);}
object session=Activator.CreateInstance(T("AntiCheatSession"))!;session.GetType().GetProperty("LastScanUtc")!.SetValue(session,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());var mods=(IList)session.GetType().GetProperty("LastMods")!.GetValue(session)!;
var modType=mod.GetType("NewfiesAntiCheat.Shared.Models.ClientModDto",true)!;
for(int i=0;i<200;i++){object row=Activator.CreateInstance(modType)!;modType.GetProperty("ModId")!.SetValue(row,"fixturemod"+i);modType.GetProperty("Version")!.SetValue(row,"1.0.0");mods.Add(row);}
var findingType=mod.GetType("NewfiesAntiCheat.Shared.Models.ScanFindingDto",true)!;object finding=Activator.CreateInstance(findingType)!;findingType.GetProperty("Code")!.SetValue(finding,"PACKET_UNAUTHORIZED_MOVEMENT");findingType.GetProperty("Scanner")!.SetValue(finding,"ServerBehavior");findingType.GetProperty("Severity")!.SetValue(finding,4);((IList)session.GetType().GetProperty("Findings")!.GetValue(session)!).Add(finding);
var method=service.GetType().GetMethod("RebuildSessionRiskFromCurrentEvidence",fields)!;object?[] arguments=method.GetParameters().Length==1?[session]:[session,false];
// Begin from the same fully evaluated client evidence in both builds.
method.Invoke(service,method.GetParameters().Length==1?[session]:[session,true]);
for(int i=0;i<100;i++)method.Invoke(service,arguments);
const int iterations=1000;long allocated=GC.GetAllocatedBytesForCurrentThread();var clock=Stopwatch.StartNew();for(int i=0;i<iterations;i++)method.Invoke(service,arguments);clock.Stop();allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
Console.WriteLine(JsonSerializer.Serialize(new{scenario="isolated behavior-risk recalculation; 200 synthetic client mod rows; no game clients",iterations,familyRisk,clientMods=mods.Count,microsecondsPerCall=clock.Elapsed.TotalMicroseconds/iterations,allocatedBytesPerCall=allocated/iterations,risk=session.GetType().GetProperty("RiskScore")!.GetValue(session),fastPath=arguments.Length==2}));
