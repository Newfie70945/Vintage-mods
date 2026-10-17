using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY");
string suitePath = Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? throw new Exception("Set NSS_ASSEMBLY"));
string discordPath = Path.GetFullPath(Environment.GetEnvironmentVariable("DISCORD_ASSEMBLY") ?? throw new Exception("Set DISCORD_ASSEMBLY"));
string clientPath = Path.GetFullPath(Environment.GetEnvironmentVariable("CLIENT_ASSEMBLY") ?? throw new Exception("Set CLIENT_ASSEMBLY"));
string motdPath = Path.GetFullPath(Environment.GetEnvironmentVariable("MOTD_ASSEMBLY") ?? throw new Exception("Set MOTD_ASSEMBLY"));
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (string root in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods"), Path.GetDirectoryName(suitePath)!, Path.GetDirectoryName(discordPath)!, Path.GetDirectoryName(motdPath)!, Path.GetDirectoryName(clientPath)! }) {
        string path = Path.Combine(root, name.Name + ".dll"); if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
    }
    return null;
};
Assembly suite = AssemblyLoadContext.Default.LoadFromAssemblyPath(suitePath), discord = AssemblyLoadContext.Default.LoadFromAssemblyPath(discordPath), motd = AssemblyLoadContext.Default.LoadFromAssemblyPath(motdPath);
object New(Type type) => Activator.CreateInstance(type, true)!;
object Field(object value, string name) => value.GetType().GetField(name, Flags)!.GetValue(value)!;
void Set(object value, string name, object? data) {
    var property = value.GetType().GetProperty(name, Flags);
    if (property != null) property.SetValue(value, data); else value.GetType().GetField(name, Flags)!.SetValue(value, data);
}
object? Call(object value, string name, params object?[] args) => value.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args);
Assembly integrity = AssemblyLoadContext.Default.LoadFromAssemblyPath(clientPath);
void Check(bool success, string message) { if (!success) throw new Exception(message); }
int Count(object value) => (int)value.GetType().GetProperty("Count")!.GetValue(value)!;

Type dedupeType = suite.GetType("BlockLogger.AntiCheat.ExpiringDiagnosticDedupe", true)!;
object dedupe = New(dedupeType);
Check(!(bool)Call(dedupe, "IsDuplicate", "source", "Player-A", 1000L, 1750)!, "First event suppressed");
Check((bool)Call(dedupe, "IsDuplicate", "SOURCE", "player-a", 1100L, 1750)!, "Duplicate event counted");
Call(dedupe, "RemovePlayer", "PLAYER-A");
Check(Count(Field(dedupe, "stamps")) == 0, "Disconnect retained dedupe identity");
for (int i = 0; i < 30_000; i++) Call(dedupe, "IsDuplicate", "source", "uid-" + i, 2000L, 1750);
Check(Count(Field(dedupe, "stamps")) == 4096, "Dedupe cardinality escaped its limit");
Call(dedupe, "IsDuplicate", "source", "fresh", 12_001L, 1750);
Check(Count(Field(dedupe, "stamps")) == 1, "Idle dedupe entries did not expire");
Console.WriteLine("PASS: 30,000 diagnostic identities stay bounded; case-insensitive duplicate/disconnect behavior and idle expiry preserved.");

Type serviceType = suite.GetType("BlockLogger.AntiCheat.AntiCheatService", true)!;
object service = Activator.CreateInstance(serviceType, new object?[] { null, null })!;
Type entryType = suite.GetType("BlockLogger.AntiCheat.AntiCheatModDbVerificationCacheEntry", true)!;
object Entry(string id, long stamp, string message = "") { object row = New(entryType); Set(row, "ModId", id); Set(row, "Version", "1.0.0"); Set(row, "CheckedUtc", stamp); Set(row, "Message", message); return row; }
Call(Field(service, "serverModDbBaselineKeys"), "Add", "baseline@1.0.0");
Call(service, "StoreModDbVerificationEntryLocked", "baseline@1.0.0", Entry("baseline", 0));
for (int i = 1; i <= 1600; i++) Call(service, "StoreModDbVerificationEntryLocked", "cache-" + i, Entry("cache-" + i, i));
var cache = (IDictionary)Field(service, "modDbVerificationCache");
Check(cache.Count == 1024 && cache.Contains("baseline@1.0.0") && cache.Contains("cache-1600"), "Cache entry cap/baseline/newest priority failed");
string large = new('x', 1024 * 1024);
for (int i = 1601; i <= 1670; i++) Call(service, "StoreModDbVerificationEntryLocked", "cache-" + i, Entry("cache-" + i, i, large));
long weight = (long)Field(service, "modDbVerificationCacheEstimatedBytes");
Check(weight <= 64L * 1024 * 1024 && cache.Count < 40 && cache.Contains("baseline@1.0.0"), "Byte budget failed on large records");
Call(service, "StoreModDbVerificationEntryLocked", "cache-1670", Entry("cache-1670", 1800));
Check((long)Field(service, "modDbVerificationCacheEstimatedBytes") < weight - 1024 * 1024, "Replacing cache entry retained its old weight");
Console.WriteLine("PASS: 1,600+ cache versions bounded by count and 64 MiB estimate; installed baseline prioritized; replacement releases prior budget.");

