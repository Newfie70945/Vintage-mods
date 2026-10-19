using System.Collections;
using System.Reflection;
using System.Runtime.Loader;

string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY."));
string modPath = Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? throw new Exception("Set NSS_ASSEMBLY."));
var roots = new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods"), Path.GetDirectoryName(modPath)! };
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (string root in roots) { string file = Path.Combine(root, name.Name + ".dll"); if (File.Exists(file)) return context.LoadFromAssemblyPath(file); }
    return null;
};
var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);
Type T(string name) => mod.GetType(name, true)!;
var findingType = T("NewfiesAntiCheat.Shared.Models.ScanFindingDto");
object Finding(string code) {
    var finding = Activator.CreateInstance(findingType)!;
    findingType.GetProperty("Code")!.SetValue(finding, code);
    findingType.GetProperty("Scanner")!.SetValue(finding, "ServerBehavior");
    findingType.GetProperty("Severity")!.SetValue(finding, 4);
    return finding;
}
object review = Finding("PACKET_FLIGHT_REVIEW"), strong = Finding("PACKET_UNAUTHORIZED_MOVEMENT");
var points = T("BlockLogger.AntiCheat.AntiCheatEvaluator").GetMethod("RiskPoints", BindingFlags.NonPublic | BindingFlags.Static)!;
if ((int)points.Invoke(null, new[] { review })! != 0 || (int)points.Invoke(null, new[] { strong })! <= 0) throw new Exception("Review-only risk exclusion failed.");
var correlationType = T("BlockLogger.AntiCheat.AntiCheatIncidentCorrelator");
object correlator = Activator.CreateInstance(correlationType, true)!;
var observe = correlationType.GetMethod("Observe")!;
observe.Invoke(correlator, new[] { (object)"fixture", review, 1000L });
var signals = (IDictionary)correlationType.GetField("byPlayer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(correlator)!;
if (signals.Count != 0) throw new Exception("Review-only finding contributed incident evidence.");
observe.Invoke(correlator, new[] { (object)"fixture", strong, 1000L });
if (signals.Count != 1) throw new Exception("Strong signal was incorrectly excluded.");
object session = Activator.CreateInstance(T("BlockLogger.AntiCheat.AntiCheatSession"))!;
var findings = (IList)session.GetType().GetProperty("Findings")!.GetValue(session)!;
findings.Add(review); findings.Add(strong);
object rule = Activator.CreateInstance(T("BlockLogger.AntiCheat.AntiCheatConsequenceRule"))!;
var value = rule.GetType().GetProperty("TriggerValue")!;
var match = T("BlockLogger.AntiCheat.AntiCheatService").GetMethod("MatchConsequence", BindingFlags.NonPublic | BindingFlags.Static)!;
bool Matches(string code) { value.SetValue(rule, code); var result = match.Invoke(null, new[] { rule, session })!; return (bool)result.GetType().GetField("Item1")!.GetValue(result)!; }
if (Matches("PACKET_FLIGHT_REVIEW") || !Matches("PACKET_UNAUTHORIZED_MOVEMENT")) throw new Exception("Finding-code consequence isolation failed.");
Console.WriteLine("PASS: production flight-review findings add zero risk, no incident evidence and no finding-code consequences; strong evidence paths remain active. No game instance created.");

foreach(var sample in new[]{
    ("PACKET_FLOOD","budget=wire;floodConfirmed=False",false),
    ("PACKET_FLOOD","budget=wire;floodConfirmed=True",true),
    ("PACKET_FLOOD","budget=wire",false),
    ("PACKET_BLOCK_REACH","action=use;reachEnforcementEligible=False",false),
    ("PACKET_BLOCK_REACH","action=break;reachEnforcementEligible=False",false),
    ("PACKET_BLOCK_REACH","action=break;reachEnforcementEligible=True;timingRejectSuppressed=False",true),
    ("PACKET_BLOCK_REACH","action=break;reachEnforcementEligible=True;timingRejectSuppressed=True",false),
    ("PACKET_MINING_CADENCE_UNCERTAIN","",false),
    ("PACKET_MALFORMED_PACKET","",true)})
{
    object row=Finding(sample.Item1);findingType.GetProperty("Evidence")!.SetValue(row,sample.Item2);findings.Clear();findings.Add(row);
    bool risk=(int)points.Invoke(null,new[]{row})!>0;
    if(risk!=sample.Item3||Matches(sample.Item1)!=sample.Item3)throw new Exception("Risk/consequence confirmation mismatch for "+sample);
    if(!sample.Item3)
    {
        string uid="advisory-"+sample.Item1+sample.Item2;observe.Invoke(correlator,new[]{(object)uid,row,2000L});
        if(signals.Contains(uid))throw new Exception("Unconfirmed evidence corroborated an incident.");
    }
}
Console.WriteLine("PASS: short/legacy floods, right-click and unconfirmed/lagged reach cannot add risk, corroborate incidents or match kick rules; confirmed flood/break and malformed-packet protections remain enforceable.");
var serviceType=T("BlockLogger.AntiCheat.AntiCheatService");
object config=Activator.CreateInstance(T("BlockLogger.AntiCheat.AntiCheatConfig"))!;
object service=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(serviceType);
serviceType.GetField("config",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(service,config);
var rebuild=serviceType.GetMethod("RebuildSessionRiskFromCurrentEvidence",BindingFlags.Instance|BindingFlags.NonPublic)!;
session.GetType().GetProperty("LastScanUtc")!.SetValue(session,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
foreach(bool perFamily in new[]{false,true})
{
    config.GetType().GetProperty("PerFamilyRiskEnabled")!.SetValue(config,perFamily);findings.Clear();object retained=Finding("FORBIDDEN_MOD");findingType.GetProperty("Scanner")!.SetValue(retained,"ServerPolicy");findings.Add(retained);
    // No evaluator or mod-baseline/API dependencies exist in this detached fixture:
    // a behavior-only recalculation must retain validated client evidence without rescanning it.
    rebuild.Invoke(service,new[]{session,(object)false});
    if((int)session.GetType().GetProperty("RiskScore")!.GetValue(session)!<=0||!findings.Contains(retained))throw new Exception("Behavior fast path discarded established client evidence.");
    findings.Clear();object noise=Finding("PACKET_FLOOD");findingType.GetProperty("Evidence")!.SetValue(noise,"floodConfirmed=False");findings.Add(noise);rebuild.Invoke(service,new[]{session,(object)false});
    if((int)session.GetType().GetProperty("RiskScore")!.GetValue(session)! !=0)throw new Exception("Advisory flood affected family/legacy risk.");
}
Console.WriteLine("PASS: behavior-only recalculation skips client evaluation, retains validated client findings and clears advisory risk in both family and legacy scoring modes.");

var store=serviceType.GetMethod("StoreServerBehaviorObservation",BindingFlags.NonPublic|BindingFlags.Static)!;
findings.Clear();object confirmed=Finding("PACKET_BLOCK_REACH"),advisory=Finding("PACKET_BLOCK_REACH");findingType.GetProperty("Evidence")!.SetValue(confirmed,"action=break;reachEnforcementEligible=True");findingType.GetProperty("Evidence")!.SetValue(advisory,"action=use;reachEnforcementEligible=False");
store.Invoke(null,new[]{session,confirmed,(object)1000L});store.Invoke(null,new[]{session,advisory,(object)2000L});
var observed=(IDictionary)session.GetType().GetProperty("ServerBehaviorFindingUtc")!.GetValue(session)!;
if(findings.Count!=1||!ReferenceEquals(findings[0],confirmed)||(long)observed["PACKET_BLOCK_REACH"]! !=1000L)throw new Exception("Advisory repeat erased or refreshed confirmed evidence.");
findings.Clear();store.Invoke(null,new[]{session,advisory,(object)3000L});if(!ReferenceEquals(findings[0],advisory))throw new Exception("Advisory observation without prior confirmed evidence was lost.");
Console.WriteLine("PASS: advisory repeats cannot erase or refresh earlier confirmed evidence; isolated observations remain available for review.");

var guardType=T("BlockLogger.AntiCheat.PacketIngressGuard");var guardCtor=guardType.GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single();var parameters=guardCtor.GetParameters();
var pending=new List<Action>();var logged=new List<string>();
object Proxy(Type type,Func<MethodInfo,object?[]?,object?> handler){var proxy=DispatchProxy.Create(type,typeof(AdvisoryApiProxy));((AdvisoryApiProxy)proxy).Handler=handler;return proxy;}
var apiType=parameters[0].ParameterType;Type Property(string name)=>new[]{apiType}.Concat(apiType.GetInterfaces()).SelectMany(t=>t.GetProperties()).First(p=>p.Name==name).PropertyType;
var events=Proxy(Property("Event"),(m,a)=>{if(m.Name=="EnqueueMainThreadTask"){pending.Add((Action)a![0]!);return null;}throw new Exception("Unexpected event API: "+m.Name);});
var logger=Proxy(Property("Logger"),(m,a)=>{if(a?.LastOrDefault() is object[] values)logged.Add(string.Join(";",values.Select(v=>v?.ToString())));return m.ReturnType==typeof(bool)?false:null;});
var api=Proxy(apiType,(m,a)=>m.Name switch{"get_Event"=>events,"get_Logger"=>logger,_=>throw new Exception("Unexpected API: "+m.Name)});
var reportParameters=parameters[2].ParameterType.GetMethod("Invoke")!.GetParameters().Select(p=>System.Linq.Expressions.Expression.Parameter(p.ParameterType)).ToArray();
var report=System.Linq.Expressions.Expression.Lambda(parameters[2].ParameterType,System.Linq.Expressions.Expression.Empty(),reportParameters).Compile();
var snapshotType=T("BlockLogger.AntiCheat.PacketGuardTpsSnapshot");object snapshot=Activator.CreateInstance(snapshotType,new object[]{true,true,20d,20d,100d,1d,"Healthy",100,50d,false})!;
var health=System.Linq.Expressions.Expression.Lambda(parameters[3].ParameterType,System.Linq.Expressions.Expression.Constant(snapshot,snapshotType)).Compile();
object guard=guardCtor.Invoke(new[]{api,config,report,health});guardType.GetField("active",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(guard,true);
var wire=guardType.GetMethod("ValidateWire",BindingFlags.Instance|BindingFlags.NonPublic)!;var clientType=wire.GetParameters()[0].ParameterType;object client=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(clientType);object[] wireArgs={client,new byte[1],1};int dropped=0;
for(int i=0;i<10000;i++)if(!(bool)wire.Invoke(guard,wireArgs)!)dropped++;
if(dropped<8000||pending.Count!=1)throw new Exception("Actual ingress resource cap/report coalescing regressed: "+dropped+" / "+pending.Count);
foreach(var action in pending)action();pending.Clear();
if(!logged.Any(line=>line.Contains("floodConfirmed=False")))throw new Exception("Short actual ingress burst lacked advisory confirmation metadata.");
wireArgs[2]=2;if((bool)wire.Invoke(guard,wireArgs)!)throw new Exception("Invalid raw frame length passed ingress.");foreach(var action in pending)action();
if(!logged.Any(line=>line.Contains("PACKET_MALFORMED_PACKET")))throw new Exception("Malformed raw frame protection disappeared.");
((IDisposable)guard).Dispose();
Console.WriteLine("PASS: actual official-runtime ingress guard drops over-budget traffic immediately, coalesces a 10,000-frame burst to one advisory callback, and still rejects malformed frames. API doubles; no real connection/client.");

config.GetType().GetProperty("OreShieldEnabled")!.SetValue(config,false);
var oreType=T("BlockLogger.AntiCheat.OreShieldService");object ore=oreType.GetConstructors().Single().Invoke(new[]{api,config,null,null});int queuedBefore=pending.Count;
object?[] startArgs={null};bool oreStarted=(bool)oreType.GetMethod("Start")!.Invoke(ore,startArgs)!;
if(oreStarted||pending.Count!=queuedBefore||oreType.GetField("virtualizer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(ore)!=null||oreType.GetField("harmony",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(ore)!=null)throw new Exception("Disabled OreShield activated work.");
Console.WriteLine("PASS: disabled OreShield startup returns before world access, virtualizer creation, hook installation or queued work.");

public class AdvisoryApiProxy:DispatchProxy
{
    public Func<MethodInfo,object?[]?,object?> Handler=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>Handler(method!,args);
}
