using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY."));
string archive = Path.GetFullPath(args.FirstOrDefault() ?? throw new Exception("Pass the Suite release ZIP."));
string expectedAssembly = args.FirstOrDefault(arg => arg.StartsWith("--assembly="))?.Substring("--assembly=".Length) ?? "NewfiesServerEssentialsSuite";
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
if (args.Contains("--game-sqlite-first")) {
    foreach (string name in new[] { "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll", "SQLitePCLRaw.batteries_v2.dll", "Microsoft.Data.Sqlite.dll" }) {
        var shared = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "Lib", name));
        Console.WriteLine("Preloaded game dependency: " + shared.FullName);
    }
}
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
    string? legacyArchive = args.Skip(1).FirstOrDefault(arg => !arg.StartsWith("--"));
    if (legacyArchive != null) {
        object rejected = Unpack(Path.GetFullPath(legacyArchive), Path.Combine(temporary, "old"));
        if (containerType.GetProperty("Error")!.GetValue(rejected) == null || !errors.Any(x => x.Contains("root folder")))
            throw new Exception("Old NuGet layout did not reproduce the reported rejection.");
        Console.WriteLine("PASS: official ModContainer reproduces rejection of old runtimes/ DLL layout.");
    }
    object accepted = Unpack(archive, Path.Combine(temporary, "fixed"));
    if (containerType.GetProperty("Error")!.GetValue(accepted) != null || errors.Count != 0)
        throw new Exception("Official mod loader rejected fixed ZIP: " + string.Join("; ", errors));
    extracted = (string)containerType.GetProperty("FolderPath")!.GetValue(accepted)!;
    if (Directory.Exists(Path.Combine(extracted, "runtimes"))) throw new Exception("SDK runtime tree leaked outside native/.");
    var assemblies = ((IEnumerable)containerType.GetProperty("AssemblyFiles")!.GetValue(accepted)!).Cast<string>().ToList();
    if (!assemblies.Any(p => Path.GetFileName(p) == expectedAssembly + ".dll") || assemblies.Any(p => Path.GetDirectoryName(p) != extracted))
        throw new Exception("Managed assemblies not recognized at mod root.");
    Console.WriteLine("PASS: official ModContainer accepts release ZIP and recognizes root managed assemblies.");
    var loaderType = library.GetType("Vintagestory.Common.ModAssemblyLoader", true)!;
    var mods = Array.CreateInstance(containerType, 1);
    mods.SetValue(accepted, 0);
    using var loader = (IDisposable)Activator.CreateInstance(loaderType, new[] { extracted }, mods)!;
    containerType.GetMethod("LoadModInfo")!.Invoke(accepted, new object?[] { null, loader });
    containerType.GetMethod("LoadAssembly")!.Invoke(accepted, new object?[] { null, loader });
    var suite = (Assembly?)containerType.GetProperty("Assembly")!.GetValue(accepted);
    if (args.Contains("--expect-shared-sqlite-failure")) {
        if (suite != null || !errors.Any(error => error.Contains("Microsoft.Data.Sqlite") && (error.Contains("manifest definition does not match", StringComparison.OrdinalIgnoreCase) || error.Contains("same name is already loaded", StringComparison.OrdinalIgnoreCase))))
            throw new Exception("Expected shared-dependency manifest mismatch was not reproduced: " + string.Join("; ", errors));
        Console.WriteLine("PASS: game-preloaded SQLite reproduces the shared assembly identity conflict.");
        Console.WriteLine(string.Join("; ", errors));
        return;
    }
    if (suite?.GetName().Name != expectedAssembly || errors.Count != 0)
        throw new Exception("Official loader failed Suite metadata/assembly loading: " + string.Join("; ", errors));
    Console.WriteLine("PASS: official ModContainer/ModAssemblyLoader load " + expectedAssembly + " metadata and managed assembly.");
    string metadataVersion = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(extracted, "modinfo.json"))).RootElement.GetProperty("version").GetString()!;
    if (expectedAssembly != "NewfiesServerEssentialsSuite") {
        if (suite.GetName().Version?.ToString(3) != metadataVersion && !args.Contains("--allow-stable-assembly-version"))
            throw new Exception("Companion assembly and metadata versions do not match.");
        Console.WriteLine("PASS: official loader loads " + expectedAssembly + " " + metadataVersion + "; assembly version " + suite.GetName().Version + ".");
        return;
    }
    var releaseType = suite.GetType("BlockLogger.SuiteRelease", true)!;
    string runtimeVersion = (string)releaseType.GetField("Version")!.GetRawConstantValue()!;
    string runtimeBuild = (string)releaseType.GetField("BuildId")!.GetRawConstantValue()!;
    if (suite.GetName().Version?.ToString(3) != metadataVersion || runtimeVersion != metadataVersion || !runtimeBuild.StartsWith("newfies-server-essentials-suite-" + metadataVersion + "-"))
        throw new Exception("Packaged metadata, assembly and runtime versions do not match.");
    Console.WriteLine("PASS: packaged metadata, assembly and runtime version " + metadataVersion + ".");

    RuntimeHelpers.RunModuleConstructor(suite.ManifestModule.ModuleHandle);
    string packagedSqlite = Path.Combine(extracted, "Microsoft.Data.Sqlite.dll");
    bool sharedSqlite = !File.Exists(packagedSqlite);
    string sqlitePath = sharedSqlite ? Path.Combine(game, "Lib", "Microsoft.Data.Sqlite.dll") : packagedSqlite;
    var sqlite = AssemblyLoadContext.Default.LoadFromAssemblyPath(sqlitePath);
    if (sharedSqlite && (assemblies.Any(p => Path.GetFileName(p).StartsWith("SQLitePCLRaw", StringComparison.OrdinalIgnoreCase)) || Directory.Exists(Path.Combine(extracted, "native", "runtimes"))))
        throw new Exception("Shared SQLite package must not include a competing provider/native runtime.");
    var connectionType = sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!;
    using var connection = (IDisposable)Activator.CreateInstance(connectionType, "Data Source=:memory:")!;
    connectionType.GetMethod("Open", Type.EmptyTypes)!.Invoke(connection, null);
    using var command = (IDisposable)connectionType.GetMethod("CreateCommand", Type.EmptyTypes)!.Invoke(connection, null)!;
    var commandType = command.GetType();
    object Scalar(string sql) { commandType.GetProperty("CommandText")!.SetValue(command, sql); return commandType.GetMethod("ExecuteScalar", Type.EmptyTypes)!.Invoke(command, null)!; }
    string version = (string)Scalar("SELECT sqlite_version()");
    if (sharedSqlite) {
        if (!Version.TryParse(version, out _) || !Path.GetFullPath(sqlite.Location).Equals(Path.GetFullPath(sqlitePath), StringComparison.Ordinal))
            throw new Exception("Shared SQLite did not resolve from the official game: " + sqlite.Location);
    } else if (version != "3.53.3") throw new Exception("Unexpected SQLite runtime: " + version);
    commandType.GetProperty("CommandText")!.SetValue(command, "CREATE TABLE smoke (value INTEGER); INSERT INTO smoke VALUES (42);");
    commandType.GetMethod("ExecuteNonQuery", Type.EmptyTypes)!.Invoke(command, null);
    if (Convert.ToInt64(Scalar("SELECT value FROM smoke")) != 42) throw new Exception("SQLite write/read failed.");
    if (OperatingSystem.IsLinux()) {
        string maps = File.ReadAllText("/proc/self/maps");
        string expectedNative = sharedSqlite ? Path.Combine(game, "Lib", "libe_sqlite3.so") : Path.Combine(extracted, "native", "runtimes", "linux-x64", "native", "libe_sqlite3.so");
        if (!maps.Contains(expectedNative)) throw new Exception("SQLite did not load the expected native library: " + expectedNative);
    }
    Console.WriteLine(sharedSqlite
        ? "PASS: Suite uses official game-managed/native SQLite " + version + " without competing packaged dependencies; in-memory create/insert/read succeeds."
        : "PASS: extracted Suite module initializer resolves packaged SQLite 3.53.3; in-memory create/insert/read succeeds without SDK runtimes/ or game SQLite fallback.");
} finally {
    // Windows retains open native DLLs until process exit; permit OS cleanup there.
    try { Directory.Delete(temporary, true); } catch (IOException) { }
}
public class LogProxy : DispatchProxy {
    public List<string> Errors = null!;
    private static string Render(object? value) => value is object?[] nested ? string.Join(" ", nested.Select(Render)) : value?.ToString() ?? "";
    protected override object? Invoke(MethodInfo? method, object?[]? arguments) {
        if (method?.Name == "Error" || (method?.Name == "Log" && arguments?.FirstOrDefault()?.ToString() == "Error"))
            Errors.Add(string.Join(" ", arguments?.Select(Render) ?? Array.Empty<string>()));
        return method?.ReturnType == typeof(bool) ? false : null;
    }
}
