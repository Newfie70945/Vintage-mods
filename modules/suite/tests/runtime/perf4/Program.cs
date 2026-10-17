// Safety/performance regressions against the compiled production assembly.
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY"));
string dll = Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? throw new Exception("Set NSS_ASSEMBLY"));
var roots = new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods"), Path.GetDirectoryName(dll)!, Environment.GetEnvironmentVariable("NSS_XSKILLS_FIXTURE") ?? "" };
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (var root in roots) { var file = Path.Combine(root, name.Name + ".dll"); if (File.Exists(file)) return context.LoadFromAssemblyPath(file); }
    return null;
};
var engine = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryLib.dll"));
var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
Type M(string name) => mod.GetType(name, true)!;
const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
void Set(object value, string name, object? data) {
    var field = value.GetType().GetField(name, fields);
    if (field != null) field.SetValue(value, data);
    else value.GetType().GetProperty(name, fields)!.SetValue(value, data);
}

const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
object? Call(object target, string name, params object?[] values) => target.GetType().GetMethod(name, members)!.Invoke(target, values);
void Check(bool good, string message) { if (!good) throw new Exception(message); }
var web = M("BlockLogger.WebDashboard.WebDashboardService");
bool Invalidates(string method, string path, int status) => (bool)web.GetMethod("ShouldInvalidateAfterResponse", members)!.Invoke(null, new object[] { method, path, status })!;
foreach (int status in new[] { 400, 401, 403, 404, 429, 500 }) Check(!Invalidates("POST", "/api/anticheat/settings", status), "Failed request invalidated cache");
Check(!Invalidates("POST", "/api/login", 200) && !Invalidates("GET", "/api/anticheat", 200), "Nonmutation invalidation");
Check(Invalidates("POST", "/api/anticheat/settings", 200), "Successful mutation did not invalidate");
bool Affected(string key, string path) => (bool)web.GetMethod("CacheAffectedByMutation", members)!.Invoke(null, new object[] { key, path })!;
Check(Affected("anticheat", "/api/anticheat/settings") && !Affected("discord-status", "/api/anticheat/settings"), "Cache invalidation not scoped");
string bounded = (string)web.GetMethod("BoundAuditField", members)!.Invoke(null, new object[] { new string('a', 128000) + "\n\0", 128 })!;
Check(bounded.Length < 180 && bounded.Contains("originalLength=128002") && !bounded.Any(char.IsControl), "Audit text unbounded or unsanitized");
Console.WriteLine("PASS: failed/auth requests preserve cached pages; successful writes target related caches; audit strings bounded.");

Type cacheType = M("BlockLogger.WebDashboard.CoalescedSnapshotCache");
object cache = Activator.CreateInstance(cacheType, new object?[] { (Func<long>)(() => 1000L) })!;
MethodInfo get = cacheType.GetMethod("GetAsync")!;
int builds = 0;
Task<object> Get(string key, Func<Task<object>>? builder = null) => (Task<object>)get.Invoke(cache,
    new object?[] { key, 5000, builder ?? (() => Task.FromResult<object>(++builds)), false, null, true })!;
await Get("anticheat"); await Get("discord-status");
cacheType.GetMethod("Invalidate")!.Invoke(cache, new object?[] { (Func<string, bool>)(key => key == "anticheat") });
await Get("discord-status"); Check(builds == 2, "Unrelated cache rebuilt");
await Get("anticheat"); Check(builds == 3, "Related cache did not refresh");
var held = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
Task<object> pending = Get("held", () => held.Task);
for (int i = 0; i < 2000; i++) await Get("window-" + i);
int size = ((ICollection)cacheType.GetField("entries", members)!.GetValue(cache)!).Count;
Check(size == 128, "Snapshot keys grew beyond capacity");
Task<object> samePending = Get("held", () => throw new Exception("Pending builder was evicted"));
held.SetResult("done"); Check((string)await pending == "done" && (string)await samePending == "done", "Coalescing lost pending work");
Console.WriteLine("PASS: 2,000 cache keys stay bounded to 128; pending work remains coalesced through eviction.");