Set(Field(service, "config"), "ClientModDbVerificationEnabled", true);
serviceType.GetField("modDbVerificationSlots", Flags)!.SetValue(service, new SemaphoreSlim(0, 2)); // Hold all slots: no external HTTP work.
Type modType = suite.GetType("NewfiesAntiCheat.Shared.Models.ClientModDto", true)!;
int queued = 0;
for (int batch = 0; batch < 12; batch++) {
    Array mods = Array.CreateInstance(modType, 256);
    for (int i = 0; i < 256; i++) { var row = New(modType); Set(row, "ModId", "fixture" + (batch * 256 + i)); Set(row, "Version", "1.0.0"); mods.SetValue(row, i); }
    queued += (int)Call(service, "QueueModDbVerification", "player", "1.22.7", mods, false)!;
    Check(Count(Field(service, "modDbVerificationPending")) <= 512, "Blocked verification tasks grew without limit");
}
Check(queued == 512, "Verification admission bound not reached");
((CancellationTokenSource)Field(service, "modDbVerificationCts")).Cancel();
long deadline = Environment.TickCount64 + 5000;
while (Count(Field(service, "modDbVerificationPending")) != 0 && Environment.TickCount64 < deadline) await Task.Delay(10);
Check(Count(Field(service, "modDbVerificationPending")) == 0, "Cancelled verifier retained pending reservations");
Console.WriteLine("PASS: 3,072 proposed verifications create at most 512 waiting tasks; cancellation drains every reservation without making network requests.");
object deferredService = Activator.CreateInstance(serviceType, new object?[] { null, null })!;
Set(Field(deferredService, "config"), "ClientModDbVerificationEnabled", true);
serviceType.GetField("modDbVerificationSlots", Flags)!.SetValue(deferredService, new SemaphoreSlim(0, 2));
object liveSession = Call(Field(deferredService, "sessions"), "GetOrCreate", "deferred-player", "Player")!;
Set(liveSession, "LastScanUtc", 1L);
object deferredMod = New(modType); Set(deferredMod, "ModId", "deferredmod"); Set(deferredMod, "Version", "1.0.0");
((IList)liveSession.GetType().GetProperty("LastMods")!.GetValue(liveSession)!).Add(deferredMod);
Array deferredMods = Array.CreateInstance(modType, 1); deferredMods.SetValue(deferredMod, 0);
object reservations = Field(deferredService, "modDbVerificationPending");
for (int i = 0; i < 512; i++) Call(reservations, "Add", "busy-" + i);
Check((int)Call(deferredService, "QueueModDbVerification", "deferred-player", "1.22.7", deferredMods, false)! == 0, "Busy verifier exceeded its bound");
Call(reservations, "Clear"); Call(deferredService, "RefillDeferredModDbVerifications");
Check((bool)Call(reservations, "Contains", "deferredmod@1.0.0")!, "Deferred live-session verification was stranded after capacity became available");
((CancellationTokenSource)Field(deferredService, "modDbVerificationCts")).Cancel();
deadline = Environment.TickCount64 + 5000;
while (Count(reservations) != 0 && Environment.TickCount64 < deadline) await Task.Delay(10);
Check(Count(reservations) == 0, "Deferred cancellation leaked a reservation");
Console.WriteLine("PASS: deferred live-session verification automatically refills after capacity is available; no extra deferred packet/list queue retained.");


