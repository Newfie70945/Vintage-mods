using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;

const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY");
string[] paths = new[] { "DISCORD_ASSEMBLY", "CHATGUARD_ASSEMBLY", "PLAYERLIST_ASSEMBLY" }
    .Select(name => Path.GetFullPath(Environment.GetEnvironmentVariable(name) ?? throw new Exception("Set " + name))).ToArray();
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (string directory in new[] { game, Path.Combine(game, "Lib") }.Concat(paths.Select(p => Path.GetDirectoryName(p)!))) {
        string file = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(file)) return context.LoadFromAssemblyPath(file);
    }
    return null;
};
Assembly discord = AssemblyLoadContext.Default.LoadFromAssemblyPath(paths[0]);
Assembly guard = AssemblyLoadContext.Default.LoadFromAssemblyPath(paths[1]);
Assembly roster = AssemblyLoadContext.Default.LoadFromAssemblyPath(paths[2]);
if (OperatingSystem.IsLinux()) {
    var cairo = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "Lib", "cairo-sharp.dll"));
    NativeLibrary.SetDllImportResolver(cairo, (name, assembly, search) => name == "libcairo-2" ? NativeLibrary.Load("libcairo.so.2") : IntPtr.Zero);
}
object New(Type type) => Activator.CreateInstance(type, true)!;
object Field(object value, string name) => value.GetType().GetField(name, Flags)!.GetValue(value)!;
void Set(object value, string name, object? data) {
    var property = value.GetType().GetProperty(name, Flags);
    if (property != null) property.SetValue(value, data); else value.GetType().GetField(name, Flags)!.SetValue(value, data);
}
object? Call(object value, string name, params object?[] args) => value.GetType().GetMethods(Flags)
    .Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args);
void Check(bool success, string message) { if (!success) throw new Exception(message); }
void Fails(Action action) {
    try { action(); } catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException or IOException) { return; }
    throw new Exception("Expected operation to fail without changing persisted state");
}
object Delegate(Type type, Func<object?[], object?> handler) {
    var invoke = type.GetMethod("Invoke")!;
    var arguments = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
    var call = Expression.Invoke(Expression.Constant(handler), Expression.NewArrayInit(typeof(object), arguments.Select(a => Expression.Convert(a, typeof(object)))));
    return Expression.Lambda(type, Expression.Convert(call, invoke.ReturnType), arguments).Compile();
}

Type linkStoreType = discord.GetType("NewfiesServerEssentials.Discord.Linking.Services.DiscordLinkStore", true)!;
Type roleStoreType = discord.GetType("NewfiesServerEssentials.Discord.Linking.Services.DiscordRoleSyncStore", true)!;
Type apiType = linkStoreType.GetConstructors().Single().GetParameters()[0].ParameterType;
Type linkRecordType = discord.GetType("NewfiesServerEssentials.Discord.Linking.Models.DiscordLinkRecord", true)!;
Type roleRecordType = discord.GetType("NewfiesServerEssentials.Discord.Linking.Models.DiscordRoleSyncState", true)!;
object Link(string uid, string account) { object record = New(linkRecordType); Set(record, "PlayerUid", uid); Set(record, "PlayerName", "Alice"); Set(record, "DiscordUserId", account); return record; }
object Role(string uid, string code) { object state = New(roleRecordType); Set(state, "PlayerUid", uid); Set(state, "ManagedRoleCode", code); return state; }
var fixture = new StoreFixture(); object api = fixture.Make(apiType)!;
object links = Activator.CreateInstance(linkStoreType, api)!;
object roles = Activator.CreateInstance(roleStoreType, api)!;
Call(links, "Load"); Call(roles, "Load");
Call(links, "Upsert", Link("player-1", "101")); Call(roles, "Upsert", Role("player-1", "supporter"));
byte[] persistedLinks = fixture.Data["newfiesdiscordlink:database-v1"].ToArray();
byte[] persistedRoles = fixture.Data["newfiesdiscordlink:rolesync-v1"].ToArray();
fixture.ThrowWrites = true;
Fails(() => Call(links, "Upsert", Link("player-1", "202")));
Fails(() => Call(links, "RemoveByPlayerUid", "player-1"));
Fails(() => Call(links, "RemoveByDiscordUserId", "101"));
Check(((ICollection)Call(links, "GetByDiscordUserId", "101")!).Count == 1 && ((ICollection)Call(links, "GetByDiscordUserId", "202")!).Count == 0,
    "Failed link writes changed the reverse index");
