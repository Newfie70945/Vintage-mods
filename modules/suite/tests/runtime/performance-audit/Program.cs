using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY");
string dll = Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? throw new Exception("Set NSS_ASSEMBLY"));
AssemblyLoadContext.Default.Resolving += (_, name) => {
    foreach (string dir in new[] { Path.GetDirectoryName(dll)!, game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") }) {
        string path = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
var suite = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
var type = suite.GetType("BlockLogger.Services.IncrementalLogReader", true)!;
object reader = Activator.CreateInstance(type)!;
var read = type.GetMethod("Read")!.CreateDelegate<Func<string, long, Func<string, bool>?, string>>(reader);
string root = Path.Combine(Path.GetTempPath(), "nss-perf-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    var results = new List<object>();
    foreach (int targetBytes in new[] { 64 * 1024, 256 * 1024 }) {
        const string line = "28.10.2026 12:00:00 [Warning] Fixture UTF-8: café 雪 — exception context preserved.\n    at Fixture.Server.OnTick()\n";
        int count = targetBytes / Encoding.UTF8.GetByteCount(line);
        string content = string.Concat(Enumerable.Repeat(line, count));
        string file = Path.Combine(root, targetBytes + ".log");
        File.WriteAllText(file, content, new UTF8Encoding(false));
        string actual = read(file, 0, null);
        if (actual != content) throw new Exception("Log output differs from the complete UTF-8 fixture.");
        for (int i = 0; i < 8; i++) read(file, 0, null);
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("NSS_PERF_ITERATIONS"), out int requested) ? Math.Clamp(requested, 24, 1000) : 24;
        var clock = new Stopwatch();
        for (int trial = 0; trial < 3; trial++) {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            clock.Restart();
            int totalCharacters = 0;
            for (int i = 0; i < iterations; i++) totalCharacters += read(file, 0, null).Length;
            clock.Stop();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (totalCharacters != content.Length * iterations) throw new Exception("Measured log output was incomplete.");
            results.Add(new { targetBytes, inputBytes = new FileInfo(file).Length, iterations, trial,
                allocatedBytesPerRead = bytes / iterations, microsecondsPerRead = clock.Elapsed.TotalMicroseconds / iterations,
                outputSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actual))).ToLowerInvariant() });
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { scenario = "Compiled production incremental log reader; warm local files, UTF-8 lines and exception context; no server/TPS claim", results }));
} finally { Directory.Delete(root, recursive: true); }
