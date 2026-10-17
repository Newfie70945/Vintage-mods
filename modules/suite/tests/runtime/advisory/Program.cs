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