Fails(() => Call(roles, "Upsert", Role("player-1", "admin")));
Fails(() => Call(roles, "Remove", "player-1"));
Check((string)Call(roles, "Get", "player-1")!.GetType().GetProperty("ManagedRoleCode")!.GetValue(Call(roles, "Get", "player-1"))! == "supporter",
    "Failed role write changed the remembered ownership");
Check(fixture.Data["newfiesdiscordlink:database-v1"].SequenceEqual(persistedLinks) && fixture.Data["newfiesdiscordlink:rolesync-v1"].SequenceEqual(persistedRoles),
    "Failed persistent writes changed stored bytes");
fixture.ThrowWrites = false;
Call(links, "Upsert", Link("player-1", "202"));
Check(((ICollection)Call(links, "GetByDiscordUserId", "101")!).Count == 0 && ((ICollection)Call(links, "GetByDiscordUserId", "202")!).Count == 1,
    "Recovered link write did not update both indexes");
Console.WriteLine("PASS: rejected link upsert/unlink/bulk-unlink and role upsert/remove restore their in-memory state; successful retry updates both indexes.");

foreach (string invalid in new[] { "{damaged", "null", "{\"SchemaVersion\":2}", "{\"LinksByPlayerUid\":{\"player-1\":null}}" }) {
    fixture.Data["newfiesdiscordlink:database-v1"] = Encoding.UTF8.GetBytes(invalid);
    Call(links, "Load"); int writes = fixture.Writes;
    Fails(() => Call(links, "Upsert", Link("new-player", "303")));
    Fails(() => Call(links, "RemoveByPlayerUid", "player-1"));
    Check(!(bool)linkStoreType.GetProperty("IsStorageReady")!.GetValue(links)! && fixture.Writes == writes && Encoding.UTF8.GetString(fixture.Data["newfiesdiscordlink:database-v1"]) == invalid,
        "Unreadable link state was overwritten");
}
fixture.Data["newfiesdiscordlink:rolesync-v1"] = Encoding.UTF8.GetBytes("{damaged roles");
Call(roles, "Load"); int roleWrites = fixture.Writes; Fails(() => Call(roles, "Upsert", Role("new-player", "admin")));
Check(!((bool)roleStoreType.GetProperty("IsStorageReady")!.GetValue(roles)!) && fixture.Writes == roleWrites, "Unreadable role state accepted a write");
fixture.Data.Remove("newfiesdiscordlink:database-v1");
Type linkServiceType = discord.GetType("NewfiesServerEssentials.Discord.Linking.DiscordLinkService", true)!;
Type linkConfigType = discord.GetType("NewfiesServerEssentials.Discord.Linking.DiscordLinkConfig", true)!;
object linkConfig = New(linkConfigType);
object linkService = Activator.CreateInstance(linkServiceType, api, Delegate(typeof(Func<>).MakeGenericType(linkConfigType), _ => linkConfig))!;
Type roleServiceType = discord.GetType("NewfiesServerEssentials.Discord.Linking.DiscordRoleSyncService", true)!;
Type roleConfigType = discord.GetType("NewfiesServerEssentials.Discord.Linking.DiscordRoleSyncConfig", true)!;
object roleConfig = New(roleConfigType); Set(roleConfig, "Enabled", true);
object roleService = Activator.CreateInstance(roleServiceType, api, Delegate(typeof(Func<>).MakeGenericType(roleConfigType), _ => roleConfig), linkService)!;
Set(roleService, "active", true);
int playerReads = fixture.PlayerReads;
Call(roleService, "ReconcileFromDiscord", "player-1", "101", new ulong[] { 1 }, "fixture");
Call(roleService, "RequestSyncForOnline", "fixture");
Check(fixture.PlayerReads == playerReads && !(bool)roleServiceType.GetProperty("IsStorageReady")!.GetValue(roleService)!,
    "Role reconciliation accessed players despite unreadable ownership state");