Type speedType = M("BlockLogger.AntiCheat.ConfirmedSpeedRisk");
object speed = Activator.CreateInstance(speedType, true)!;
foreach (string code in new[] { "PACKET_MOUNT_MOVEMENT_HACK", "PACKET_FLIGHT_REVIEW", "PACKET_ACCELERATION_HACK", "PACKET_MALFORMED_PACKET" })
    Check(!(bool)speedType.GetMethod("Eligible", members)!.Invoke(null, new object[] { code, 4, "speedEscalationEligible=True" })!, "Non-speed observation escalated");
Check(!(bool)speedType.GetMethod("Eligible", members)!.Invoke(null, new object[] { "PACKET_SPEED_HACK", 3, "speedEscalationEligible=True" })!, "High/degraded-TPS speed escalated");
Check(!(bool)speedType.GetMethod("Eligible", members)!.Invoke(null, new object[] { "PACKET_SPEED_HACK", 4, "speedEscalationEligible=False" })!, "Degraded or warming-up TPS allowed escalation");
Check((bool)speedType.GetMethod("Eligible", members)!.Invoke(null, new object[] { "PACKET_SPEED_HACK", 4, "mode=ground;speedEscalationEligible=True;tpsBand=Healthy" })!, "Healthy confirmed speed rejected");
Check((int)Call(speed, "Observe", 1000L)! == 1 && (int)Call(speed, "Observe", 1001L)! == 1, "Duplicate speed report counted");
Check((int)Call(speed, "Observe", 11000L)! == 2 && (int)Call(speed, "Bonus", 11000L, 25)! == 25, "Second confirmed report wrong risk");
Check((int)Call(speed, "Observe", 21000L)! == 3 && (int)Call(speed, "Bonus", 21000L, 25)! == 50, "Third report did not reach 100");
Check((int)Call(speed, "Bonus", 21000L, 0)! == 0, "Disabled repeat risk not respected");
Check((int)Call(speed, "Count", 81000L)! == 0, "Old confirmations retained");
Call(speed, "Observe", 90000L); Check((int)Call(speed, "Count", 80000L)! == 0, "Clock reversal retained confirmations");
// Exercise the actual family scorer so helper escalation cannot be hidden by the old max-50 rule.
object config = Activator.CreateInstance(M("BlockLogger.AntiCheat.AntiCheatConfig"))!;
object session = Activator.CreateInstance(M("BlockLogger.AntiCheat.AntiCheatSession"))!;
object service = RuntimeHelpers.GetUninitializedObject(M("BlockLogger.AntiCheat.AntiCheatService")); Set(service, "config", config);
object finding = Activator.CreateInstance(M("NewfiesAntiCheat.Shared.Models.ScanFindingDto"))!;
Set(finding, "Code", "PACKET_SPEED_HACK"); Set(finding, "Scanner", "ServerBehavior"); Set(finding, "Severity", 4);
((IList)session.GetType().GetProperty("Findings")!.GetValue(session)!).Add(finding);
object confirmations = session.GetType().GetProperty("ConfirmedSpeed", members)!.GetValue(session)!;
foreach (var (time, expected) in new[] { (100000L, 50), (110000L, 75), (120000L, 100) })
{
    Call(confirmations, "Observe", time); Set(session, "LastScanUtc", time);
    Call(service, "UpdateSessionFamilyRisk", session, time, false);
    Check((int)session.GetType().GetProperty("RiskScore")!.GetValue(session)! == expected, "Family scorer capped repeated speed at 50");
}
Console.WriteLine("PASS: compiled live-family risk reaches 50/75/100; duplicate, stale, mount and downgraded speed reports do not escalate.");

// Exercise the complete production behavior-report path and actual disconnect callback.
var acType = M("BlockLogger.AntiCheat.AntiCheatService");
var acConstructor = acType.GetConstructors().Single();
Type serverApi = acConstructor.GetParameters()[0].ParameterType;
object enforcementApi = Proxy(serverApi, (m, a) => m.Name switch {
    "get_Event" => Proxy(m.ReturnType, (em, ea) => Default(em.ReturnType)),
    "get_Logger" => Proxy(m.ReturnType, (lm, la) => Default(lm.ReturnType)),
    _ => Default(m.ReturnType)
});
Type adminDelegateType = acConstructor.GetParameters()[1].ParameterType;
var nullAdmin = System.Linq.Expressions.Expression.Lambda(adminDelegateType,
    System.Linq.Expressions.Expression.Constant(null, adminDelegateType.GenericTypeArguments[0])).Compile();
