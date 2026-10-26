// Regression checks for the repaired Perf3 follow-up audit findings.
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY"));
string dll = Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? throw new Exception("Set NSS_ASSEMBLY"));
var roots = new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods"), Path.GetDirectoryName(dll)! };
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (var root in roots) { var file = Path.Combine(root, name.Name + ".dll"); if (File.Exists(file)) return context.LoadFromAssemblyPath(file); }
    return null;
};
var engine = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryLib.dll"));
var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
Type E(string name) => engine.GetType(name, true)!;
Type M(string name) => mod.GetType(name, true)!;
const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
void Set(object value, string name, object? data) {
    var field = value.GetType().GetField(name, fields);
    if (field != null) field.SetValue(value, data);
    else value.GetType().GetProperty(name, fields)!.SetValue(value, data);
}

// Actual native inventory constructors and ActiveHotbarSlot getter; no world.
object player = RuntimeHelpers.GetUninitializedObject(E("Vintagestory.Server.ServerPlayer"));
var worldDataField = player.GetType().GetField("worlddata", fields)!;
object worldData = RuntimeHelpers.GetUninitializedObject(worldDataField.FieldType);
Set(worldData, "PlayerUID", "audit-fixture");
worldDataField.SetValue(player, worldData);
object manager = RuntimeHelpers.GetUninitializedObject(E("Vintagestory.Server.ServerPlayerInventoryManager"));
Type managerBase = E("Vintagestory.Common.PlayerInventoryManager");
managerBase.GetField("player")!.SetValue(manager, player);
var inventoriesField = managerBase.GetField("Inventories")!;
object inventories = Activator.CreateInstance(inventoriesField.FieldType)!;
inventoriesField.SetValue(manager, inventories);
player.GetType().GetField("inventoryMgr", fields)!.SetValue(player, manager);
object hotbar = Activator.CreateInstance(E("Vintagestory.Common.InventoryPlayerHotbar"), "hotbar", "audit-fixture", null)!;
object backpack = Activator.CreateInstance(E("Vintagestory.Common.InventoryPlayerBackpacks"), "backpack", "audit-fixture", null)!;
var add = inventoriesField.FieldType.GetMethod("Add", new[] { typeof(string), hotbar.GetType().BaseType!.BaseType! });
add ??= inventoriesField.FieldType.GetMethods().Single(x => x.Name == "Add" && x.GetParameters().Length == 2);
add.Invoke(inventories, new[] { (object)"hotbar-audit-fixture", hotbar });
add.Invoke(inventories, new[] { (object)"backpack-audit-fixture", backpack });

Type guardType = M("BlockLogger.AntiCheat.PacketIngressGuard");
var constructor = guardType.GetConstructors(fields).Single();
Type apiType = constructor.GetParameters()[0].ParameterType;
Type eventsType = apiType.GetProperty("Event")!.PropertyType;
int queuedReports = 0;
object events = Proxy(eventsType, (method, args) => {
    if (method.Name == "EnqueueMainThreadTask") { queuedReports++; return null; }
    throw new Exception("Unexpected event access " + method.Name);
});
object api = Proxy(apiType, (method, args) => method.Name == "get_Event" ? events : throw new Exception("Unexpected API access " + method.Name));
var healthType=constructor.GetParameters()[3].ParameterType;
object health=System.Linq.Expressions.Expression.Lambda(healthType,System.Linq.Expressions.Expression.Default(healthType.GetGenericArguments()[0])).Compile();
object guard = constructor.Invoke(new[] { api, Activator.CreateInstance(M("BlockLogger.AntiCheat.AntiCheatConfig")), null,health });
Set(guard, "active", true);
object client = RuntimeHelpers.GetUninitializedObject(E("Vintagestory.Server.ConnectedClient"));
Set(client, "Player", player);
MethodInfo validate = guardType.GetMethod("ValidatePacket", fields)!;
int count = (int)hotbar.GetType().GetProperty("Count")!.GetValue(hotbar)!;
foreach (int slot in new[] { 0, 9, 10, 11, 12, 13 }) {
    managerBase.GetProperty("ActiveHotbarSlotNumber")!.SetValue(manager, slot);
    object? nativeSlot = managerBase.GetProperty("ActiveHotbarSlot")!.GetValue(manager);
    object payload = Activator.CreateInstance(E("Packet_SelectedHotbarSlot"))!;
    Set(payload, "SlotNumber", slot);
    object packet = Activator.CreateInstance(E("Packet_Client"))!;
    Set(packet, "Id", 13); Set(packet, "SelectedHotbarSlot", payload);
    bool accepted = (bool)validate.Invoke(guard, new[] { client, packet })!;
    Console.WriteLine($"slot={slot};nativeSlotExists={nativeSlot != null};hotbarCount={count};GuardAccepted={accepted}");
    if (nativeSlot == null || !accepted) throw new Exception("Native active inventory selection was rejected");
}
if (queuedReports != 0) throw new Exception("Legitimate selections generated malformed findings");
foreach (int slot in new[] { -1, 14, int.MaxValue }) {
    object payload = Activator.CreateInstance(E("Packet_SelectedHotbarSlot"))!; Set(payload, "SlotNumber", slot);
    object packet = Activator.CreateInstance(E("Packet_Client"))!; Set(packet, "Id", 13); Set(packet, "SelectedHotbarSlot", payload);
    if ((bool)validate.Invoke(guard, new[] { client, packet })!) throw new Exception("Invalid active slot accepted: " + slot);
}
Console.WriteLine("PASS: native hotbar/backpack selections accepted without findings; negative/out-of-range selections rejected.");