Console.WriteLine("PASS: malformed/future/null link state preserves raw save bytes, role mutations are disabled, and reconciliation stops before touching players.");

Type providerType = discord.GetType("NewfiesServerEssentials.Discord.Integration.MotdDiscordProvider", true)!;
var providerCtor = providerType.GetConstructors(Flags).Single(c => c.GetParameters().Length == 5);
var providerParameters = providerCtor.GetParameters();
Type integrationConfigType = providerParameters[0].ParameterType.GetGenericArguments()[0];
object integration = New(integrationConfigType); Set(integration, "GuildId", 1UL);
object motd = integrationConfigType.GetProperty("Motd")!.GetValue(integration)!;
Set(motd, "Enabled", true); Set(motd, "AnnouncementsChannelId", 11UL); Set(motd, "UpdatesChannelId", 12UL); Set(motd, "EventsChannelId", 13UL);
Type readReturn = providerParameters[2].ParameterType.GetMethod("Invoke")!.ReturnType;
Type messageListType = readReturn.GetGenericArguments()[0], messageType = messageListType.GetGenericArguments()[0];
long now = 1000; int reads = 0, errors = 0, notifications = 0; bool fail = false;
object? pending = null;
object Messages(ulong channel) {
    object message = StoreFixture.Proxy(messageType, (method, _) => method.Name switch {
        "get_Content" => "Channel " + channel, "get_Id" => channel, "get_Timestamp" => DateTimeOffset.UnixEpoch,
        _ => StoreFixture.Default(method.ReturnType)
    });
    var messages = Array.CreateInstance(messageType, 1); messages.SetValue(message, 0); return messages;
}
object Finished(object messages) => typeof(Task).GetMethods().Single(m => m.Name == "FromResult").MakeGenericMethod(messageListType).Invoke(null, new[] { messages })!;
object readDelegate = Delegate(providerParameters[2].ParameterType, values => {
    reads++; if (fail) throw new IOException("Injected Discord REST failure");
    if (pending != null) { object task = pending.GetType().GetProperty("Task")!.GetValue(pending)!; pending = null; return task; }
    return Finished(Messages((ulong)values[0]!));
});
object provider = providerCtor.Invoke(new[] {
    Delegate(providerParameters[0].ParameterType, _ => integration), (Func<bool>)(() => true), readDelegate,
    (Func<long>)(() => now), (Action<Exception>)(_ => errors++)
});
providerType.GetEvent("ContentChanged")!.AddEventHandler(provider, (Action)(() => notifications++));
Call(provider, "GetAnnouncements");
for (int i = 0; i < 1000; i++) { Call(provider, "GetAnnouncements"); Call(provider, "GetUpdates"); Call(provider, "GetEvents"); }
Check(reads == 3 && notifications == 1, "Join/getter burst refetched cached feeds");
now += 30_001; Call(provider, "GetAnnouncements");
Check(reads == 6 && notifications == 1, "Unchanged feeds triggered redundant refresh notification");
fail = true; now += 30_001; object lastGood = Call(provider, "GetAnnouncements")!; int failedReads = reads;
for (int i = 0; i < 100; i++) Call(provider, "GetAnnouncements");
Check(reads == failedReads && errors == 1 && ((ICollection)lastGood).Count == 1, "Feed failure lost the last good feed or bypassed retry backoff");
fail = false; now += 10_001; Call(provider, "GetAnnouncements");
Check(reads == failedReads + 3, "Feed refresh did not recover after retry backoff");
Type completionType = typeof(TaskCompletionSource<>).MakeGenericType(messageListType);
object completion = Activator.CreateInstance(completionType)!; pending = completion;
Call(provider, "QueueRefresh", true); int heldReads = reads;
for (int i = 0; i < 100; i++) Call(provider, "GetAnnouncements");
Check(reads == heldReads, "Concurrent feed reads were not coalesced");
((IDisposable)provider).Dispose();
completionType.GetMethod("SetResult")!.Invoke(completion, new[] { Messages(11) });
Check(SpinWait.SpinUntil(() => !(bool)Field(provider, "refreshing"), 5000), "Feed task did not finish after disposal");
Check(((ICollection)Call(provider, "GetAnnouncements")!).Count == 0 && notifications == 1, "Disposed feed provider published a late result");
Console.WriteLine("PASS: 3,000 feed reads use three REST reads, unchanged content stays quiet, failures keep the last good feed with backoff, and disposal rejects pending results.");