Type healthType = suite.GetType("BlockLogger.ServerHealth.ServerHealthSnapshot", true)!;
Type sampleType = suite.GetType("BlockLogger.ServerHealth.ServerHealthSample", true)!;
object health = New(healthType); IList samples = (IList)healthType.GetProperty("Samples")!.GetValue(health)!;
for (int i = 0; i < 180; i++) {
    object sample = New(sampleType); Set(sample, "UnixMilliseconds", 1000L + i * 1000); Set(sample, "ManagedHeapMb", 10894d - i * 9); Set(sample, "PrivateMemoryMb", 16724d);
    Set(sample, "GcCommittedMb", 15385d); Set(sample, "GcFragmentedMb", 5602d); Set(sample, "EstimatedNativeMb", 1339d); Set(sample, "Gen2Collections", i / 10); samples.Add(sample);
}
object dashboard = RuntimeHelpers.GetUninitializedObject(suite.GetType("BlockLogger.WebDashboard.WebDashboardService", true)!);
object analysis = Call(dashboard, "BuildMemoryGcAnalysis", health)!;
Check(((string)analysis.GetType().GetProperty("Finding")!.GetValue(analysis)!).Contains("fragmentation"), "Declining floor with fragmentation misclassified");
for (int i = 0; i < samples.Count; i++) Set(samples[i]!, "ManagedHeapMb", 1000d + i * 20);
analysis = Call(dashboard, "BuildMemoryGcAnalysis", health)!;
Check(((string)analysis.GetType().GetProperty("Finding")!.GetValue(analysis)!).Contains("retained-memory"), "Fragmentation hid real rising-floor evidence");
Console.WriteLine("PASS: reported fragmentation/reclaiming pattern distinguished from a rising post-GC floor.");

Type linkConfigType = discord.GetType("NewfiesServerEssentials.Discord.Linking.DiscordLinkConfig", true)!;
object linkConfig = New(linkConfigType); Set(linkConfig, "CodeCooldownSeconds", 60); Set(linkConfig, "CodeExpirySeconds", 60); Set(linkConfig, "FailedAttemptWindowSeconds", 60);
var configLambda = System.Linq.Expressions.Expression.Lambda(typeof(Func<>).MakeGenericType(linkConfigType), System.Linq.Expressions.Expression.Constant(linkConfig)).Compile();
long now = 1000; object codes = Activator.CreateInstance(discord.GetType("NewfiesServerEssentials.Discord.Linking.Services.LinkCodeService", true)!, configLambda, (Func<long>)(() => now))!;
for (int i = 0; i < 5000; i++) Call(codes, "TryValidate", "invalid", "discord-" + i, null, null);
Check(Count(Field(codes, "failedAttemptsByDiscordUser")) == 4096, "Discord failed-user tracking not bounded");
for (int i = 0; i < 1100; i++) Call(codes, "TryCreate", "player-" + i, "Player", null, null);
Check(Count(Field(codes, "pendingByPlayerUid")) == 1024, "Pending link-code cap failed");
now += 60_001; Call(codes, "PruneExpired");
foreach (string field in new[] { "pendingByPlayerUid", "pendingByCodeHash", "lastIssuedAtByPlayerUid", "failedAttemptsByDiscordUser" }) Check(Count(Field(codes, field)) == 0, "Expired Discord tracking retained: " + field);
object?[] createArgs = { "Alice", "Alice", null, null }; Check((bool)Call(codes, "TryCreate", createArgs)!, "Fresh code creation failed");
object?[] validateArgs = { createArgs[2], "discord-alice", null, null }; Check((bool)Call(codes, "TryValidate", validateArgs)!, "Fresh legitimate code rejected");
Check(!(bool)Call(codes, "TryCreate", "Alice", "Alice", null, null)!, "Active cooldown cleared too early");
Call(codes, "Clear"); Check(Count(Field(codes, "pendingByPlayerUid")) == 0, "Shutdown retained link codes");
Console.WriteLine("PASS: 5,000 Discord identities/1,100 code requests bounded; expired records pruned; legitimate validation and cooldown preserved; shutdown clears tracking.");

