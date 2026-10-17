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
var library = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryLib.dll"));
var harmonyAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "Lib", "0Harmony.dll"));
var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);
var guardType = mod.GetType("BlockLogger.AntiCheat.PacketIngressGuard", throwOnError: true)!;
var harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", throwOnError: true)!;
object harmony = Activator.CreateInstance(harmonyType, "newfies.test.ingress-smoke")!;
object guard = RuntimeHelpers.GetUninitializedObject(guardType);
guardType.GetField("harmony", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(guard, harmony);
guardType.GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(guard,
    Activator.CreateInstance(mod.GetType("BlockLogger.AntiCheat.AntiCheatConfig", throwOnError: true)!));
MethodInfo patch = guardType.GetMethod("Patch", BindingFlags.Instance | BindingFlags.NonPublic)!;
Type T(string name) => library.GetType(name, throwOnError: true)!;
var accessTools = harmonyAssembly.GetType("HarmonyLib.AccessTools", throwOnError: true)!;
var fieldRef = accessTools.GetMethods().Single(m => m.Name == "FieldRefAccess" && m.IsGenericMethodDefinition &&
    m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
foreach (var field in new[] { ("client", "Vintagestory.Server.ConnectedClient"), ("packet", "Packet_Client") })
{
    object? accessor = fieldRef.MakeGenericMethod(T("Vintagestory.Server.ReceivedClientPacket"), T(field.Item2)).Invoke(null, new object[] { field.Item1 });
    if (accessor == null) throw new Exception("Missing field accessor " + field.Item1);
    Console.WriteLine("PASS field accessor: ReceivedClientPacket." + field.Item1);
}
object lengthAccessor = fieldRef.MakeGenericMethod(T("CitoMemoryStream"), typeof(int)).Invoke(null, new object[] { "bufferlength" })!;
guardType.GetField("inputLength", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, lengthAccessor);
Console.WriteLine("PASS field accessor: CitoMemoryStream.bufferlength");
try
{
    var harmonyMethodType = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod", throwOnError: true)!;
    MethodInfo parser = T("Vintagestory.Server.ServerMain").GetMethod("ParseClientPacket_offthread", BindingFlags.Instance | BindingFlags.NonPublic,
        new[] { T("Vintagestory.Server.ConnectedClient"), typeof(byte[]), typeof(int) })!;
    MethodInfo patchMethod = harmonyType.GetMethods().Single(m => m.Name == "Patch");
    var patchArgs = patchMethod.GetParameters().Select(p => p.Name switch
    {
        "original" => (object)parser,
        "prefix" => Activator.CreateInstance(harmonyMethodType, guardType.GetMethod("BeforeParse", BindingFlags.Static | BindingFlags.NonPublic)!),
        "finalizer" => Activator.CreateInstance(harmonyMethodType, guardType.GetMethod("FinishParse", BindingFlags.Static | BindingFlags.NonPublic)!),
        _ => null
    }).ToArray();
    patchMethod.Invoke(harmony, patchArgs);
    Console.WriteLine("PASS Harmony prefix+finalizer: ServerMain.ParseClientPacket_offthread");
    MethodInfo udpParser = T("Packet_UdpPacketSerializer").GetMethod("DeserializeBuffer",
        new[] { typeof(byte[]), typeof(int), T("Packet_UdpPacket") })!;
    var udpPatchArgs = patchMethod.GetParameters().Select(p => p.Name switch
    {
        "original" => (object)udpParser,
        "prefix" => Activator.CreateInstance(harmonyMethodType, guardType.GetMethod("BeforeUdpParse", BindingFlags.Static | BindingFlags.NonPublic)!),
        "finalizer" => Activator.CreateInstance(harmonyMethodType, guardType.GetMethod("FinishUdpParse", BindingFlags.Static | BindingFlags.NonPublic)!),
        _ => null
    }).ToArray();
    patchMethod.Invoke(harmony, udpPatchArgs);
    Console.WriteLine("PASS Harmony prefix+finalizer: Packet_UdpPacketSerializer.DeserializeBuffer");
    var targets = new[]
    {
        (T("ProtocolParser"), "ReadBytes", new[] { T("CitoStream") }, "BeforeReadBytes"),
        (T("Vintagestory.Server.ServerMain"), "HandleClientPacket_mainthread", new[] { T("Vintagestory.Server.ReceivedClientPacket") }, "BeforeDispatch"),
        (T("Vintagestory.Server.Systems.ServerUdpNetwork"), "HandlePlayerPosition", new[] { T("Packet_EntityPosition"), T("Vintagestory.Server.ServerPlayer") }, "BeforePlayerPosition")
    };
    foreach (var target in targets)
    {
        patch.Invoke(guard, new object[] { target.Item1, target.Item2, target.Item3, target.Item4 });
        Console.WriteLine("PASS Harmony prefix: " + target.Item1.Name + "." + target.Item2);
    }
    // Use small inputs only: the exception's guard-specific text proves impossible
    // declarations are rejected before vanilla ReadBytes can allocate them.
    guardType.GetField("current", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, guard);
    guardType.GetField("parsingLimit", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, 8192);
    MethodInfo readBytes = T("ProtocolParser").GetMethod("ReadBytes", new[] { T("CitoStream") })!;
    object Stream(byte[] bytes) => Activator.CreateInstance(T("CitoMemoryStream"), bytes, bytes.Length)!;
    var decoded = (byte[])readBytes.Invoke(null, new[] { Stream(new byte[] { 3, 10, 20, 30 }) })!;
    if (!decoded.SequenceEqual(new byte[] { 10, 20, 30 })) throw new Exception("Valid byte-array decode changed");
    try
    {
        // Declared 1000 bytes with no body. Deliberately small even if the hook failed.
        readBytes.Invoke(null, new[] { Stream(new byte[] { 0xe8, 0x07 }) });
        throw new Exception("Impossible declaration was accepted");
    }
    catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException invalid && invalid.Message == "Invalid client byte-array length.") { }
    Console.WriteLine("PASS allocation guard: valid body preserved, impossible small length rejected by prefix");
    FieldInfo parsingLimit = guardType.GetField("parsingLimit", BindingFlags.Static | BindingFlags.NonPublic)!;
    parsingLimit.SetValue(null, 0);
    // Same serializer used by UdpNetServer before endpoint authentication. These
    // tiny frames contain one custom-channel Data field, with no game instances.
    byte[] validUdp = { 8, 6, 34, 5, 26, 3, 10, 20, 30 };
    object packet = Activator.CreateInstance(T("Packet_UdpPacket"))!;
    udpParser.Invoke(null, new object[] { validUdp, validUdp.Length, packet });
    object channel = T("Packet_UdpPacket").GetField("ChannelPacket")!.GetValue(packet)!;
    var udpData = (byte[])T("Packet_CustomPacket").GetField("Data")!.GetValue(channel)!;
    if (!udpData.SequenceEqual(new byte[] { 10, 20, 30 }) || (int)parsingLimit.GetValue(null)! != 0)
        throw new Exception("Valid UDP parse or context cleanup failed");
    try
    {
        byte[] invalidUdp = { 8, 6, 34, 3, 26, 0xe8, 0x07 }; // Declared 1000, body absent.
        udpParser.Invoke(null, new object[] { invalidUdp, invalidUdp.Length, Activator.CreateInstance(T("Packet_UdpPacket"))! });
        throw new Exception("Impossible UDP byte-array declaration accepted");
    }
    catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException invalid && invalid.Message == "Invalid client byte-array length.") { }
    if ((int)parsingLimit.GetValue(null)! != 0) throw new Exception("UDP exception leaked parsing context");
    Console.WriteLine("PASS actual UDP serializer: valid frame preserved, truncated declaration rejected, parsing context restored");
    try
    {
        byte[] missingBody = { 8, 6 };
        udpParser.Invoke(null, new object[] { missingBody, missingBody.Length, Activator.CreateInstance(T("Packet_UdpPacket"))! });
        throw new Exception("Missing UDP custom-channel body accepted");
    }
    catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException invalid && invalid.Message == "Invalid client UDP packet shape.") { }
    if ((int)parsingLimit.GetValue(null)! != 0) throw new Exception("UDP shape rejection leaked parsing context");
    Console.WriteLine("PASS actual UDP serializer: missing custom-channel envelope rejected and context restored");
}
finally
{
    guardType.GetField("current", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
    guardType.GetField("parsingLimit", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, 0);
    harmonyType.GetMethod("UnpatchAll", new[] { typeof(string) })!.Invoke(harmony, new object[] { "newfies.test.ingress-smoke" });
}
Console.WriteLine("PASS: verified runtime field refs and patched/unpatched ingress methods; no game instance created.");