Type clientApiType = AssemblyLoadContext.Default.Assemblies.Single(a => a.GetName().Name == "VintagestoryAPI").GetType("Vintagestory.API.Client.ICoreClientAPI", true)!;
Type guiType = clientApiType.GetProperty("Gui")!.PropertyType, renderType = clientApiType.GetProperty("Render")!.PropertyType;
Type dialogBase = guiType.Assembly.GetType("Vintagestory.API.Client.GuiDialog", true)!;
IList loaded = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(dialogBase))!;
int deleted = 0;
object render = StoreFixture.Proxy(renderType, (method, _) => method.Name switch { "DeleteTexture" => Delete(), _ => StoreFixture.Default(method.ReturnType) });
object? Delete() { deleted++; return null; }
object gui = StoreFixture.Proxy(guiType, (method, _) => method.Name switch {
    "get_LoadedGuis" or "get_OpenedGuis" => loaded, "CreateCompo" => throw new FixtureStop(), "DeleteTexture" => Delete(), _ => StoreFixture.Default(method.ReturnType)
});
var clientFixture = new StoreFixture();
object capi = StoreFixture.Proxy(clientApiType, (method, _) => method.Name switch {
    "get_Gui" => gui, "get_Render" => render, _ => clientFixture.Make(method.ReturnType)
});
Type composerType = guiType.Assembly.GetType("Vintagestory.API.Client.GuiComposer", true)!;
Type boundsType = guiType.Assembly.GetType("Vintagestory.API.Client.ElementBounds", true)!;
object bounds = boundsType.GetProperty("Empty", Flags)!.GetValue(null)!;
object HeadlessDialog(Type type) {
    object dialog = RuntimeHelpers.GetUninitializedObject(type);
    dialogBase.GetField("capi", Flags)!.SetValue(dialog, capi);
    Set(dialog, "Composers", Activator.CreateInstance(dialogBase.GetNestedType("DlgComposers", Flags)!, dialog)!);
    return dialog;
}
void Attach(object dialog) {
    object composer = Activator.CreateInstance(composerType, Flags, null, new[] { capi, bounds, "companion-fixture" }, null)!;
    Set(Field(composer, "staticElementsTexture"), "TextureId", deleted + 1); Set(dialog, "SingleComposer", composer);
}
Type guardDialogType = guard.GetType("NewfiesChatGuard.ChatGuardDialog", true)!;
Type rosterDialogType = roster.GetType("NewfiesPlayerList.PublicPlayerListHud", true)!;
object guardDialog = HeadlessDialog(guardDialogType), rosterDialog = HeadlessDialog(rosterDialogType);
Set(rosterDialog, "snapshot", New(roster.GetType("NewfiesPlayerList.PublicPlayerListResponse", true)!));
foreach (object dialog in new[] { guardDialog, rosterDialog }) {
    for (int i = 0; i < 20; i++) {
        Attach(dialog); int before = deleted;
        try { Call(dialog, "Compose"); throw new Exception("Expected headless composition stop"); }
        catch (TargetInvocationException error) when (error.InnerException is FixtureStop) { }
        Check(deleted == before + 1 && dialogBase.GetProperty("SingleComposer")!.GetValue(dialog) == null, "GUI refresh retained its previous composer");
    }
}
Attach(guardDialog); int closeBefore = deleted; Call(guardDialog, "OnGuiClosed");
Check(deleted == closeBefore + 1, "Closing ChatGuard retained textures");
Type guardClientType = guard.GetType("NewfiesChatGuard.ChatGuardGuiClient", true)!;
object guardClient = Activator.CreateInstance(guardClientType, capi)!; Set(guardClient, "dialog", guardDialog); loaded.Add(guardDialog);
Type responseType = guard.GetType("NewfiesChatGuard.ChatGuardGuiResponse", true)!;
object fresh = New(responseType); Set(fresh, "RequestId", 2L); Set(fresh, "Message", "latest result"); Call(guardClient, "OnResponse", fresh);
object old = New(responseType); Set(old, "RequestId", 1L); Set(old, "Message", "obsolete result"); Call(guardClient, "OnResponse", old);
Check((string)Field(guardDialog, "status") == "latest result", "Older GUI response replaced the latest result");
Call(guardClient, "Dispose"); Set(old, "OpenDialog", true); Call(guardClient, "OnResponse", old); Call(guardClient, "Dispose");
Check(loaded.Count == 0 && guardClientType.GetField("dialog", Flags)!.GetValue(guardClient) == null && !(bool)Call(guardClient, "Toggle")!,
    "Disposed ChatGuard reopened on a late packet");