// The special skill/tool slot shifts the native backpack start from 10 to 11.
object skillSlot = hotbar.GetType().GetProperty("Item")!.GetValue(hotbar, new object[] { 10 })!;
Type stackType = Type.GetType("Vintagestory.API.Common.ItemStack, VintagestoryAPI", true)!;
object stack = RuntimeHelpers.GetUninitializedObject(stackType); Set(stack, "StackSize", 1); Set(skillSlot, "Itemstack", stack);
foreach (int slot in new[] { 10, 11, 12, 13, 14 }) {
    managerBase.GetProperty("ActiveHotbarSlotNumber")!.SetValue(manager, slot);
    CheckNative(slot);
}
void CheckNative(int slot) {
    if (managerBase.GetProperty("ActiveHotbarSlot")!.GetValue(manager) == null) throw new Exception("Occupied skill-slot native fixture invalid");
    object payload = Activator.CreateInstance(E("Packet_SelectedHotbarSlot"))!; Set(payload, "SlotNumber", slot);
    object packet = Activator.CreateInstance(E("Packet_Client"))!; Set(packet, "Id", 13); Set(packet, "SelectedHotbarSlot", payload);
    if (!(bool)validate.Invoke(guard, new[] { client, packet })!) throw new Exception("Occupied skill-slot selection rejected");
}
Console.WriteLine("PASS: occupied skill-slot preserves shifted native hotbar/backpack bounds.");

// Small empty custom envelope: no payload deserialization or live connection.
object custom = Activator.CreateInstance(E("Packet_CustomPacket"))!;
Set(custom, "ChannelId", 1); Set(custom, "MessageId", -1); Set(custom, "Data", Array.Empty<byte>());
object customPacket = Activator.CreateInstance(E("Packet_Client"))!;
Set(customPacket, "Id", 23); Set(customPacket, "CustomPacket", custom);
if ((bool)validate.Invoke(guard, new[] { client, customPacket })!) throw new Exception("Negative custom message index passed guard");
object channel = Activator.CreateInstance(E("Vintagestory.Server.NetworkChannel"), null, 1, "audit-fixture")!;
try {
    channel.GetType().GetMethod("OnPacket")!.Invoke(channel, new[] { custom, player });
    throw new Exception("Expected native index failure not reproduced");
} catch (TargetInvocationException ex) when (ex.InnerException is IndexOutOfRangeException) {
    Console.WriteLine("PASS: ingress rejects negative custom message index before native NetworkChannel.OnPacket would throw.");
}

Type findingType = M("NewfiesAntiCheat.Shared.Models.ScanFindingDto");
object Finding() {
    object f = Activator.CreateInstance(findingType)!;
    foreach (var (name, value) in new (string, object)[] { ("Code", "PACKET_MALFORMED_PACKET"), ("Scanner", "ServerBehavior"), ("Severity", 3), ("Subject", "audit-fixture"), ("Evidence", "packetId=13;reason=shape-or-bounds") })
        findingType.GetProperty(name)!.SetValue(f, value);
    return f;
}
object current = Finding();
var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(findingType))!;
list.Add(current);
MethodInfo analyze = M("BlockLogger.AntiCheat.AntiCheatFindingIntelligence").GetMethod("Analyze")!;
int Score() { object result = analyze.Invoke(null, new[] { current, list })!; return (int)result.GetType().GetProperty("ConfidenceScore")!.GetValue(result)!; }
int before = Score();
list.Add(Finding()); list.Add(Finding()); list.Add(Finding());
int after = Score();
if (before != 78 || after != before) throw new Exception("Duplicate evidence inflated confidence");
Console.WriteLine($"PASS: identical repeated evidence retains displayed confidence {before} -> {after}.");