// Use the real engine composer/LoadedTexture disposer with a fake renderer. No GL context.
Type clientApiType = AssemblyLoadContext.Default.Assemblies.Single(a => a.GetName().Name == "VintagestoryAPI").GetType("Vintagestory.API.Client.ICoreClientAPI", true)!;
Type guiType = clientApiType.GetProperty("Gui")!.PropertyType, renderType = clientApiType.GetProperty("Render")!.PropertyType;
Type dialogBase = guiType.Assembly.GetType("Vintagestory.API.Client.GuiDialog", true)!;
IList loaded = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(dialogBase))!;
int deleted = 0;
object render = Proxy(renderType, (method, args) => method.Name switch {
    "DeleteTexture" => CountDelete(), "get_FrameWidth" => throw new FixtureStop(), _ => throw new Exception("Unexpected renderer call: " + method.Name)
});
object? CountDelete() { deleted++; return null; }
object gui = Proxy(guiType, (method, args) => method.Name switch { "get_LoadedGuis" or "get_OpenedGuis" => loaded, "DeleteTexture" => CountDelete(), _ => null });
object capi = Proxy(clientApiType, (method, args) => method.Name switch { "get_Gui" => gui, "get_Render" => render, _ => throw new Exception("Unexpected API call: " + method.Name) });
Type dialogType = motd.GetType("NewfiesCommunityMOTD.GuiDialogMotd", true)!;
object dialog = RuntimeHelpers.GetUninitializedObject(dialogType);
dialogBase.GetField("capi", Flags)!.SetValue(dialog, capi);
object composers = Activator.CreateInstance(dialogBase.GetNestedType("DlgComposers", Flags)!, dialog)!; Set(dialog, "Composers", composers);
Set(dialog, "onClosedCb", (Action<bool>)(_ => { }));
Type packetType = motd.GetType("NewfiesCommunityMOTD.MotdPacket", true)!; Set(dialog, "packet", New(packetType));
Type composerType = guiType.Assembly.GetType("Vintagestory.API.Client.GuiComposer", true)!;
Type boundsType = guiType.Assembly.GetType("Vintagestory.API.Client.ElementBounds", true)!;
object bounds = boundsType.GetProperty("Empty", Flags)!.GetValue(null)!;
void AttachComposer() {
    object composer = Activator.CreateInstance(composerType, Flags, null, new[] { capi, bounds, "memory-fixture" }, null)!;
    Set(Field(composer, "staticElementsTexture"), "TextureId", deleted + 1); Set(dialog, "SingleComposer", composer);
}
for (int i = 0; i < 50; i++) {
    AttachComposer();
    try { Call(dialog, "ComposeDialog"); throw new Exception("Expected headless composition stop"); }
    catch (TargetInvocationException e) when (e.InnerException is FixtureStop) { }
    Check(deleted == i + 1 && dialogType.GetProperty("SingleComposer")!.GetValue(dialog) == null, "Recomposition leaked its old texture/composer");
}
AttachComposer(); Call(dialog, "OnGuiClosed"); Check(deleted == 51, "Closing MOTD retained textures");
string tempLogo = Path.Combine(Path.GetTempPath(), "nss-memory-logo-" + Guid.NewGuid()); File.WriteAllText(tempLogo, "fixture"); Set(dialog, "serverLogoTempPath", tempLogo);
loaded.Add(dialog);
object system = New(motd.GetType("NewfiesCommunityMOTD.MotdClientSystem", true)!); Set(system, "capi", capi); Set(system, "currentDialog", dialog);
for (int i = 0; i < 10; i++) {
    object refresh = New(packetType); Set(refresh, "ForceShow", true); AttachComposer();
    try { Call(system, "OnMotdPacket", refresh); throw new Exception("Expected headless refresh stop"); }
    catch (TargetInvocationException e) when (e.InnerException is FixtureStop) { }
    Check(ReferenceEquals(Field(system, "currentDialog"), dialog) && loaded.Count == 1, "Repeat MOTD request registered another dialog");
}
Call(system, "Dispose"); Check(loaded.Count == 0 && !File.Exists(tempLogo), "MOTD shutdown retained dialog or temporary logo");
Console.WriteLine("PASS: 50 real engine composer replacements dispose textures before rebuild; 10 forced MOTD packets reuse one dialog; close/shutdown remove textures, loaded dialog and temporary logo.");

