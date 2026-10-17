using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Models;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace NewfiesAntiCheatClient;

internal static class AssemblyFingerprintScanner
{
    private const int MaxTokens = 4096;
    private const int MaxPackageDllsPerMod = 64;
    private const long MaxDllBytes = 64L * 1024 * 1024;

    // Loaded managed assemblies are immutable for the lifetime of the process. Cache the
    // expensive reflection/IL fingerprints by assembly identity+MVID so routine deep scans do
    // not repeatedly walk thousands of types/method bodies on the client.
    private static readonly object AnalysisCacheLock = new();
    private static readonly Dictionary<string, CachedAssemblyAnalysis> AnalysisCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed class CachedAssemblyAnalysis
    {
        public List<string> ModSystemTypeNames { get; init; } = new();
        public int StructureTokenCount { get; init; }
        public string StructureSha256 { get; init; } = "";
        public List<ulong> SimilarityMinHash { get; init; } = new();
        public int BehaviorTokenCount { get; init; }
        public string BehaviorSha256 { get; init; } = "";
        public List<ulong> BehaviorMinHash { get; init; } = new();
    }

    public static Dictionary<Assembly, HashSet<string>> BuildAssociations(ICoreClientAPI api)
    {
        var map = new Dictionary<Assembly, HashSet<string>>();
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToList();
        var byHash = new Dictionary<string, List<Assembly>>(StringComparer.OrdinalIgnoreCase);
        var byFileName = new Dictionary<string, List<Assembly>>(StringComparer.OrdinalIgnoreCase);

        int loadedIndex = 0;
        foreach (var asm in loaded)
        {
            if ((++loadedIndex & 31) == 0) Thread.Yield();
            string location = SafeLocation(asm);
            string hash = ModContentHasher.HashFile(location);
            if (hash.Length == 64)
            {
                if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = new List<Assembly>();
                list.Add(asm);
            }
            string file = Path.GetFileName(location);
            if (!string.IsNullOrWhiteSpace(file))
            {
                if (!byFileName.TryGetValue(file, out var list)) byFileName[file] = list = new List<Assembly>();
                list.Add(asm);
            }
        }

        foreach (var mod in api.ModLoader.Mods.Where(m => m?.Info != null).Take(512))
        {
            string modId = mod.Info.ModID ?? "";
            if (string.IsNullOrWhiteSpace(modId)) continue;

            TryAssociateContainedModSystems(mod, modId, map);
            TryAssociatePackageAssemblies(mod.SourcePath ?? mod.FileName ?? "", modId, byHash, byFileName, map);
        }

        Add(map, typeof(NewfiesAntiCheatClientModSystem).Assembly, AntiCheatProtocol.ClientModId);
        return map;
    }

