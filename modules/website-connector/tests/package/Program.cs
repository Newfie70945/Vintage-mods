using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY."));
string archive = Path.GetFullPath(args.FirstOrDefault() ?? throw new Exception("Pass the Suite release ZIP."));
string temporary = Path.Combine(Path.GetTempPath(), "nss-package-smoke-" + Guid.NewGuid().ToString("N"));
string? extracted = null;
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (string? root in new[] { extracted, game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") }) {
        if (root == null) continue;
        string file = Path.Combine(root, name.Name + ".dll");
        if (File.Exists(file)) return context.LoadFromAssemblyPath(file);
    }
    return null;
};
var library = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryLib.dll"));
var containerType = library.GetType("Vintagestory.Common.ModContainer", true)!;
var loggerType = containerType.GetConstructors().Single().GetParameters()[1].ParameterType;
var errors = new List<string>();
object logger = DispatchProxy.Create(loggerType, typeof(LogProxy));
((LogProxy)logger).Errors = errors;
object Unpack(string zip, string folder) {
    errors.Clear();
    object mod = Activator.CreateInstance(containerType, new FileInfo(zip), logger, false)!;
    containerType.GetMethod("Unpack")!.Invoke(mod, new object[] { folder });
    return mod;
}
try {
    object accepted = Unpack(archive, Path.Combine(temporary, "fixed"));
    if (containerType.GetProperty("Error")!.GetValue(accepted) != null || errors.Count != 0)
        throw new Exception("Official mod loader rejected fixed ZIP: " + string.Join("; ", errors));
    extracted = (string)containerType.GetProperty("FolderPath")!.GetValue(accepted)!;
    if (Directory.Exists(Path.Combine(extracted, "runtimes"))) throw new Exception("SDK runtime tree leaked outside native/.");
    var assemblies = ((IEnumerable)containerType.GetProperty("AssemblyFiles")!.GetValue(accepted)!).Cast<string>().ToList();
    if (!assemblies.Any(p => Path.GetFileName(p) == "NewfiesWebsiteConnector.dll") || assemblies.Any(p => Path.GetDirectoryName(p) != extracted))
        throw new Exception("Managed assemblies not recognized at mod root.");
    Console.WriteLine("PASS: official ModContainer accepts release ZIP and recognizes root managed assemblies.");
    var loaderType = library.GetType("Vintagestory.Common.ModAssemblyLoader", true)!;
    var mods = Array.CreateInstance(containerType, 1);
    mods.SetValue(accepted, 0);
    using var loader = (IDisposable)Activator.CreateInstance(loaderType, new[] { extracted }, mods)!;
    containerType.GetMethod("LoadModInfo")!.Invoke(accepted, new object?[] { null, loader });
    containerType.GetMethod("LoadAssembly")!.Invoke(accepted, new object?[] { null, loader });
    var suite = (Assembly?)containerType.GetProperty("Assembly")!.GetValue(accepted);
    if (suite?.GetName().Name != "NewfiesWebsiteConnector" || errors.Count != 0)
        throw new Exception("Official loader failed Website Connector metadata/assembly loading: " + string.Join("; ", errors));
    Console.WriteLine("PASS: official ModContainer/ModAssemblyLoader load Website Connector metadata and managed assembly.");
    var systemType = suite!.GetType("Newfies.WebsiteConnector.WebsiteConnectorModSystem", true)!;
    if (suite.GetName().Version?.ToString(3) != "1.1.1") throw new Exception("Connector assembly version was not bumped.");
    var apiAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryAPI.dll"));
    var gameVersionType = apiAssembly.GetType("Vintagestory.API.Config.GameVersion", true)!;
    var expectedVersion = (string)gameVersionType.GetField("ShortGameVersion")!.GetValue(null)!;
    var actualVersion = (string)suite.GetType("Newfies.WebsiteConnector.RuntimeGameVersion", true)!.GetMethod("Read")!.Invoke(null, new object[] { gameVersionType })!;
    if (actualVersion != expectedVersion) throw new Exception("Packaged connector does not read the loaded game's version.");
    Console.WriteLine("PASS: connector 1.1.1 reads the loaded game API version (" + actualVersion + ").");
    var system = Activator.CreateInstance(systemType)!;
    var shouldLoad = systemType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Single(m => m.Name == "ShouldLoad");
    var sideType = shouldLoad.GetParameters()[0].ParameterType;
    if ((bool)shouldLoad.Invoke(system, new[] {Enum.Parse(sideType, "Client")})! ||
        !(bool)shouldLoad.Invoke(system, new[] {Enum.Parse(sideType, "Server")})!)
        throw new Exception("Companion was not server-only.");
    Console.WriteLine("PASS: server-only ModSystem instantiation, Client=False and Server=True.");
} finally {
    // Windows retains open native DLLs until process exit; permit OS cleanup there.
    try { Directory.Delete(temporary, true); } catch (IOException) { }
}
public class LogProxy : DispatchProxy {
    public List<string> Errors = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? arguments) {
        if (method?.Name == "Error" || (method?.Name == "Log" && arguments?.FirstOrDefault()?.ToString() == "Error"))
            Errors.Add(string.Join(" ", arguments?.Select(x => x?.ToString()) ?? Array.Empty<string>()));
        return method?.ReturnType == typeof(bool) ? false : null;
    }
}