// Production ingress finding callbacks remain bound to the observed player session.
var pending=new List<Action>();int findings=0,logs=0;
var logType=apiType.GetInterfaces().Append(apiType).SelectMany(t=>t.GetProperties()).First(p=>p.Name=="Logger").PropertyType;
object logProxy=Proxy(logType,(m,a)=>{logs++;return m.ReturnType==typeof(bool)?false:null;});
object sessionEvents=Proxy(eventsType,(m,a)=>{if(m.Name=="EnqueueMainThreadTask"){pending.Add((Action)a![0]!);return null;}throw new Exception("Unexpected event "+m.Name);});
object sessionApi=Proxy(apiType,(m,a)=>m.Name=="get_Event"?sessionEvents:m.Name=="get_Logger"?logProxy:throw new Exception("Unexpected API "+m.Name));
var reportType=constructor.GetParameters()[2].ParameterType;
Action found=()=>findings++;
var parameters=reportType.GetMethod("Invoke")!.GetParameters().Select(p=>System.Linq.Expressions.Expression.Parameter(p.ParameterType)).ToArray();
var reporter=System.Linq.Expressions.Expression.Lambda(reportType,System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(found)),parameters).Compile();
object sessionGuard=constructor.Invoke(new[]{sessionApi,Activator.CreateInstance(M("BlockLogger.AntiCheat.AntiCheatConfig")),reporter,health});Set(sessionGuard,"active",true);
var report=guardType.GetMethod("Report",fields)!;object severity=Enum.Parse(report.GetParameters()[4].ParameterType,"High");
Set(client,"Player",player);report.Invoke(sessionGuard,new object?[]{client,"PACKET_TEST_REPLACED","test","test",severity,false,null,null});
Set(client,"Player",RuntimeHelpers.GetUninitializedObject(player.GetType()));pending.Single()();
if(findings!=0||logs!=0)throw new Exception("Replaced session received the previous player's finding");
pending.Clear();Set(client,"Player",null);report.Invoke(sessionGuard,new object?[]{client,"PACKET_TEST_ANONYMOUS","test","test",severity,false,null,null});Set(client,"Player",player);pending.Single()();
if(findings!=0||logs==0)throw new Exception("Pre-authentication finding was assigned to authenticated player");
Console.WriteLine("PASS: compiled ingress callbacks discard replaced sessions and keep anonymous observations anonymous; no world access.");


// Exercise the actual dispatch guard, preserving native tab/ground slots and batch sizes.
bool Dispatch(int id,string field,object? payload) {
 object packet=Activator.CreateInstance(E("Packet_Client"))!;Set(packet,"Id",id);Set(packet,field,payload);
 return (bool)validate.Invoke(guard,new[]{client,packet})!;
}
foreach(var envelope in new[]{(7,"ActivateInventorySlot","Packet_ActivateInventorySlot"),(8,"MoveItemstack","Packet_MoveItemstack"),(9,"Flipitemstacks","Packet_FlipItemstacks"),(30,"InvOpenedClosed","Packet_InvOpenedClosed")}) {
 if(Dispatch(envelope.Item1,envelope.Item2,null))throw new Exception("Missing inventory payload accepted");
 object payload=Activator.CreateInstance(E("Packet_Client").GetField(envelope.Item2)!.FieldType)!;
 if(Dispatch(envelope.Item1,envelope.Item2,payload))throw new Exception("Missing inventory ID accepted");
 if(envelope.Item1==30)Set(payload,"InventoryId","ground-audit-fixture");
 else { Set(payload,"TargetInventoryId","custom-tabbed-audit-fixture");if(envelope.Item1 is 8 or 9)Set(payload,"SourceInventoryId","hotbar-audit-fixture"); }
 if(envelope.Item1==8) {
   Set(payload,"Quantity",-1);if(Dispatch(8,envelope.Item2,payload))throw new Exception("Negative transfer accepted");
   Set(payload,"Quantity",int.MaxValue); // Native all-stack/modded batch requests remain unchanged.
   Set(payload,"TargetSlot",-1); // Leave special slot interpretation to native inventory implementation.
 }
 if(!Dispatch(envelope.Item1,envelope.Item2,payload))throw new Exception("Valid inventory envelope rejected");
}
object entityInteraction=Activator.CreateInstance(E("Packet_EntityInteraction"))!;
Set(entityInteraction,"SelectionBoxIndex",-1);
if(Dispatch(17,"EntityInteraction",entityInteraction))throw new Exception("Negative entity selection accepted");
Set(entityInteraction,"SelectionBoxIndex",0);
if(!Dispatch(17,"EntityInteraction",entityInteraction))throw new Exception("Valid entity interaction rejected");
Console.WriteLine("PASS: production inventory envelopes, negative transfer rejection, full-size batches, special slots and entity selection validation.");