object enforcement = acConstructor.Invoke(new[] { enforcementApi, nullAdmin }); Set(enforcement, "runtimeAvailable", true);
object enforcementConfig = acType.GetProperty("Config")!.GetValue(enforcement)!;
int disconnects = 0;
// Native IServerPlayer contains an internal default member, so DispatchProxy cannot
// implement it. Use a real ServerPlayer subclass overriding only Disconnect(string).
Type nativePlayerType = engine.GetType("Vintagestory.Server.ServerPlayer", true)!;
var dynamicAssembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Perf4PlayerFixture"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
var moduleBuilder = dynamicAssembly.DefineDynamicModule("fixture");
var builder = moduleBuilder.DefineType("Perf4NativePlayer", TypeAttributes.Public, nativePlayerType);
ConstructorInfo baseConstructor = nativePlayerType.GetConstructors().Single();
var ctor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, baseConstructor.GetParameters().Select(p => p.ParameterType).ToArray());
var ctorIl = ctor.GetILGenerator(); ctorIl.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); ctorIl.Emit(System.Reflection.Emit.OpCodes.Ldarg_1); ctorIl.Emit(System.Reflection.Emit.OpCodes.Ldarg_2); ctorIl.Emit(System.Reflection.Emit.OpCodes.Call, baseConstructor); ctorIl.Emit(System.Reflection.Emit.OpCodes.Ret);
var callback = builder.DefineField("DisconnectObserved", typeof(Action<string>), FieldAttributes.Public | FieldAttributes.Static);
var disconnect = builder.DefineMethod("Disconnect", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, typeof(void), new[] { typeof(string) });
var il = disconnect.GetILGenerator(); il.Emit(System.Reflection.Emit.OpCodes.Ldsfld, callback); il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1); il.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(Action<string>).GetMethod("Invoke")!); il.Emit(System.Reflection.Emit.OpCodes.Ret);
builder.DefineMethodOverride(disconnect, nativePlayerType.GetMethod("Disconnect", new[] { typeof(string) })!);
Type playerType = builder.CreateType()!; playerType.GetField("DisconnectObserved")!.SetValue(null, (Action<string>)(_ => disconnects++));
object Player(string uid)
{
    object player = RuntimeHelpers.GetUninitializedObject(playerType);
    var dataField = nativePlayerType.GetField("worlddata", members)!;
    object data = RuntimeHelpers.GetUninitializedObject(dataField.FieldType); Set(data, "PlayerUID", uid); dataField.SetValue(player, data);
    return player;
}
object manager = acType.GetField("sessions", members)!.GetValue(enforcement)!;
void Seed(string uid)
{
    object liveSession = Call(manager, "GetOrCreate", uid, "Fixture")!;
    object liveSpeed = liveSession.GetType().GetProperty("ConfirmedSpeed", members)!.GetValue(liveSession)!;
    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); Call(liveSpeed, "Observe", now - 20010); Call(liveSpeed, "Observe", now - 10005);
}
var severityType = acType.GetMethod("ReportServerBehaviorFinding", members)!.GetParameters()[2].ParameterType;
void Report(string uid, string code, int severity, string evidence)
{
    Call(enforcement, "ReportServerBehaviorFinding", Player(uid), code, Enum.ToObject(severityType, severity), "fixture", uid, evidence);
}
Seed("kick"); Report("kick", "PACKET_SPEED_HACK", 4, "mode=ground;speedEscalationEligible=True;tpsBand=Healthy");
Check(disconnects == 1, "Repeated confirmed speed did not actually disconnect");
Set(enforcementConfig, "PacketGuardKickRepeatedConfirmedSpeed", false);
Seed("disabled"); Report("disabled", "PACKET_SPEED_HACK", 4, "speedEscalationEligible=True");
Set(enforcementConfig, "PacketGuardKickRepeatedConfirmedSpeed", true);
Seed("mount"); Report("mount", "PACKET_MOUNT_MOVEMENT_HACK", 4, "speedEscalationEligible=True");
Seed("degraded"); Report("degraded", "PACKET_SPEED_HACK", 4, "speedEscalationEligible=False;tpsBand=Degraded");
Check(disconnects == 1, "Disabled setting, mount or degraded observation kicked");
Console.WriteLine("PASS: complete production report path actually disconnects the third qualifying speed report; kick-off, mount and degraded-TPS observations do not disconnect.");