// The Suite has the same native composer lifetime requirements. In particular,
// each waypoint editor creates a fresh companion, unlike the reused Shift-L UI.
object SuiteDialog(string name) {
    object value = RuntimeHelpers.GetUninitializedObject(suite.GetType(name, true)!);
    dialogBase.GetField("capi", Flags)!.SetValue(value, capi);
    Set(value, "Composers", Activator.CreateInstance(dialogBase.GetNestedType("DlgComposers", Flags)!, value)!);
    return value;
}
void AttachSuiteComposer(object owner) {
    object composer = Activator.CreateInstance(composerType, Flags, null, new[] { capi, bounds, "suite-lifetime-fixture" }, null)!;
    Set(Field(composer, "staticElementsTexture"), "TextureId", deleted + 1); Set(owner, "SingleComposer", composer);
}
object investigator = SuiteDialog("BlockLogger.Frontend.InvestigatorDialog");
Set(investigator, "profilerLiveUpdates", true); Set(investigator, "profilerLiveRequestPending", true);
AttachSuiteComposer(investigator); loaded.Add(investigator); int suiteTextureStart = deleted;
Call(investigator, "OnGuiClosed");
Check(deleted == suiteTextureStart + 1 && investigator.GetType().GetProperty("SingleComposer")!.GetValue(investigator) == null, "Closed Shift-L retained its composer/texture");
Check(!(bool)Field(investigator, "profilerLiveUpdates") && !(bool)Field(investigator, "profilerLiveRequestPending"), "Closed Shift-L retained live profiler polling");
Set(investigator, "section", "webserver"); Set(investigator, "webView", "webmap");
Type webReply = suite.GetType("BlockLogger.Protocol.WebDashboardResponse", true)!;
object reply = New(webReply); Set(reply, "RequestId", 1L); Set(reply, "Success", true); Set(reply, "Message", "Closed reply fixture");
investigator.GetType().GetMethod("Show", new[] { webReply })!.Invoke(investigator, new[] { reply });
Check(investigator.GetType().GetProperty("SingleComposer")!.GetValue(investigator) == null, "Late reply rebuilt an invisible Shift-L composer");
Call(investigator, "Dispose"); Call(investigator, "Dispose");
Check(!loaded.Contains(investigator) && deleted == suiteTextureStart + 1, "Shift-L shutdown retained registration or disposed its texture twice");
for (int i = 0; i < 100; i++) {
    object companion = SuiteDialog("BlockLogger.Frontend.AdminWaypointTeleportDialog");
    AttachSuiteComposer(companion); loaded.Add(companion); int before = deleted;
    Call(companion, "OnGuiClosed"); Call(companion, "Dispose"); Call(companion, "Dispose");
    Check(!loaded.Contains(companion) && deleted == before + 1, "Repeated waypoint editors retained registered companions/textures");
}
Console.WriteLine("PASS: Shift-L closes release real engine textures and stop polling; late server replies create no invisible composer; shutdown unregisters once; 100 waypoint companions leave no retained dialog or double texture disposal.");

var cleanupTasks = new Queue<Action>();
Type eventsType = clientApiType.GetProperty("Event")!.PropertyType;
object events = Proxy(eventsType, (method, args) => { if (method.Name == "EnqueueMainThreadTask") { cleanupTasks.Enqueue((Action)args![0]!); return null; } throw new Exception(method.Name); });
object consentApi = Proxy(clientApiType, (method, args) => method.Name switch { "get_Gui" => gui, "get_Render" => render, "get_Event" => events, _ => null });
Type consentType = integrity.GetType("NewfiesAntiCheatClient.ModEvidenceConsentDialog", true)!;
for (int i = 0; i < 20; i++) {
    int decisions = 0; bool accepted = false;
    object consent = RuntimeHelpers.GetUninitializedObject(consentType);
    dialogBase.GetField("capi", Flags)!.SetValue(consent, consentApi);
    Set(consent, "Composers", Activator.CreateInstance(dialogBase.GetNestedType("DlgComposers", Flags)!, consent)!);
    Set(consent, "onDecision", (Action<bool>)(decision => { accepted = decision; decisions++; }));
    object composer = Activator.CreateInstance(composerType, Flags, null, new[] { consentApi, bounds, "consent-fixture" }, null)!;
    Set(Field(composer, "staticElementsTexture"), "TextureId", deleted + 1); Set(consent, "SingleComposer", composer);
    loaded.Add(consent); int before = deleted;
    if (i % 2 == 0) Call(consent, "Resolve", true);
    Call(consent, "OnGuiClosed");
    Check(decisions == 1 && accepted == (i % 2 == 0), "Closing changed the one-time consent decision");
    Check(deleted == before + 1 && loaded.Count == 1 && cleanupTasks.Count == 1, "Close did not free consent textures or deferred unregister incorrectly");
    cleanupTasks.Dequeue()();
    Check(loaded.Count == 0 && (bool)Field(consent, "disposed"), "Closed consent dialog remained registered");
    Call(consent, "Dispose"); Check(deleted == before + 1, "Repeated cleanup disposed textures twice");
}
Console.WriteLine("PASS: 20 consent dialogs free textures, defer unregister until after GUI event, preserve accepted/declined decisions exactly once and tolerate repeated disposal.");

static object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler) { object proxy = DispatchProxy.Create(type, typeof(AuditProxy)); ((AuditProxy)proxy).Handler = handler; return proxy; }
public class AuditProxy : DispatchProxy {
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
}
public sealed class FixtureStop : Exception { }
