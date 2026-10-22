using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY")
    ?? throw new InvalidOperationException("Set VINTAGE_STORY to the official Vintage Story 1.22.7 install directory."));
string modPath = Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? args.FirstOrDefault()
    ?? throw new InvalidOperationException("Set NSS_ASSEMBLY to the compiled NewfiesServerEssentialsSuite.dll path."));
string output = Path.GetDirectoryName(modPath)!;
var roots = new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods"), output };
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    foreach (string root in roots)
    {
        string path = Path.Combine(root, name.Name + ".dll");
        if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
    }
    return null;
};
var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);
var type = mod.GetType("BlockLogger.WebDashboard.WebDashboardService", throwOnError: true)!;
object service = RuntimeHelpers.GetUninitializedObject(type);
FieldInfo apiField = type.GetField("api", BindingFlags.Instance | BindingFlags.NonPublic)!;
Type apiType = apiField.FieldType;
Type eventsType = apiType.GetProperty("Event")!.PropertyType;
var queued = new List<Action>();
var fakeEvents = Proxy(eventsType, (method, arguments) =>
{
    if (method.Name == "EnqueueMainThreadTask") { queued.Add((Action)arguments![0]!); return null; }
    throw new Exception("Unexpected events access: " + method.Name);
});
apiField.SetValue(service, Proxy(apiType, (method, arguments) => method.Name == "get_Event" ? fakeEvents : throw new Exception("Unexpected API access: " + method.Name)));
MethodInfo run = type.GetMethod("RunOnMainThreadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(int));
int work = 0;
Task<int> Schedule(bool skip) => (Task<int>)run.Invoke(service, new object[] { (Func<int>)(() => ++work), "smoke-read", skip })!;
async Task Expire(Task<int> task)
{
    try { await task; throw new Exception("Expected timeout"); }
    catch (TimeoutException) { }
}
await Expire(Schedule(true));
if (work != 0 || queued.Count != 1) throw new Exception("Queued read executed before callback");
var latest = Schedule(true);
queued[0]();
if (work != 0) throw new Exception("Abandoned cache read executed");
queued[1]();
if (await latest != 1 || work != 1) throw new Exception("Replacement cache read did not execute once");
Console.WriteLine("PASS: timed-out queued cache read skipped; replacement runs exactly once");
await Expire(Schedule(false));
queued[2]();
if (work != 2) throw new Exception("Default mutation completion behavior changed");
Console.WriteLine("PASS: default queued mutation retains completion after timeout; no game instance created");

const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
void SetField(string name, object value) => type.GetField(name, flags)!.SetValue(service, value);
SetField("hardeningLock", new object());
SetField("featureOverhead", Activator.CreateInstance(type.GetField("featureOverhead", flags)!.FieldType)!);
var gate = new SemaphoreSlim(0, 1); SetField("heavyOperationGate", gate);
Type cacheType = mod.GetType("BlockLogger.WebDashboard.CoalescedSnapshotCache")!;
SetField("snapshotCache", Activator.CreateInstance(cacheType, new object?[] { null })!);
Type metricType = type.GetNestedType("WebRequestMetricContext", flags)!;
Type leaseType = type.GetNestedType("RouteOperationLease", flags)!;
object lease = Activator.CreateInstance(leaseType, service, "POST:/api/admin/cleanup", "smoke", true)!;
object metricSlot = type.GetField("requestMetricContext", flags)!.GetValue(null)!;
Task<int> WithContext(string method, object? operationLease)
{
    object metric = Activator.CreateInstance(metricType, true)!;
    metricType.GetProperty("Method")!.SetValue(metric, method);
    metricType.GetProperty("Path")!.SetValue(metric, "/api/admin/cleanup");
    metricType.GetProperty("RouteLease")!.SetValue(metric, operationLease);
    metricSlot.GetType().GetProperty("Value")!.SetValue(metricSlot, metric);
    Task<int> task = Schedule(false);
    metricSlot.GetType().GetProperty("Value")!.SetValue(metricSlot, null);
    return task;
}
Task<int> directRead = WithContext("GET", null), exclusiveWrite = WithContext("POST", lease);
await Task.WhenAll(Expire(directRead), Expire(exclusiveWrite));
((IDisposable)lease).Dispose(); // HTTP route ends while the write remains queued.
if (gate.CurrentCount != 0) throw new Exception("Heavy-operation gate released at HTTP timeout");
queued[3](); if (work != 2) throw new Exception("Direct GET executed after timeout");
queued[4](); if (work != 3 || gate.CurrentCount != 1) throw new Exception("Write/gate did not finish at actual operation completion");
Console.WriteLine("PASS: direct timed-out GET skipped; queued mutation executes once; exclusive gate survives HTTP timeout until game work finishes.");

// Block metric finalization after the action returns. The HTTP task must not be
// published while its callback interval is still waiting to be recorded.
object finalMetric = Activator.CreateInstance(metricType, true)!;
metricType.GetProperty("Method")!.SetValue(finalMetric, "GET");
metricSlot.GetType().GetProperty("Value")!.SetValue(metricSlot, finalMetric);
using var entered = new ManualResetEventSlim();
using var releaseAction = new ManualResetEventSlim();
var ordered = (Task<int>)run.Invoke(service, new object[] { (Func<int>)(() => { entered.Set(); releaseAction.Wait(); return 99; }), "ordering", true })!;
metricSlot.GetType().GetProperty("Value")!.SetValue(metricSlot, null);
var callback = Task.Run(queued[^1]);
if (!entered.Wait(TimeSpan.FromSeconds(2))) throw new Exception("Fixture callback did not enter");
object metricSync = metricType.GetProperty("Sync")!.GetValue(finalMetric)!;
Monitor.Enter(metricSync);
bool completedBeforeTiming;
try { releaseAction.Set(); Thread.Sleep(100); completedBeforeTiming = ordered.IsCompleted; }
finally { Monitor.Exit(metricSync); }
await callback;
if (completedBeforeTiming || await ordered != 99) throw new Exception("HTTP task completed before callback timing finalized");
var windows = (System.Collections.IList)metricType.GetProperty("WorkWindows")!.GetValue(finalMetric)!;
if (windows.Count != 1) throw new Exception("Completed response lacks its measured callback interval");
var thrown = (Task<int>)run.Invoke(service, new object[] { (Func<int>)(() => throw new InvalidOperationException("fixture failure")), "exception", true })!;
queued[^1]();
try { await thrown; throw new Exception("Callback exception lost"); }
catch (InvalidOperationException error) when (error.Message == "fixture failure") { }
Console.WriteLine("PASS: response completion waits for callback timing; original callback exceptions preserved.");

static object Proxy(Type type, Func<MethodInfo, object?[]?, object?> invoke)
{
    object result = DispatchProxy.Create(type, typeof(FakeProxy));
    ((FakeProxy)result).Handler = invoke;
    return result;
}
public class FakeProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