object creation=Activator.CreateInstance(E("Packet_Client").GetField("CreateItemstack")!.FieldType)!;
Set(creation,"TargetInventoryId","hotbar-audit-fixture");Set(creation,"TargetSlot",0);
Set(creation,"Itemstack",Activator.CreateInstance(creation.GetType().GetField("Itemstack")!.FieldType));
var modeType=worldData.GetType().GetField("GameMode",fields)!.FieldType;
Set(worldData,"connected",true);
Set(worldData,"GameMode",Enum.Parse(modeType,"Survival"));
if(Dispatch(10,"CreateItemstack",creation))throw new Exception("Survival creative creation accepted");
Set(worldData,"GameMode",Enum.Parse(modeType,"Creative"));
if(!Dispatch(10,"CreateItemstack",creation))throw new Exception("Valid creative destination rejected");
Set(creation,"TargetSlot",count);
if(Dispatch(10,"CreateItemstack",creation))throw new Exception("Out-of-bounds creative slot accepted");
Set(creation,"TargetSlot",0);Set(creation,"TargetInventoryId","absent-fixture");
if(Dispatch(10,"CreateItemstack",creation))throw new Exception("Unknown creative destination accepted");
Console.WriteLine("PASS: creative creation requires creative mode, a known inventory and a bounded destination slot.");


// Occurrence totals survive replacement of the current same-code evidence row.
var occurrenceSession=Activator.CreateInstance(M("BlockLogger.AntiCheat.AntiCheatSession"))!;
var occurrenceFinding=Activator.CreateInstance(mod.GetType("NewfiesAntiCheat.Shared.Models.ScanFindingDto",true)!)!;
Set(occurrenceFinding,"Code","PACKET_NOCLIP");Set(occurrenceFinding,"Scanner","ServerBehavior");
var storeOccurrence=M("BlockLogger.AntiCheat.AntiCheatService").GetMethod("StoreServerBehaviorObservation",BindingFlags.NonPublic|BindingFlags.Static)!;
storeOccurrence.Invoke(null,[occurrenceSession,occurrenceFinding,1000L]);storeOccurrence.Invoke(null,[occurrenceSession,occurrenceFinding,2000L]);
var occurrenceMap=(IDictionary)occurrenceSession.GetType().GetProperty("ServerOccurrences")!.GetValue(occurrenceSession)!;
var occurrenceRow=occurrenceMap["PACKET_NOCLIP"]!;
if((long)occurrenceRow.GetType().GetProperty("Count")!.GetValue(occurrenceRow)!=2L || (long)occurrenceRow.GetType().GetProperty("FirstSeenUtc")!.GetValue(occurrenceRow)!=1000L || (long)occurrenceRow.GetType().GetProperty("LastSeenUtc")!.GetValue(occurrenceRow)!=2000L)throw new Exception("Occurrence totals/timestamps incorrect");
if(((IList)occurrenceSession.GetType().GetProperty("Findings")!.GetValue(occurrenceSession)!).Count!=1)throw new Exception("Occurrence counting duplicated risk evidence rows");
Console.WriteLine("PASS: recorded occurrence counts/timestamps are separate from current deduplicated findings.");

static object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler) {
    object result = DispatchProxy.Create(type, typeof(AuditProxy));
    ((AuditProxy)result).Handler = handler; return result;
}
public class AuditProxy : DispatchProxy {
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
}
