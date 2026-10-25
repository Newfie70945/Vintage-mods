using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace NewfiesAntiCheatClient;

internal sealed class ClientBuildManifestSnapshot
{
    public string ManifestSha256 { get; init; } = "";
    public string SignatureSha256 { get; init; } = "";
    public string BuildId { get; init; } = "";
}

internal static class SignedBuildManifestClient
{
    internal const string ManifestFileName = "client-integrity.manifest.json";
    internal const string SignatureFileName = "client-integrity.manifest.sig";

    // The signed manifest lives in the same immutable mod package as Client Integrity. Heartbeats
    // and rescans previously reopened that ZIP (or reread both loose files) every time even though
    // the package had not changed. Cache the parsed/hashes by a cheap source metadata signature;
    // a size/mtime change invalidates the cache before the next integrity measurement.
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, CachedManifest> Cache = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private const int MaxCacheEntries = 16;
    private const int MaxManifestBytes = 256 * 1024;

    private sealed class CachedManifest
    {
        public string Signature { get; init; } = "";
        public ClientBuildManifestSnapshot Snapshot { get; init; } = new();
    }

    public static ClientBuildManifestSnapshot Read(string? packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath)) return new();

        string key;
        try { key = Path.GetFullPath(packagePath); }
        catch { key = packagePath; }

        string signature = BuildSourceSignature(key);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached) &&
                string.Equals(cached.Signature, signature, StringComparison.Ordinal))
                return cached.Snapshot;
        }

        ClientBuildManifestSnapshot snapshot;
        try { snapshot = ReadUncached(key); }
        catch { return new(); } // Do not cache transient I/O failures under an unchanged metadata signature.
        lock (CacheLock)
        {
            if (Cache.Count >= MaxCacheEntries && !Cache.ContainsKey(key))
                Cache.Clear();
            Cache[key] = new CachedManifest { Signature = signature, Snapshot = snapshot };
        }
        return snapshot;
    }

    private static ClientBuildManifestSnapshot ReadUncached(string packagePath)
    {
            byte[] manifest = ReadEntry(packagePath, ManifestFileName);
            byte[] signature = ReadEntry(packagePath, SignatureFileName);
            string buildId = "";
            if (manifest.Length > 0)
            {
                using var doc = JsonDocument.Parse(manifest);
                if (doc.RootElement.TryGetProperty("buildId", out var id) && id.ValueKind == JsonValueKind.String)
                    buildId = (id.GetString() ?? "").Trim();
            }
            return new ClientBuildManifestSnapshot
            {
                ManifestSha256 = Hash(manifest),
                SignatureSha256 = Hash(signature),
                BuildId = buildId.Length <= 96 ? buildId : buildId[..96]
            };
    }

    private static string BuildSourceSignature(string packagePath)
    {
        try
        {
            if (File.Exists(packagePath))
            {
                var file = new FileInfo(packagePath);
                if (Path.GetExtension(packagePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    return "Z|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;

                string siblingDirectory = Path.GetDirectoryName(packagePath) ?? "";
                return "F|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks +
                       "|" + FileSignature(Path.Combine(siblingDirectory, ManifestFileName)) +
                       "|" + FileSignature(Path.Combine(siblingDirectory, SignatureFileName));
            }

            if (Directory.Exists(packagePath))
            {
                return "D|" + FileSignature(Path.Combine(packagePath, ManifestFileName)) +
                       "|" + FileSignature(Path.Combine(packagePath, SignatureFileName));
            }

            string directory = Path.GetDirectoryName(packagePath) ?? "";
            if (directory.Length > 0)
            {
                return "S|" + FileSignature(Path.Combine(directory, ManifestFileName)) +
                       "|" + FileSignature(Path.Combine(directory, SignatureFileName));
            }
        }
        catch { }
        return "MISSING";
    }

    private static string FileSignature(string path)
    {
        try
        {
            if (!File.Exists(path)) return "0";
            var file = new FileInfo(path);
            return file.Length + ":" + file.LastWriteTimeUtc.Ticks;
        }
        catch { return "0"; }
    }

    private static byte[] ReadEntry(string? packagePath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(packagePath)) return Array.Empty<byte>();
        if (Directory.Exists(packagePath))
        {
            string path = Path.Combine(packagePath, fileName);
            return File.Exists(path) ? ReadFileBounded(path) : Array.Empty<byte>();
        }
        if (File.Exists(packagePath) && Path.GetExtension(packagePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(packagePath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, false);
            var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/').TrimStart('/'), fileName, StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.Length < 0 || entry.Length > 256 * 1024) return Array.Empty<byte>();
            using var input = entry.Open();
            return ReadBounded(input,entry.Length);
        }
        if (File.Exists(packagePath))
        {
            string sibling = Path.Combine(Path.GetDirectoryName(packagePath) ?? "", fileName);
            return File.Exists(sibling) ? ReadFileBounded(sibling) : Array.Empty<byte>();
        }
        return Array.Empty<byte>();
    }

    private static byte[] ReadFileBounded(string path)
    {
        using var input=File.OpenRead(path);
        if(input.Length>MaxManifestBytes)return Array.Empty<byte>();
        return ReadBounded(input,input.Length);
    }

    private static byte[] ReadBounded(Stream input,long expected)
    {
        if(expected<0||expected>MaxManifestBytes)throw new InvalidDataException("Manifest length exceeds limit.");
        using var output=new MemoryStream((int)expected);
        byte[] buffer=new byte[8192];
        int read;
        while((read=input.Read(buffer,0,(int)Math.Min(buffer.Length,expected-output.Length+1)))>0)
        {
            if(output.Length+read>expected)throw new InvalidDataException("Manifest exceeds its declared length.");
            output.Write(buffer,0,read);
        }
        if(output.Length!=expected)throw new InvalidDataException("Manifest is truncated.");
        return output.ToArray();
    }

    private static string Hash(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return "";
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }
}