    public static List<ClientAssemblyDto> Scan(Dictionary<Assembly, HashSet<string>> associations, List<ScanFindingDto> findings)
    {
        var result = new List<ClientAssemblyDto>();
        int scanIndex = 0;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(x => !x.IsDynamic)
                     .OrderBy(x => x.GetName().Name, StringComparer.OrdinalIgnoreCase).Take(512))
        {
            if ((++scanIndex & 15) == 0) Thread.Yield();
            try
            {
                string location = SafeLocation(asm);
                var dto = new ClientAssemblyDto
                {
                    AssemblyName = asm.GetName().Name ?? "",
                    Version = asm.GetName().Version?.ToString() ?? "",
                    FileName = Path.GetFileName(location),
                    Sha256 = ModContentHasher.HashFile(location),
                    FileSizeBytes = SafeFileSize(location),
                    ModuleVersionId = SafeMvid(asm),
                    AssociatedModIds = associations.TryGetValue(asm, out var ids)
                        ? ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(32).ToList()
                        : new List<string>()
                };

                if (ShouldBuildStructure(asm, dto.AssociatedModIds))
                {
                    var analysis = GetOrBuildAnalysis(asm);
                    dto.ModSystemTypeNames = analysis.ModSystemTypeNames.ToList();
                    dto.StructureTokenCount = analysis.StructureTokenCount;
                    dto.StructureSha256 = analysis.StructureSha256;
                    dto.SimilarityMinHash = analysis.SimilarityMinHash.ToList();
                    dto.BehaviorTokenCount = analysis.BehaviorTokenCount;
                    dto.BehaviorSha256 = analysis.BehaviorSha256;
                    dto.BehaviorMinHash = analysis.BehaviorMinHash.ToList();
                }

                result.Add(dto);
            }
            catch (Exception ex)
            {
                findings.Add(new ScanFindingDto
                {
                    Scanner = "Assemblies",
                    Code = "ASSEMBLY_SCAN_ERROR",
                    Severity = (int)AntiCheatSeverity.Info,
                    Summary = "An assembly could not be fingerprinted.",
                    Subject = SafeAssemblyName(asm),
                    Evidence = Trim(ex.Message, 200)
                });
            }
        }
        return result;
    }

    public static Dictionary<string, List<string>> IdentityAssociationMap(Dictionary<Assembly, HashSet<string>> associations)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in associations)
        {
            string key = RuntimeIdentity(pair.Key);
            if (key.Length == 0) continue;
            result[key] = pair.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(32).ToList();
        }
        return result;
    }

    public static string RuntimeIdentity(Assembly asm)
    {
        string name = SafeAssemblyName(asm);
        string version = asm.GetName().Version?.ToString() ?? "";
        string mvid = SafeMvid(asm);
        return name + "|" + version + "|" + mvid;
    }

    private static CachedAssemblyAnalysis GetOrBuildAnalysis(Assembly asm)
    {
        string key = RuntimeIdentity(asm);
        lock (AnalysisCacheLock)
        {
            if (AnalysisCache.TryGetValue(key, out var cached))
                return cached;
        }

        var structureTokens = BuildStructureTokens(asm, out var modSystems);
        var behaviorTokens = BuildBehaviorTokens(asm);
        var built = new CachedAssemblyAnalysis
        {
            ModSystemTypeNames = modSystems,
            StructureTokenCount = structureTokens.Count,
            StructureSha256 = structureTokens.Count > 0
                ? ModContentHasher.HashBytes(Encoding.UTF8.GetBytes(string.Join("\n", structureTokens)))
                : "",
            SimilarityMinHash = structureTokens.Count > 0
                ? StructuralSimilarity.ComputeMinHash(structureTokens)
                : new List<ulong>(),
            BehaviorTokenCount = behaviorTokens.Count,
            BehaviorSha256 = behaviorTokens.Count > 0
                ? ModContentHasher.HashBytes(Encoding.UTF8.GetBytes(string.Join("\n", behaviorTokens)))
                : "",
            BehaviorMinHash = behaviorTokens.Count > 0
                ? StructuralSimilarity.ComputeMinHash(behaviorTokens)
                : new List<ulong>()
        };

        lock (AnalysisCacheLock)
        {
            if (AnalysisCache.Count > 1024)
                AnalysisCache.Clear();
            AnalysisCache[key] = built;
        }
        return built;
    }

    private static List<string> BuildStructureTokens(Assembly asm, out List<string> modSystems)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        modSystems = new List<string>();

        try
        {
            foreach (var reference in asm.GetReferencedAssemblies())
            {
                string name = reference.Name ?? "";
                if (name.Length > 0) tokens.Add("R:" + name);
            }
        }
        catch { }

        foreach (Type type in SafeTypes(asm).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            string typeNameOnly = type.Name ?? "";
            if (typeNameOnly.Length == 0 || typeNameOnly.StartsWith("<", StringComparison.Ordinal)) continue;
            string typeName = type.FullName ?? (string.IsNullOrWhiteSpace(type.Namespace) ? typeNameOnly : type.Namespace + "." + typeNameOnly);
            if (string.IsNullOrWhiteSpace(typeName)) continue;
            tokens.Add("T:" + typeName);

            try
            {
                if (typeof(ModSystem).IsAssignableFrom(type) && !type.IsAbstract)
                    modSystems.Add(typeName);
            }
            catch { }

            try
            {
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    string methodName = method.Name ?? "";
                    if (methodName.Length == 0 || methodName.StartsWith("<", StringComparison.Ordinal)) continue;
                    int parameterCount;
                    try { parameterCount = method.GetParameters().Length; } catch { parameterCount = 0; }
                    tokens.Add("M:" + typeName + ":" + methodName + "/" + parameterCount);
                    if (tokens.Count >= MaxTokens) break;
                }
            }
            catch { }

            if (tokens.Count < MaxTokens)
            {
                try
                {
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        string fieldName = field.Name ?? "";
                        if (fieldName.Length == 0 || fieldName.StartsWith("<", StringComparison.Ordinal)) continue;
                        tokens.Add("F:" + typeName + ":" + fieldName);
                        if (tokens.Count >= MaxTokens) break;
                    }
                }
                catch { }
            }

            if (tokens.Count >= MaxTokens) break;
        }

        modSystems = modSystems.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Take(128).ToList();
        return tokens.OrderBy(x => x, StringComparer.Ordinal).Take(MaxTokens).ToList();
    }

    private static List<string> BuildBehaviorTokens(Assembly asm)
    {
        var tokens = new List<string>();
        foreach (Type type in SafeTypes(asm).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            try
            {
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    MethodBody? body;
                    try { body = method.GetMethodBody(); } catch { continue; }
                    if (body == null) continue;
                    byte[]? il;
                    try { il = body.GetILAsByteArray(); } catch { continue; }
                    int parameterCount;
                    try { parameterCount = method.GetParameters().Length; } catch { parameterCount = 0; }
                    int exceptionCount;
                    try { exceptionCount = body.ExceptionHandlingClauses.Count; } catch { exceptionCount = 0; }
                    string token = IlBehaviorFingerprint.BuildMethodToken(il, parameterCount, exceptionCount);
                    if (token.Length > 0) tokens.Add(token);
                    if (tokens.Count >= MaxTokens) break;
                }
            }
            catch { }
            if (tokens.Count >= MaxTokens) break;
        }
        return tokens.OrderBy(x => x, StringComparer.Ordinal).Take(MaxTokens).ToList();
    }

    private static bool ShouldBuildStructure(Assembly asm, IReadOnlyCollection<string> associatedModIds)
    {
        if (associatedModIds.Count > 0) return true;
        string name = SafeAssemblyName(asm);
        if (name.Length == 0) return false;
        if (name.Equals("System", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Vintagestory", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("protobuf", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("0Harmony", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("OpenTK", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("OpenTK.", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static void TryAssociateContainedModSystems(object mod, string modId, Dictionary<Assembly, HashSet<string>> map)
    {
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type t = mod.GetType();
            foreach (var property in t.GetProperties(flags).Where(p => p.Name.Contains("system", StringComparison.OrdinalIgnoreCase)))
            {
                if (property.GetIndexParameters().Length != 0) continue;
                object? value;
                try { value = property.GetValue(mod); } catch { continue; }
                AssociateValue(value, modId, map);
            }
            foreach (var field in t.GetFields(flags).Where(f => f.Name.Contains("system", StringComparison.OrdinalIgnoreCase)))
            {
                object? value;
                try { value = field.GetValue(mod); } catch { continue; }
                AssociateValue(value, modId, map);
            }
        }
        catch { }
    }

    private static void AssociateValue(object? value, string modId, Dictionary<Assembly, HashSet<string>> map)
    {
        if (value is ModSystem one)
        {
            Add(map, one.GetType().Assembly, modId);
            return;
        }
        if (value is not IEnumerable enumerable || value is string) return;
        int count = 0;
        foreach (object? item in enumerable)
        {
            if (++count > 256) break;
            if (item is ModSystem system) Add(map, system.GetType().Assembly, modId);
        }
    }

    private static void TryAssociatePackageAssemblies(
        string path, string modId,
        Dictionary<string, List<Assembly>> byHash,
        Dictionary<string, List<Assembly>> byFileName,
        Dictionary<Assembly, HashSet<string>> map)
    {
        try
        {
            if (File.Exists(path) && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = File.OpenRead(path);
                using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
                int seen = 0;
                foreach (var entry in archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name) && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Take(MaxPackageDllsPerMod))
                {
                    if (++seen > MaxPackageDllsPerMod || entry.Length < 0 || entry.Length > MaxDllBytes) break;
                    using var input = entry.Open();
                    MatchPackageDll(entry.Name, ModContentHasher.HashStream(input), modId, byHash, byFileName, map);
                }
                return;
            }

            if (Directory.Exists(path))
            {
                foreach (string dll in Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories).Take(MaxPackageDllsPerMod))
                    MatchPackageDll(Path.GetFileName(dll), ModContentHasher.HashFile(dll), modId, byHash, byFileName, map);
            }
        }
        catch { }
    }

    private static void MatchPackageDll(
        string fileName, string hash, string modId,
        Dictionary<string, List<Assembly>> byHash,
        Dictionary<string, List<Assembly>> byFileName,
        Dictionary<Assembly, HashSet<string>> map)
    {
        if (hash.Length == 64 && byHash.TryGetValue(hash, out var exact))
        {
            foreach (var asm in exact) Add(map, asm, modId);
            return;
        }

        // Filename-only association is used only when there is exactly one loaded assembly with
        // that filename; ambiguity is deliberately left unassociated rather than guessed.
        if (!string.IsNullOrWhiteSpace(fileName) && byFileName.TryGetValue(fileName, out var named) && named.Count == 1)
            Add(map, named[0], modId);
    }

    private static void Add(Dictionary<Assembly, HashSet<string>> map, Assembly asm, string modId)
    {
        if (!map.TryGetValue(asm, out var ids)) map[asm] = ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ids.Add(modId);
    }

    private static IEnumerable<Type> SafeTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).Cast<Type>(); }
        catch { return Array.Empty<Type>(); }
    }

    private static string SafeLocation(Assembly asm) { try { return asm.IsDynamic ? "" : (asm.Location ?? ""); } catch { return ""; } }
    private static long SafeFileSize(string? path) { try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? new FileInfo(path).Length : 0L; } catch { return 0L; } }
    private static string SafeMvid(Assembly asm) { try { return asm.ManifestModule.ModuleVersionId.ToString("D"); } catch { return ""; } }
    private static string SafeAssemblyName(Assembly asm) { try { return asm.GetName().Name ?? ""; } catch { return ""; } }
    private static string Trim(string? value, int max) => string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}
