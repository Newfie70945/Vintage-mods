using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BlockLogger.AntiCheat;


public sealed class AntiCheatCoreAssemblyFingerprint
{
    public string AssemblyName { get; init; } = "";
    public string Version { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string ModuleVersionId { get; init; } = "";
}

public sealed class AntiCheatCanonicalDllFingerprint
{
    public string FileName { get; init; } = "";
    public long FileSizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
}

public sealed class AntiCheatClientCompanionBaseline
{
    public string ModId { get; init; } = "";
    public string Version { get; init; } = "";
    public string SourceFileName { get; init; } = "";
    public string PackageSha256 { get; init; } = "";
    public string ContentSha256 { get; init; } = "";
    public long PackageBytes { get; init; }
    public int DllCount { get; init; }
    public long DllBytes { get; init; }
    public string AssemblyName { get; init; } = "NewfiesAntiCheatClient";
    public string AssemblyFileName { get; init; } = "NewfiesAntiCheatClient.dll";
    public string AssemblySha256 { get; init; } = "";
    public long AssemblyFileSizeBytes { get; init; }
    public string ModuleVersionId { get; init; } = "";
    public string BuildManifestSha256 { get; init; } = "";
    public string BuildManifestSignatureSha256 { get; init; } = "";
    public string BuildId { get; init; } = "";
    public bool BuildManifestSignatureVerified { get; init; }
}

public sealed class AntiCheatCanonicalModFingerprint
{
    public string ModId { get; init; } = "";
    public string Version { get; init; } = "";
    public string PackageSha256 { get; init; } = "";
    public string ContentSha256 { get; init; } = "";
    public string SourceFileName { get; init; } = "";
    public long PackageBytes { get; init; }
    public int DllCount { get; init; }
    public long DllBytes { get; init; }
    public System.Collections.Generic.List<AntiCheatCanonicalDllFingerprint> Assemblies { get; init; } = new();
}

/// <summary>
/// Local package/content hashing used to compare a client's claimed server-shared mod build with
/// the exact package loaded by the server. The normalized hash ignores ZIP metadata and root
/// modinfo.json, so simply renaming/recompressing a package does not create a false mismatch.
/// </summary>
internal static class AntiCheatLocalModHasher
{
    private const long MaxEntryBytes = 64L * 1024 * 1024;
    private const long MaxTotalBytes = 512L * 1024 * 1024;