string temp = Path.Combine(Path.GetTempPath(), "nss-perf4-" + Guid.NewGuid()); Directory.CreateDirectory(temp);
try
{
    string part = Path.Combine(temp, "evidence.part");
    object writer = Activator.CreateInstance(M("BlockLogger.AntiCheat.EvidenceFileWriter"), part)!;
    byte[] data = Enumerable.Range(0, 65536).Select(x => (byte)x).ToArray(), expected = (byte[])data.Clone();
    Check((bool)Call(writer, "TryWrite", data)!, "Evidence append rejected"); Array.Fill(data, (byte)0);
    string hash = await (Task<string>)Call(writer, "CompleteAsync")!;
    Check(hash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected)).ToLowerInvariant() && File.ReadAllBytes(part).SequenceEqual(expected), "Evidence ownership/hash mismatch");
    string aborted = Path.Combine(temp, "cancelled.part");
    object abortedWriter = Activator.CreateInstance(M("BlockLogger.AntiCheat.EvidenceFileWriter"), aborted)!;
    Call(abortedWriter, "TryWrite", expected); Call(abortedWriter, "Abort");
    try { await (Task<string>)Call(abortedWriter, "CompleteAsync")!; } catch (OperationCanceledException) { }
    Check(!File.Exists(aborted), "Aborted partial evidence retained");
    Console.WriteLine("PASS: worker evidence writes own packet bytes, durably finalize/hash correctly and clean cancelled partials.");

    string journal = Path.Combine(temp, "receipts.jsonl");
    await File.WriteAllLinesAsync(journal, Enumerable.Range(0, 20000).Select(i => $"{{\"key\":\"old-{i}\",\"phase\":\"intent\"}}"));
    object index = Activator.CreateInstance(M("BlockLogger.Services.RecoveryReceiptIndex"), journal)!;
    async Task<HashSet<string>> Find(params string[] keys) => await (Task<HashSet<string>>)Call(index, "FindAsync", (object)keys, CancellationToken.None)!;
    var found = await Find("old-19999", "new"); Check(found.SetEquals(new[] { "old-19999" }), "Historical uncertain receipt lost");
    await File.AppendAllTextAsync(journal, "{\"key\":\"new\",\"phase\":\"intent\"}\n");
    found = await Find("old-0", "new"); Check(found.Count == 2, "New durable receipt not indexed");
    object restarted = Activator.CreateInstance(index.GetType(), journal)!;
    var restartFound = await (Task<HashSet<string>>)Call(restarted, "FindAsync", (object)new[] { "old-1", "new" }, CancellationToken.None)!;
    Check(restartFound.Count == 2, "Restart lost indexed receipt");
    await File.AppendAllTextAsync(journal, "broken-json\n");
    try { await Find("new"); throw new Exception("Corrupt receipt journal accepted"); } catch (System.Text.Json.JsonException) { }
    Check(File.ReadAllText(journal).Contains("broken-json"), "Authoritative receipt journal was altered");
    Console.WriteLine("PASS: 20,000 durable receipts indexed on disk; only requested keys returned; append/restart and corrupt-journal fail-closed behavior retained.");
}
finally { Directory.Delete(temp, true); }

static object? Default(Type type) => type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
static object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler) { object proxy = DispatchProxy.Create(type, typeof(Perf4Proxy)); ((Perf4Proxy)proxy).Handler = handler; return proxy; }
public class Perf4Proxy : DispatchProxy { public Func<MethodInfo, object?[]?, object?> Handler = null!; protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args); }
