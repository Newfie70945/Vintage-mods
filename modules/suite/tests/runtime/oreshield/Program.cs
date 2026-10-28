using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY."));
string modPath = Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? args.FirstOrDefault() ?? throw new Exception("Set NSS_ASSEMBLY."));
string output = Path.GetDirectoryName(modPath)!;
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (string root in new[] { output, game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") }) {
        string file = Path.Combine(root, name.Name + ".dll");
        if (File.Exists(file)) return context.LoadFromAssemblyPath(file);
    }
    return null;
};
var library = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryLib.dll"));
var apiAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryAPI.dll"));
var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);
Type T(string name) => library.GetType(name) ?? apiAssembly.GetType(name) ?? mod.GetType(name) ?? throw new Exception(name);
FieldInfo Field(Type type, string name) {
    for (Type? t = type; t != null; t = t.BaseType) {
        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (f != null) return f;
    }
    throw new Exception("Missing field " + name);
}
void Set(object target, string name, object? value) => Field(target.GetType(), name).SetValue(target, value);
object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, args);
object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler) {
    var result = DispatchProxy.Create(type, typeof(ApiProxy)); ((ApiProxy)result).Handler = handler; return result;
}
object? Default(Type t) => t == typeof(void) ? null : t.IsValueType ? Activator.CreateInstance(t) : null;
var serverType = T("Vintagestory.Server.ServerMain");
object server = RuntimeHelpers.GetUninitializedObject(serverType);
var profiler = Field(serverType, "FrameProfiler");
profiler.SetValue(null, Activator.CreateInstance(profiler.FieldType, "ore-fixture"));
var poolType = T("Vintagestory.Common.ChunkDataPool");
object pool = Activator.CreateInstance(poolType, 32, server)!;
var chunkType = T("Vintagestory.Server.ServerChunk");
object chunk = chunkType.GetMethod("CreateNew")!.Invoke(null, new[] { pool })!;
object data = chunkType.GetProperty("Data")!.GetValue(chunk)!;
var setBlock = data.GetType().GetMethod("SetBlockUnsafe")!;
int Index(int x, int y, int z) => (y * 32 + z) * 32 + x;
int buried = Index(10, 10, 10), exposed = Index(20, 10, 10), placed = Index(15, 10, 10);
int hiddenOnlyVariant = Index(27, 9, 18);
for (int i = 0; i < 32768; i++) setBlock.Invoke(data, new object[] { i, 1 });
foreach (int i in new[] { buried, exposed, placed }) setBlock.Invoke(data, new object[] { i, 2 });
setBlock.Invoke(data, new object[] { hiddenOnlyVariant, 3 });
setBlock.Invoke(data, new object[] { exposed + 1, 0 });
var accessorType = T("Vintagestory.API.Common.IBlockAccessor");
object[] fixtureBlocks = Enumerable.Range(0, 5).Select(id => { var block = Activator.CreateInstance(T("Vintagestory.API.Common.Block"))!; Set(block, "BlockId", id); return block; }).ToArray();
int worldReads = 0;
bool deniedResyncProbe=false;
object accessor = Proxy(accessorType, (method, args) => {
    if (method.Name == "GetChunkAtBlockPos" && deniedResyncProbe)return chunk;
    if (method.Name == "GetChunk") worldReads++;
    if (method.Name == "GetBlock" && args?[0]?.GetType() == T("Vintagestory.API.MathTools.BlockPos")) {
        object p = args[0]!;
        int x = (int)Field(p.GetType(), "X").GetValue(p)!, y = (int)Field(p.GetType(), "Y").GetValue(p)!, z = (int)Field(p.GetType(), "Z").GetValue(p)!;
        int id = (int)data.GetType().GetMethod("GetBlockIdUnsafe", new[] { typeof(int) })!.Invoke(data, new object[] { Index(x & 31, y & 31, z & 31) })!;
        return fixtureBlocks[id];
    }
    return method.Name == "GetChunk" && args?.Length == 3 && args.All(x => Convert.ToInt32(x) == 0) ? chunk : Default(method.ReturnType);
});
object worldMap = RuntimeHelpers.GetUninitializedObject(T("Vintagestory.Server.ServerWorldMap"));
Set(worldMap, "RelaxedBlockAccess", accessor); Set(server, "WorldMap", worldMap);
var virtualizerType = T("BlockLogger.AntiCheat.OreShieldBlockVirtualizer");
var configType = T("BlockLogger.AntiCheat.AntiCheatConfig");
object config = Activator.CreateInstance(configType)!;
configType.GetProperty("OreShieldEnabled")!.SetValue(config, true);
var paletteType = T("BlockLogger.AntiCheat.OreShieldPalette");
object palette = RuntimeHelpers.GetUninitializedObject(paletteType);
Set(palette, "<Opaque>k__BackingField", new[] { false, true, true, true, true });
Set(palette, "<ProtectedOre>k__BackingField", new[] { false, false, true, true, true });
Set(palette, "<HostByBlock>k__BackingField", new[] { 0, 0, 1, 1, 1 });
var errors = new List<string>();
Type apiType = Field(virtualizerType, "api").FieldType;
var placedSave = new Dictionary<string, byte[]>();
var queuedSync = new List<Action>();
object? veinMiner = null;
bool failStore = false;
object api = Proxy(apiType, (method, arguments) => method.Name switch {
    "get_Event" => Proxy(method.ReturnType, (m, a) => { if (m.Name == "EnqueueMainThreadTask") { queuedSync.Add((Action)a![0]!); return null; } if (m.Name == "RegisterCallback") { var callback = (Delegate)a![0]!; queuedSync.Add(() => callback.DynamicInvoke(0f)); return 1L; } return m.Name == "RegisterGameTickListener" ? 1L : Default(m.ReturnType); }),
    "get_Logger" => Proxy(method.ReturnType, (m, a) => { if (m.Name == "Error") errors.Add(string.Join(" ", (a ?? Array.Empty<object>()).SelectMany(v => v is object[] nested ? nested : new[] { v }))); return Default(m.ReturnType); }),
    "get_WorldManager" => Proxy(method.ReturnType, (m, a) => m.Name == "get_SaveGame" ? Proxy(m.ReturnType, (sm, sa) => {
        if (sm.Name == "GetData") return placedSave.GetValueOrDefault((string)sa![0]!);
        if (sm.Name == "StoreData") { if (failStore) throw new IOException("Injected save failure"); placedSave[(string)sa![0]!] = (byte[])sa[1]!; return null; }
        return Default(sm.ReturnType);
    }) : Default(m.ReturnType)),
    "get_World" => Proxy(method.ReturnType, (m, a) => m.Name == "get_BlockAccessor" && deniedResyncProbe ? accessor : m.Name == "get_AllOnlinePlayers" ? Array.CreateInstance(m.ReturnType.GetElementType()!, 0) : m.Name == "PlayerByUid" ? veinMiner : Default(m.ReturnType)),
    _ => Default(method.ReturnType)
});
object virtualizer = Activator.CreateInstance(virtualizerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { api, server, config, palette, null, null }, null)!;
var posType = T("Vintagestory.API.MathTools.BlockPos");
object Position(int x) => Activator.CreateInstance(posType, x, 10, 10, 0)!;
object positionList = Activator.CreateInstance(typeof(List<>).MakeGenericType(posType))!;
((IList)positionList).Add(Position(10)); ((IList)positionList).Add(Position(20));
object discoveries = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), positionList.GetType()))!;
((IDictionary)discoveries).Add("knows-ore", positionList);
if (virtualizerType.GetMethod("ImportDiscoveryState") != null) Call(virtualizer, "ImportDiscoveryState", discoveries);
else {
    var players = (IDictionary)Field(virtualizerType, "playerDiscoveries").GetValue(virtualizer)!;
    Type chunksType = players.GetType().GetGenericArguments()[1];
    var chunks = (IDictionary)Activator.CreateInstance(chunksType)!;
    chunks.Add(Activator.CreateInstance(chunksType.GetGenericArguments()[0], 0, 0, 0)!, new HashSet<int> { buried, exposed });
    players.Add("knows-ore", chunks);
}
object exemptions = Field(virtualizerType, "playerPlacedOreExemptions").GetValue(virtualizer)!;
var keyType = exemptions.GetType().GetGenericArguments()[0];
exemptions.GetType().GetMethod("Add")!.Invoke(exemptions, new[] { Activator.CreateInstance(keyType, 15, 10, 10, 0) });
var packetType = T("Packet_ServerChunk");
object canonical = chunkType.GetMethod("ToPacket", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(chunk, new object[] { 0, 0, 0, false })!;
byte[] original = (byte[])Field(packetType, "Blocks").GetValue(canonical)!;
byte[] originalCopy = original.ToArray();
var unpack = T("Vintagestory.Common.ChunkData").GetMethod("UnpackBlocksTo", BindingFlags.Static | BindingFlags.NonPublic)!;
int[] Decode(object packet) {
    var blocks = new int[32768]; unpack.Invoke(null, new[] { blocks, Field(packetType, "Blocks").GetValue(packet), Field(packetType, "LightSat").GetValue(packet), Field(packetType, "Compver").GetValue(packet) }); return blocks;
}
var serviceType = T("BlockLogger.AntiCheat.OreShieldService");
int serviceArguments = serviceType.GetConstructors().Single().GetParameters().Length;
object service = Activator.CreateInstance(serviceType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { api, config, null, null }.Take(serviceArguments).ToArray(), null)!;
Set(service, "server", server); Set(service, "virtualizer", virtualizer); Set(service, "active", true);
Field(serviceType, "current").SetValue(null, service);
var clientType = T("Vintagestory.Server.ConnectedClient");
object Client(int id, string uid) {
    var result = RuntimeHelpers.GetUninitializedObject(clientType); Set(result, "Id", id); Set(result, "ChunkSent", new HashSet<long>());
    ((IDictionary)Field(serviceType, "clientUidById").GetValue(service)!).Add(id, uid); return result;
}
object known = Client(1, "knows-ore"), unknown = Client(2, "unknown"), another = Client(3, "another");
var harmonyAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "Lib", "0Harmony.dll"));
var harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", true)!;
object harmony = Activator.CreateInstance(harmonyType, "nss.test.ore-packets")!;
Set(service, "harmony", harmony);
var sendType = T("Vintagestory.Server.ServerSystemSendChunks");
try {
    bool legacy = serviceType.GetMethod("PatchRecipientChunkSendPaths", BindingFlags.Instance | BindingFlags.NonPublic) == null;
    if (legacy)
        Call(service, "Patch", sendType, "collectChunk", new[] { chunkType, typeof(int), typeof(int), typeof(int), clientType, typeof(bool) }, "AfterRecipientChunk", false, 0);
    else Call(service, "PatchRecipientChunkSendPaths", sendType);
    object sender = RuntimeHelpers.GetUninitializedObject(sendType); Set(sender, "server", server);
    foreach (string name in new[] { "packetsWithEntities", "packetsWithoutEntities" }) {
        var field = Field(sendType, name); var dictionary = (IDictionary)Activator.CreateInstance(field.FieldType)!; dictionary.Add(0L, canonical); field.SetValue(sender, dictionary);
    }
    var collect = sendType.GetMethod("collectChunk", BindingFlags.Instance | BindingFlags.NonPublic)!;
    object Send(object client) => collect.Invoke(sender, new[] { chunk, 0, 0, 0, client, false })!;
    void Concealed(object packet, string message) { var ids = Decode(packet); if (ids[buried] != 1 || ids[exposed] != 1 || ids[placed] != 2 || ids[exposed + 1] != 0) throw new Exception(message); }
    worldReads = 0;
    var knownPacket = Send(known);
    Console.WriteLine($"Cold packet mask: synchronous world-chunk lookups={worldReads}; payload bytes={originalCopy.Length}.");
    if (Decode(knownPacket)[buried] != 2 || Decode(knownPacket)[exposed] != 2) throw new Exception("Legitimate discoveries were concealed.");
    var unknownPacket = Send(unknown); Concealed(unknownPacket, "A recipient inherited another player's ore discoveries.");
    var serializer = T("Packet_ServerChunkSerializer");
    var wire = (byte[])serializer.GetMethod("SerializeToBytes")!.Invoke(null, new[] { unknownPacket })!;
    object received = Activator.CreateInstance(packetType)!;
    serializer.GetMethod("DeserializeBuffer")!.Invoke(null, new object[] { wire, wire.Length, received });
    Concealed(received, "Wire serialization leaked ore.");
    // Inspect the native wire palette, not only decoded visible cells: stale,
    // unreferenced IDs still disclose the presence of a concealed ore variant.
    var layerType = T("Vintagestory.Common.ChunkDataLayer");
    var wireLayer = Activator.CreateInstance(layerType, pool)!;
    try {
        Call(wireLayer, "Decompress", Field(packetType, "Blocks").GetValue(received)!);
        var wirePalette = (int[])Field(layerType, "palette").GetValue(wireLayer)!;
        int paletteCount = (int)Field(layerType, "paletteCount").GetValue(wireLayer)!;
        if (Decode(received)[hiddenOnlyVariant] != 1 || wirePalette.Take(paletteCount).Contains(3))
            throw new Exception("Concealed-only ore variant survived in the outgoing wire palette.");
        if (!wirePalette.Take(paletteCount).Contains(2)) throw new Exception("Legitimate placed ore disappeared from the wire palette.");
        Console.WriteLine("PASS: outgoing native palette excludes concealed-only ore variants and retains legitimate visible ore.");
    } finally { Call(pool, "FreeArrays", wireLayer); }
    if (legacy) {
        Console.WriteLine("PASS legacy working reference: real patched 1.22.7 collectChunk conceals buried/cave ore for an undiscovered recipient while retaining placed ore. Recipient-isolation assertions apply to the new build only.");
        return;
    }
    if (ReferenceEquals(knownPacket, unknownPacket) || ReferenceEquals(canonical, unknownPacket)) throw new Exception("Shared packet object was reused across recipients.");
    if (!originalCopy.SequenceEqual((byte[])Field(packetType, "Blocks").GetValue(canonical)!) || !ReferenceEquals(original, Field(packetType, "Blocks").GetValue(canonical))) throw new Exception("Canonical engine packet was mutated.");
    var repeat = Send(unknown); Concealed(repeat, "Repeat send leaked ore.");
    Concealed(Send(another), "A second undiscovered recipient leaked ore.");
    if (!ReferenceEquals(Field(packetType, "Blocks").GetValue(unknownPacket), Field(packetType, "Blocks").GetValue(repeat))) throw new Exception("Common masked payload cache was not reused.");
    var hook = serviceType.GetMethod("AfterRecipientChunkDynamic", BindingFlags.Static | BindingFlags.NonPublic)!;
    object?[] wrapperArgs = { new[] { chunk, unknown }, unknownPacket }; hook.Invoke(null, wrapperArgs);
    if (!ReferenceEquals(wrapperArgs[1], unknownPacket)) throw new Exception("Same-recipient nested wrapper was not deduplicated.");
    object?[] otherArgs = { new[] { chunk, another }, knownPacket }; hook.Invoke(null, otherArgs); Concealed(otherArgs[1]!, "A rewrapped discovered packet leaked ore to another recipient.");
    // Reverse order: a private concealed packet must never overwrite a later known view.
    var hiddenFirst = Send(unknown);
    var knownLater = Send(known);
    Concealed(hiddenFirst, "A later known recipient changed an earlier concealed packet.");
    if (Decode(knownLater)[buried] != 2 || Decode(knownLater)[exposed] != 2) throw new Exception("Undiscovered-first send suppressed another player's discoveries.");
    configType.GetProperty("OreShieldChunkCacheEnabled")!.SetValue(config, false);
    Concealed(Send(unknown), "Cache-disabled masking leaked ore.");
    var alternateLayer = Activator.CreateInstance(layerType, pool)!;
    try {
        Call(alternateLayer, "PopulateWithAir");
        int[] alternative = Decode(canonical);
        alternative[hiddenOnlyVariant] = 4;
        for (int i = 0; i < alternative.Length; i++) Call(alternateLayer, "SetUnsafe", i, alternative[i]);
        byte[] alternateBytes = (byte[])layerType.GetMethod("Compress")!.Invoke(null, new object[] { alternateLayer, new int[32768] })!;
        var canonicalWire = (byte[])serializer.GetMethod("SerializeToBytes")!.Invoke(null, new[] { canonical })!;
        object alternatePacket = Activator.CreateInstance(packetType)!;
        serializer.GetMethod("DeserializeBuffer")!.Invoke(null, new object[] { canonicalWire, canonicalWire.Length, alternatePacket });
        Set(alternatePacket, "Blocks", alternateBytes);
        Call(virtualizer, "MaskChunkForPlayerUid", "unknown", chunk, alternatePacket);
        if (!((byte[])Field(packetType, "Blocks").GetValue(received)!).SequenceEqual((byte[])Field(packetType, "Blocks").GetValue(alternatePacket)!))
            throw new Exception("Equivalent visible geology produced different compressed bytes for a concealed-only variant change.");
        Console.WriteLine("PASS: changing a concealed-only mineral with the same host produces identical outgoing block bytes.");
    } finally { Call(pool, "FreeArrays", alternateLayer); }
    if ((long)Field(serviceType, "hookErrors").GetValue(service)! != 0 || errors.Count != 0) throw new Exception("Masking required degraded fallback: " + string.Join(";", errors));
    if(virtualizerType.GetField("revisionEpoch",BindingFlags.Instance|BindingFlags.NonPublic)!=null)
    {
        var revisions=(IDictionary)Field(virtualizerType,"revisions").GetValue(virtualizer)!;
        var chunkKeyType=revisions.GetType().GetGenericArguments()[0];
        object zero=Activator.CreateInstance(chunkKeyType,0,0,0)!;
        long oldRevision=(long)Call(virtualizer,"RevisionNoLock",zero)!;
        for(int i=1;i<=16386;i++)Call(virtualizer,"BumpRevisionNoLock",Activator.CreateInstance(chunkKeyType,i,0,0));
        if(revisions.Count>16384 || (long)Call(virtualizer,"RevisionNoLock",zero)!<=oldRevision || (long)Field(virtualizerType,"revisionCompactions").GetValue(virtualizer)! == 0)throw new Exception("Revision compaction lost its bound or reused an old epoch");
        Concealed(Send(unknown),"Epoch compaction leaked concealed ore");
        if(Decode(Send(known))[exposed]!=2)throw new Exception("Epoch compaction lost discoveries");
        var imported=(IDictionary)Activator.CreateInstance(discoveries.GetType())!;
        for(int i=0;i<1000;i++) {var positions=(IList)Activator.CreateInstance(positionList.GetType())!;positions.Add(Position(1000+i*32));imported.Add("offline-"+i,positions);}
        Call(virtualizer,"ImportDiscoveryState",imported);
        long visited=(long)Field(virtualizerType,"discoveryInvalidationOwnersVisited").GetValue(virtualizer)!;
        setBlock.Invoke(data,new object[]{buried,1});Call(virtualizer,"InvalidateAround",Position(10));
        if((long)Field(virtualizerType,"discoveryInvalidationOwnersVisited").GetValue(virtualizer)!-visited!=1)throw new Exception("Mining scanned unrelated retained players");
        Call(virtualizer,"RemovePlayerConnectionState","knows-ore");
        if(Decode(Send(known))[exposed]!=2)throw new Exception("Reconnect cleanup lost a valid discovery");
        Call(virtualizer,"RemovePlayerState","knows-ore");
        if((long)Field(virtualizerType,"discoveryChunkRecords").GetValue(virtualizer)! !=1000L)throw new Exception("Discovery owner records leaked after explicit cleanup");
        Console.WriteLine("PASS: 16,386 changed chunks trigger bounded monotonic revision epochs; mining visits one affected owner among 1,001 retained players; reconnect discoveries and explicit index cleanup survive.");
    }
    if (serviceType.GetMethod("BeforeNativeBreak", BindingFlags.Static | BindingFlags.NonPublic) != null)
    {
        Set(service, "palette", palette); Set(service, "server", server);
        var nativeBreak = T("Vintagestory.Common.BlockAccessorBase").GetMethod("BreakBlock", new[] { posType, T("Vintagestory.API.Common.IPlayer"), typeof(float) })!;
        var hmType = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod", true)!;
        object NativeHook(string name) => Activator.CreateInstance(hmType, serviceType.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!)!;
        Type strictAccessor = T("Vintagestory.Common.BlockAccessorStrict");
        var inheritedBreak = strictAccessor.GetMethod("BreakBlock", new[] { posType, T("Vintagestory.API.Common.IPlayer"), typeof(float) })!;
        if (inheritedBreak.ReflectedType == inheritedBreak.DeclaringType) throw new Exception("Missing inherited-method regression fixture");
        bool rejectedInherited = false;
        try { harmonyType.GetMethod("Patch")!.Invoke(harmony, new object?[] { inheritedBreak, NativeHook("BeforeNativeBreak"), null, null, NativeHook("AfterNativeBreak") }); }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException && ex.InnerException.Message.Contains("declared method")) { rejectedInherited = true; }
        if (!rejectedInherited) throw new Exception("Reported inherited BreakBlock rejection not reproduced");
        Call(service, "PatchNativeBreak", strictAccessor);
        Console.WriteLine("PASS: inherited native accessor reproduces the reported Harmony rejection; production startup hook resolves and patches the declared implementation.");
        Type nativePlayerType = T("Vintagestory.Server.ServerPlayer");
        object miner = RuntimeHelpers.GetUninitializedObject(nativePlayerType);
        var worldDataField = Field(nativePlayerType, "worlddata");
        object playerData = RuntimeHelpers.GetUninitializedObject(worldDataField.FieldType);
        Set(playerData, "PlayerUID", "modded-miner"); worldDataField.SetValue(miner, playerData);
        veinMiner = miner;
        object target = Position(10);
        setBlock.Invoke(data, new object[] { buried, 2 });
        var prefix = serviceType.GetMethod("BeforeNativeBreak", BindingFlags.Static | BindingFlags.NonPublic)!;
        var postfix = serviceType.GetMethod("AfterNativeBreak", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] stateArgs = { target, miner, null }; prefix.Invoke(null, stateArgs);
        if (stateArgs[2] == null) throw new Exception("Native mod break was not captured: " + string.Join(";", errors));
        long pulses = (long)Field(virtualizerType, "miningRevealPulses").GetValue(virtualizer)!;
        postfix.Invoke(null, new object?[] { stateArgs[2], null });
        if ((long)Field(virtualizerType, "miningRevealPulses").GetValue(virtualizer)! != pulses) throw new Exception("Rejected/unchanged mod break authorized discovery");
        setBlock.Invoke(data, new object[] { buried, 0 });
        var originalException = new InvalidOperationException("native neighbor update failure");
        if (!ReferenceEquals(postfix.Invoke(null, new object?[] { stateArgs[2], originalException }), originalException)) throw new Exception("OreShield swallowed a native/mod exception");
        if ((long)Field(virtualizerType, "miningRevealPulses").GetValue(virtualizer)! != pulses + 1 || queuedSync.Count != 1) throw new Exception("Direct API ore break did not queue mined-cell cancellation");
        postfix.Invoke(null, new object?[] { stateArgs[2], null });
        if ((long)Field(virtualizerType, "miningRevealPulses").GetValue(virtualizer)! != pulses + 1) throw new Exception("Nested/public post-break delivery duplicated native sync");
        for (int i = 0; i < 100; i++) Call(virtualizer, "SyncAuthoritativeWorldCellToAllClients", Position(22));
        if (queuedSync.Count != 1) throw new Exception("Ore cancellation burst scheduled one callback per cell");
        queuedSync[0](); queuedSync.Clear();
        if ((int)Field(virtualizerType, "pendingAuthoritativeCells").GetValue(virtualizer)!.GetType().GetProperty("Count")!.GetValue(Field(virtualizerType, "pendingAuthoritativeCells").GetValue(virtualizer))! != 0) throw new Exception("Mined-cell correction batch did not drain");
        Console.WriteLine("PASS: native 1.22.7 BreakBlock Harmony hooks compile; direct mod ore changes queue cancellation exactly once; unchanged/rejected breaks do not authorize discovery; 100 same-cell corrections use one callback.");
    }
    if (virtualizerType.GetMethod("ExportCompactDiscoveryState", BindingFlags.Instance | BindingFlags.NonPublic) != null)
    {
        object compact = Call(virtualizer, "ExportCompactDiscoveryState")!;
        object restored = Activator.CreateInstance(virtualizerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { api, server, config, palette, null, null }, null)!;
        Call(restored, "ImportCompactDiscoveryState", compact);
        long originalCount = (long)Field(virtualizerType, "discoveryCells").GetValue(virtualizer)!;
        long restoredCount = (long)Field(virtualizerType, "discoveryCells").GetValue(restored)!;
        if (originalCount != restoredCount) throw new Exception("Compact reload lost discoveries");
        Call(restored, "RemovePlayerState", "offline-0");
        if ((long)Field(virtualizerType, "discoveryCells").GetValue(virtualizer)! != originalCount) throw new Exception("Compact reload shared mutable discovery records");
        object newPlaced = Position(18), otherPlaced = Activator.CreateInstance(posType, 19, 10, 10, 2)!;
        Call(virtualizer, "MarkPlayerPlacedOreExemption", newPlaced, true);
        Call(virtualizer, "MarkPlayerPlacedOreExemption", otherPlaced, true);
        if (placedSave.Count != 0) throw new Exception("Placement serialized whole exemption state synchronously");
        failStore = true; Call(virtualizer, "FlushPlacedOreCheckpoints", 2); failStore = false;
        if ((int)Field(virtualizerType, "dirtyPlacedOreChunks").GetValue(virtualizer)!.GetType().GetProperty("Count")!.GetValue(Field(virtualizerType, "dirtyPlacedOreChunks").GetValue(virtualizer))! != 2) throw new Exception("Failed checkpoint discarded dirty exemptions");
        Call(virtualizer, "FlushPlacedOreOnWorldSave");
        object reload = Activator.CreateInstance(virtualizerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { api, server, config, palette, null, null }, null)!;
        if (!(bool)Call(reload, "IsPlayerPlacedOre", newPlaced)! || !(bool)Call(reload, "IsPlayerPlacedOre", otherPlaced)!) throw new Exception("Placed-ore shard reload lost dimension/position");
        Call(reload, "RemovePlayerPlacedOreExemption", newPlaced, true); Call(reload, "FlushPlacedOreOnWorldSave");
        object afterRemove = Activator.CreateInstance(virtualizerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { api, server, config, palette, null, null }, null)!;
        if ((bool)Call(afterRemove, "IsPlayerPlacedOre", newPlaced)! || !(bool)Call(afterRemove, "IsPlayerPlacedOre", otherPlaced)!) throw new Exception("Checkpoint removal/reload incorrect");
        // Change coalescing cannot grow with repeated callbacks for the same cell.
        for (int i = 0; i < 1000; i++) Call(reload, "QueueWorldChange", newPlaced, true);
        var pending = (IDictionary)Field(virtualizerType, "pendingWorldChanges").GetValue(reload)!;
        if (pending.Count != 1) throw new Exception("OreShield failed to coalesce repeated changes");
        for (int i = 0; i < 5000; i++) Call(reload, "QueueWorldChange", Position(i * 32), false);
        if (pending.Count != 4096) throw new Exception("OreShield world-change queue exceeded bound");
        Console.WriteLine("PASS: compact discovery reload owns its records; placed ore persists per chunk/dimension, retry retains dirty state, removal round-trips; 6,000 geometry events coalesce within a 4,096-cell queue.");
        // Legacy v1 saves migrate without overwriting the original data.
        placedSave.Clear();
        using (var buffer = new MemoryStream()) {
            using var writer = new BinaryWriter(buffer); writer.Write(1); writer.Write(1); writer.Write(21); writer.Write(10); writer.Write(10); writer.Write(3); writer.Flush();
            placedSave["newfies:oreshield-playerplaced-v1"] = buffer.ToArray();
        }
        byte[] legacyCopy = placedSave.Values.Single().ToArray();
        object legacyReload = Activator.CreateInstance(virtualizerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { api, server, config, palette, null, null }, null)!;
        object legacyPos = Activator.CreateInstance(posType, 21, 10, 10, 3)!;
        if (!(bool)Call(legacyReload, "IsPlayerPlacedOre", legacyPos)!) throw new Exception("Legacy placed ore not loaded");
        Call(legacyReload, "FlushPlacedOreOnWorldSave");
        if (!placedSave["newfies:oreshield-playerplaced-v1"].SequenceEqual(legacyCopy)) throw new Exception("Legacy save overwritten during migration");
        object migratedReload = Activator.CreateInstance(virtualizerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { api, server, config, palette, null, null }, null)!;
        if (!(bool)Call(migratedReload, "IsPlayerPlacedOre", legacyPos)!) throw new Exception("Migrated placed ore lost");
        Console.WriteLine("PASS: legacy placed-ore migration preserves the original save and round-trips through v2 shards.");
    }
    if (virtualizerType.GetMethod("RevealConnectedVeinAfterMine", BindingFlags.Instance | BindingFlags.NonPublic) != null)
    {
        configType.GetProperty("OreShieldRevealWholeVeinOnMine")!.SetValue(config, true);
        configType.GetProperty("OreShieldMaxVeinRevealBlocks")!.SetValue(config, 64);
        var buildFamily = paletteType.GetMethod("BuildVeinFamily", BindingFlags.Static | BindingFlags.NonPublic)!;
        string Family(string grade, string rock) {
            object b = Activator.CreateInstance(T("Vintagestory.API.Common.Block"))!;
            Set(b, "Code", Activator.CreateInstance(T("Vintagestory.API.Common.AssetLocation"), "game:ore-" + grade + "-cassiterite-" + rock));
            Type variantsType = Field(b.GetType(), "Variant").FieldType;
            object variants = Activator.CreateInstance(variantsType, new Dictionary<string,string> { ["grade"] = grade })!;
            Set(b, "Variant", variants);
            return (string)buildFamily.Invoke(null, new object[] { b, rock })!;
        }
        string poor = Family("poor", "granite"), rich = Family("rich", "basalt");
        if (poor != rich) throw new Exception("Ore grades/host boundaries split one mineral family");
        Set(palette, "<VeinFamilyByBlock>k__BackingField", new string?[] { null, null, poor, rich, "game:ore-hematite" });
        for(int i=0;i<32768;i++)setBlock.Invoke(data,new object[]{i,1});
        setBlock.Invoke(data,new object[]{Index(10,10,10),0});
        for(int x=11;x<=15;x++)for(int y=11;y<=15;y++)for(int z=11;z<=15;z++)setBlock.Invoke(data,new object[]{Index(x,y,z),(x+y+z)%2==0?2:3});
        setBlock.Invoke(data,new object[]{Index(16,15,15),4});
        object vein = Activator.CreateInstance(virtualizerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { api, server, config, palette, null, null }, null)!;
        int beforeSeedingReads = worldReads;
        Call(vein,"RevealConnectedVeinAfterMine",veinMiner,Position(10),2);
        if(worldReads!=beforeSeedingReads)throw new Exception("Mining synchronously read the vein neighborhood");
        Call(vein,"RevealAfterPlayerBreak",veinMiner,Position(10),2);
        var jobs=(IDictionary)Field(virtualizerType,"pendingVeins").GetValue(vein)!;
        if(jobs.Count!=1)throw new Exception("Diagonal-only vein was not queued from the mined ore");
        for(int i=0;i<100 && jobs.Count>0;i++) { Set(vein,"visibilityStarted",System.Diagnostics.Stopwatch.GetTimestamp());Call(vein,"ProcessVeins"); }
        if(jobs.Count!=0 || (long)Field(virtualizerType,"discoveryCells").GetValue(vein)! !=64)throw new Exception("Bounded mixed-grade vein did not reveal exactly its 64-cell limit");
        var records=(IDictionary)Field(virtualizerType,"playerDiscoveries").GetValue(vein)!;
        if(records.Count!=1 || !records.Contains("modded-miner"))throw new Exception("Vein discovery leaked to another recipient");
        configType.GetProperty("OreShieldMaxVeinRevealBlocks")!.SetValue(config,128);
        object full = Activator.CreateInstance(virtualizerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { api, server, config, palette, null, null }, null)!;
        Call(full,"RevealAfterPlayerBreak",veinMiner,Position(10),2);
        var fullJobs=(IDictionary)Field(virtualizerType,"pendingVeins").GetValue(full)!;
        for(int i=0;i<100 && fullJobs.Count>0;i++) { Set(full,"visibilityStarted",System.Diagnostics.Stopwatch.GetTimestamp());Call(full,"ProcessVeins"); }
        if(fullJobs.Count!=0 || (long)Field(virtualizerType,"discoveryCells").GetValue(full)! !=125)throw new Exception("Whole vein omitted connected grades or crossed into a different mineral");
        object fair = Activator.CreateInstance(virtualizerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { api, server, config, palette, null, null }, null)!;
        Call(fair,"RevealConnectedVeinAfterMine",veinMiner,Position(10),2);
        var fairJobs=(IDictionary)Field(virtualizerType,"pendingVeins").GetValue(fair)!;
        object second=Activator.CreateInstance(fairJobs["modded-miner"]!.GetType(),true)!;
        fairJobs.Add("second-miner",second);
        Set(fairJobs["modded-miner"]!,"LastServed",5L);Set(second,"LastServed",0L);
        Set(fair,"visibilityStarted",System.Diagnostics.Stopwatch.GetTimestamp());Call(fair,"ProcessVeins");
        if(fairJobs.Contains("second-miner")||!fairJobs.Contains("modded-miner"))throw new Exception("A newly waiting reveal job starved behind an already served vein");
        Console.WriteLine("PASS: whole-vein jobs select the least recently served miner rather than repeatedly taking the first dictionary slot.");
        Call(vein,"RevealAfterPlayerBreak",veinMiner,Position(10),1);
        if(jobs.Count!=0)throw new Exception("Mining host rock authorized an entire deposit");
        Call(vein,"RevealAfterPlayerBreak",veinMiner,Position(10),2);
        configType.GetProperty("OreShieldRevealWholeVeinOnMine")!.SetValue(config,false);
        Set(vein,"visibilityStarted",System.Diagnostics.Stopwatch.GetTimestamp());Call(vein,"ProcessVeins");
        if(jobs.Count!=0)throw new Exception("Disabled vein reveal retained queued work");
        Console.WriteLine("PASS: diagonal-only mixed-grade vein is discovered for the miner, bounded at 64 cells; all 125 cells reveal at a higher cap without revealing adjacent hematite; host-rock mining does not reveal deposits and disabling clears queued jobs.");
    }

    // Denied-action sync must not expose ore membership through different transports.
    var channelField=Field(virtualizerType,"oreShieldSyncChannel");var supportField=Field(virtualizerType,"supportsClientSync");
    var originalChannel=channelField.GetValue(virtualizer);var originalSupport=supportField.GetValue(virtualizer);
    var clientsField=Field(serverType,"Clients");var originalClients=clientsField.GetValue(server);
    try {
        deniedResyncProbe=true;
        channelField.SetValue(virtualizer,Proxy(channelField.FieldType,(m,a)=>Default(m.ReturnType)));
        var supportParameter=System.Linq.Expressions.Expression.Parameter(supportField.FieldType.GetGenericArguments()[0]);
        supportField.SetValue(virtualizer,System.Linq.Expressions.Expression.Lambda(supportField.FieldType,System.Linq.Expressions.Expression.Constant(true),supportParameter).Compile());
        object syncPlayer=RuntimeHelpers.GetUninitializedObject(T("Vintagestory.Server.ServerPlayer"));
        var syncDataField=Field(syncPlayer.GetType(),"worlddata");object syncData=RuntimeHelpers.GetUninitializedObject(syncDataField.FieldType);
        Set(syncData,"PlayerUID","uniform-denial");syncDataField.SetValue(syncPlayer,syncData);Set(syncPlayer,"client",unknown);
        var clients=(IDictionary)Activator.CreateInstance(clientsField.FieldType)!;clients.Add(2,unknown);clientsField.SetValue(server,clients);
        ((HashSet<long>)Field(clientType,"ChunkSent").GetValue(unknown)!).Add(0);
        Call(virtualizer,"ResyncDeniedAction",syncPlayer,Position(10));
        Call(virtualizer,"ResyncDeniedAction",syncPlayer,Position(11));
        var outbox=(IDictionary)Field(virtualizerType,"clientSyncOutbox").GetValue(virtualizer)!;
        var groups=outbox.Keys.Cast<object>().Where(k=>(string)k.GetType().GetProperty("PlayerUid")!.GetValue(k)! =="uniform-denial").ToList();
        if(groups.Count!=1 || (bool)groups[0].GetType().GetProperty("Stabilize")!.GetValue(groups[0])!)throw new Exception("Denied cells did not share one ordinary recipient sync transport");
        var queuedCells=(IDictionary)Field(outbox[groups[0]]!.GetType(),"Positions").GetValue(outbox[groups[0]]!)!;
        if(queuedCells.Count!=2)throw new Exception("Ordinary and ore cells did not both queue for uniform resynchronization");
        Console.WriteLine("PASS: denied ordinary/ore cells share one recipient-masked world-update transport; no raw ore fallback or block-type packet distinction.");
    } finally {deniedResyncProbe=false;channelField.SetValue(virtualizer,originalChannel);supportField.SetValue(virtualizer,originalSupport);clientsField.SetValue(server,originalClients);}
    Console.WriteLine("PASS: real patched 1.22.7 collectChunk, compressed buried/cave ore concealed per recipient, legitimate discoveries/placed ore retained, canonical payload immutable, shared mask cache reused, repeated sends and nested/cross-recipient wrappers isolated. No server/world started.");
} finally {
    Field(serviceType, "current").SetValue(null, null);
    harmonyType.GetMethod("UnpatchAll", new[] { typeof(string) })!.Invoke(harmony, new object[] { "nss.test.ore-packets" });
}
public class ApiProxy : DispatchProxy {
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
}