    public static (long PackageBytes, int DllCount, long DllBytes, System.Collections.Generic.List<AntiCheatCanonicalDllFingerprint> Assemblies) GetPackageStats(string? path)
    {
        var assemblies = new System.Collections.Generic.List<AntiCheatCanonicalDllFingerprint>();
        if (string.IsNullOrWhiteSpace(path)) return (0L, 0, 0L, assemblies);
        try
        {
            if (File.Exists(path))
            {
                long packageBytes = new FileInfo(path).Length;
                if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var fs = File.OpenRead(path);
                    using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
                    long dllBytes = 0;
                    foreach (var entry in archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name) && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName, StringComparer.Ordinal).Take(128))
                    {
                        if (entry.Length < 0 || entry.Length > MaxEntryBytes) continue;
                        dllBytes += entry.Length;
                        if (dllBytes > MaxTotalBytes) break;
                        using var dllStream = entry.Open();
                        assemblies.Add(new AntiCheatCanonicalDllFingerprint { FileName = Path.GetFileName(entry.Name), FileSizeBytes = entry.Length, Sha256 = HashStream(dllStream) });
                    }
                    return (packageBytes, assemblies.Count, dllBytes, assemblies);
                }
                bool isDll = Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase);
                if (isDll) assemblies.Add(new AntiCheatCanonicalDllFingerprint { FileName = Path.GetFileName(path), FileSizeBytes = packageBytes, Sha256 = HashFile(path) });
                return (packageBytes, assemblies.Count, isDll ? packageBytes : 0L, assemblies);
            }

            if (Directory.Exists(path))
            {
                long packageBytes = 0;
                long dllBytes = 0;
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(4096))
                {
                    long length;
                    try { length = new FileInfo(file).Length; } catch { continue; }
                    if (length < 0 || length > MaxEntryBytes) continue;
                    packageBytes += length;
                    if (Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase) && assemblies.Count < 128)
                    {
                        dllBytes += length;
                        assemblies.Add(new AntiCheatCanonicalDllFingerprint { FileName = Path.GetFileName(file), FileSizeBytes = length, Sha256 = HashFile(file) });
                    }
                    if (packageBytes > MaxTotalBytes) break;
                }
                return (packageBytes, assemblies.Count, dllBytes, assemblies);
            }
        }
        catch { }
        return (0L, 0, 0L, assemblies);
    }

    public static string HashFileSha256(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
        try { return HashFile(path); } catch { return ""; }
    }

    public static string HashPackage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            if (File.Exists(path)) return HashFile(path);
            if (!Directory.Exists(path)) return "";
            using var sha = SHA256.Create();
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                byte[] name = Encoding.UTF8.GetBytes(Path.GetRelativePath(path, file).Replace('\\', '/'));
                sha.TransformBlock(name, 0, name.Length, null, 0);
                using var fs = File.OpenRead(file);
                TransformStream(sha, fs);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Hex(sha.Hash);
        }
        catch { return ""; }
    }

    public static string HashNormalizedContent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            if (File.Exists(path) && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = File.OpenRead(path);
                using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
                var builder = new StringBuilder();
                long total = 0;
                foreach (var entry in archive.Entries
                             .Where(e => !string.IsNullOrEmpty(e.Name) && !string.Equals(Normalize(e.FullName), "modinfo.json", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(e => e.FullName, StringComparer.Ordinal))
                {
                    if (entry.Length < 0 || entry.Length > MaxEntryBytes) return "";
                    total += entry.Length;
                    if (total > MaxTotalBytes) return "";
                    using var input = entry.Open();
                    builder.Append(Normalize(entry.FullName)).Append('|').Append(entry.Length).Append('|').Append(HashStream(input)).Append('\n');
                }
                return HashBytes(Encoding.UTF8.GetBytes(builder.ToString()));
            }

            if (Directory.Exists(path))
            {
                var builder = new StringBuilder();
                long total = 0;
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                             .OrderBy(x => Path.GetRelativePath(path, x).Replace('\\', '/'), StringComparer.Ordinal))
                {
                    string relative = Normalize(Path.GetRelativePath(path, file));
                    if (string.Equals(relative, "modinfo.json", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(file);
                    if (info.Length < 0 || info.Length > MaxEntryBytes) return "";
                    total += info.Length;
                    if (total > MaxTotalBytes) return "";
                    using var input = File.OpenRead(file);
                    builder.Append(relative).Append('|').Append(info.Length).Append('|').Append(HashStream(input)).Append('\n');
                }
                return HashBytes(Encoding.UTF8.GetBytes(builder.ToString()));
            }
            return File.Exists(path) ? HashFile(path) : "";
        }
        catch { return ""; }
    }

    private static string HashFile(string path) { using var fs = File.OpenRead(path); return HashStream(fs); }
    private static string HashStream(Stream stream) { using var sha = SHA256.Create(); return Hex(sha.ComputeHash(stream)); }
    private static string HashBytes(byte[] bytes) => Hex(SHA256.HashData(bytes));
    private static void TransformStream(HashAlgorithm sha, Stream input)
    {
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0) sha.TransformBlock(buffer, 0, read, null, 0);
    }
    private static string Normalize(string entry) => (entry ?? "").Replace('\\', '/').TrimStart('/');
    private static string Hex(byte[]? bytes) => Convert.ToHexString(bytes ?? Array.Empty<byte>()).ToLowerInvariant();
}