Type rosterClientType = roster.GetType("NewfiesPlayerList.PublicPlayerListClient", true)!;
object rosterClient = Activator.CreateInstance(rosterClientType, capi)!; Set(rosterClient, "hud", rosterDialog); loaded.Add(rosterDialog);
Call(rosterClient, "Dispose"); Check(loaded.Count == 0, "Disposed player-list HUD remained registered");
Console.WriteLine("PASS: 40 real engine composer replacements free textures before rebuild; ChatGuard close/disposal and PlayerList disposal unregister dialogs; old/late ChatGuard replies are ignored.");

Type lampType = roster.GetType("NewfiesPlayerList.SupporterLampClient", true)!;
object lamp = Activator.CreateInstance(lampType, capi)!;
object color = Call(lamp, "UpdateColor", "player-1", (byte)33, (byte)7, (byte)16)!;
Type registryType = roster.GetType("NewfiesPlayerList.SupporterLampLightRegistry", true)!;
var registry = (IDictionary)registryType.GetField("Active", Flags)!.GetValue(null)!;
object registryColor = registry["player-1"]!;
for (int i = 0; i < 10_000; i++) Check(ReferenceEquals(color, Call(lamp, "UpdateColor", "player-1", (byte)33, (byte)7, (byte)16)), "Unchanged supporter color allocated a new state");
Check(ReferenceEquals(registryColor, registry["player-1"]), "Unchanged color repeatedly replaced registry arrays");
Check(!ReferenceEquals(color, Call(lamp, "UpdateColor", "player-1", (byte)34, (byte)7, (byte)16)), "Changed color did not update the renderer state");
Call(lamp, "Dispose"); Check(!registry.Contains("player-1"), "Lamp shutdown retained its registry entry");
Console.WriteLine("PASS: 10,000 unchanged supporter-light polls reuse client and registry colors; real color changes update and shutdown clears the registry.");
Console.WriteLine("Companion improvement checks passed without network access, GL context, or live server state.");

public sealed class FixtureStop : Exception { }
public class FixtureProxy : DispatchProxy {
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
public sealed class StoreFixture {
    public Dictionary<string, byte[]> Data = new();
    public bool ThrowWrites;
    public int Writes, PlayerReads;
    readonly Dictionary<Type, object> proxies = new();
    public static object? Default(Type type) => type == typeof(void) ? null : type == typeof(string) ? "" : type.IsArray ? Array.CreateInstance(type.GetElementType()!, 0) : type.IsValueType ? Activator.CreateInstance(type) : null;
    public static object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler) {
        object result = DispatchProxy.Create(type, typeof(FixtureProxy)); ((FixtureProxy)result).Handler = handler; return result;
    }
    public object? Make(Type type) {
        if (!type.IsInterface) return Default(type);
        if (proxies.TryGetValue(type, out var existing)) return existing;
        object result = Proxy(type, (method, args) => {
            if (method.Name == "GetData") return Data.GetValueOrDefault((string)args![0]!);
            if (method.Name == "StoreData") {
                if (ThrowWrites) throw new IOException("Injected store failure");
                Writes++; Data[(string)args![0]!] = ((byte[])args[1]!).ToArray(); return null;
            }
            if (method.Name == "get_Server" || method.Name == "get_Players" || method.Name == "PlayerByUid") PlayerReads++;
            return Make(method.ReturnType);
        });
        proxies[type] = result; return result;
    }
}
