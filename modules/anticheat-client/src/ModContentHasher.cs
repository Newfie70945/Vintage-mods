using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace NewfiesAntiCheatClient;

internal sealed class ModPackageFingerprint
{
    public string PackageSha256 { get; init; } = "";
    public string ContentSha256 { get; init; } = "";
    public long PackageBytes { get; init; }
    public int DllCount { get; init; }
    public long DllBytes { get; init; }
}

internal static class ModContentHasher
{
    private const long MaxEntryBytes = 64L * 1024 * 1024;
    private const long MaxTotalBytes = 512L * 1024 * 1024;
    // Fingerprints are immutable for as long as the source size/mtime signature is unchanged.
    // Keep that evidence for the lifetime of the client process instead of expiring it on a timer:
    // routine heartbeats/rescans still re-check cheap filesystem metadata, but they do not re-read
    // unchanged mod packages or assembly contents from disk. A changed size/mtime invalidates the
    // entry immediately on the next use. Capacity bounds prevent an unbounded cache.
    private const int MaxPackageCacheEntries = 2048;
    private const int MaxFileHashCacheEntries = 4096;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly object FileHashCacheLock = new();
    private static readonly Dictionary<string, FileHashCacheEntry> FileHashCache = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private sealed class FileHashCacheEntry
    {
        public string Signature { get; init; } = "";
        public long CachedUtcTicks { get; init; }
        public string Sha256 { get; init; } = "";
    }

    private sealed class CacheEntry
    {
        public string Signature { get; init; } = "";
        public long CachedUtcTicks { get; init; }
        public ModPackageFingerprint Fingerprint { get; init; } = new();
    }

    public static ModPackageFingerprint FingerprintPackage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new();
        string fullPath;
        try { fullPath = Path.GetFullPath(path); } catch { fullPath = path; }
        string signature = BuildSourceSignature(fullPath);
        long nowTicks = DateTime.UtcNow.Ticks;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(fullPath, out var cached) &&
                string.Equals(cached.Signature, signature, StringComparison.Ordinal))
                return cached.Fingerprint;
        }

        ModPackageFingerprint fp = ComputeFingerprint(fullPath);
        // A transient read/lock failure must be retried even when size/mtime stay unchanged.
        if (fp.PackageSha256.Length == 0) return fp;
        lock (CacheLock)
        {
            if (Cache.Count >= MaxPackageCacheEntries && !Cache.ContainsKey(fullPath))
            {
                foreach (string key in Cache.OrderBy(x => x.Value.CachedUtcTicks).Select(x => x.Key).Take(256).ToList())
                    Cache.Remove(key);
            }
            Cache[fullPath] = new CacheEntry { Signature = signature, CachedUtcTicks = nowTicks, Fingerprint = fp };
        }
        return fp;
    }

    public static string HashPackage(string? path) => FingerprintPackage(path).PackageSha256;
    public static string HashNormalizedContent(string? path) => FingerprintPackage(path).ContentSha256;
    public static (long PackageBytes, int DllCount, long DllBytes) GetPackageStats(string? path)
    {
        var fp = FingerprintPackage(path);
        return (fp.PackageBytes, fp.DllCount, fp.DllBytes);
    }

    private static ModPackageFingerprint ComputeFingerprint(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                string packageHash = HashFile(path);
                if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    bool isDll = Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase);
                    return new ModPackageFingerprint
                    {
                        PackageSha256 = packageHash,
                        ContentSha256 = packageHash,
                        PackageBytes = info.Length,
                        DllCount = isDll ? 1 : 0,
                        DllBytes = isDll ? info.Length : 0L
                    };
                }

                using var fs = File.OpenRead(path);
                using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
                var builder = new StringBuilder();
                long total = 0, dllBytes = 0;
                int dllCount = 0;
                bool contentFingerprintSafe = true;
                foreach (var entry in archive.Entries
                             .Where(e => !string.IsNullOrEmpty(e.Name))
                             .OrderBy(e => e.FullName, StringComparer.Ordinal))
                {
                    string normalized = Normalize(entry.FullName);
                    if (entry.Length < 0 || entry.Length > MaxEntryBytes)
                    {
                        if (!string.Equals(normalized, "modinfo.json", StringComparison.OrdinalIgnoreCase)) contentFingerprintSafe = false;
                        continue;
                    }
                    if (entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        dllCount++;
                        dllBytes += entry.Length;
                    }
                    if (string.Equals(normalized, "modinfo.json", StringComparison.OrdinalIgnoreCase)) continue;
                    total += entry.Length;
                    if (total > MaxTotalBytes) { contentFingerprintSafe = false; continue; }
                    if (!contentFingerprintSafe) continue;
                    using var input = entry.Open();
                    builder.Append(normalized).Append('|').Append(entry.Length).Append('|').Append(HashStream(input)).Append('\n');
                }
                return new ModPackageFingerprint
                {
                    PackageSha256 = packageHash,
                    ContentSha256 = contentFingerprintSafe ? HashBytes(Encoding.UTF8.GetBytes(builder.ToString())) : "",
                    PackageBytes = info.Length,
                    DllCount = dllCount,
                    DllBytes = dllBytes
                };
            }

            if (Directory.Exists(path))
            {
                // Preserve the historical directory-package hash ordering while computing the
                // normalized-content fingerprint from the same file reads. Package identity uses
                // absolute-path OrdinalIgnoreCase ordering; normalized content uses relative-path
                // Ordinal ordering to mirror the server's ModDB ZIP normalization.
                var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                using var packageSha = SHA256.Create();
                var normalizedEntries = new List<(string Path, long Length, string Sha256)>();
                long packageBytes = 0, dllBytes = 0, normalizedTotal = 0;
                int dllCount = 0, statsFiles = 0;
                bool normalizedSafe = true, statsActive = true;
                byte[] ioBuffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    foreach (string file in files)
                    {
                        FileInfo info;
                        try { info = new FileInfo(file); } catch { continue; }
                        string relative = Normalize(Path.GetRelativePath(path, file));
                        byte[] name = Encoding.UTF8.GetBytes(relative);
                        packageSha.TransformBlock(name, 0, name.Length, null, 0);

                        bool entrySafe = info.Length >= 0 && info.Length <= MaxEntryBytes;
                        using var perFileSha = entrySafe ? SHA256.Create() : null;
                        using var input = File.OpenRead(file);
                        int read;
                        while ((read = input.Read(ioBuffer, 0, ioBuffer.Length)) > 0)
                        {
                            packageSha.TransformBlock(ioBuffer, 0, read, null, 0);
                            perFileSha?.TransformBlock(ioBuffer, 0, read, null, 0);
                        }
                        perFileSha?.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

                        if (statsActive && statsFiles < 4096 && entrySafe)
                        {
                            statsFiles++;
                            packageBytes += info.Length;
                            if (Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                            {
                                dllCount++;
                                dllBytes += info.Length;
                            }
                            if (packageBytes > MaxTotalBytes) statsActive = false;
                        }

                        if (string.Equals(relative, "modinfo.json", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!entrySafe || perFileSha == null)
                        {
                            normalizedSafe = false;
                            continue;
                        }
                        normalizedTotal += info.Length;
                        if (normalizedTotal > MaxTotalBytes)
                        {
                            normalizedSafe = false;
                            continue;
                        }
                        normalizedEntries.Add((relative, info.Length, Hex(perFileSha.Hash)));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(ioBuffer);
                }
                packageSha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var normalizedBuilder = new StringBuilder();
                if (normalizedSafe)
                {
                    foreach (var entry in normalizedEntries.OrderBy(x => x.Path, StringComparer.Ordinal))
                        normalizedBuilder.Append(entry.Path).Append('|').Append(entry.Length).Append('|').Append(entry.Sha256).Append('\n');
                }
                return new ModPackageFingerprint
                {
                    PackageSha256 = Hex(packageSha.Hash),
                    ContentSha256 = normalizedSafe ? HashBytes(Encoding.UTF8.GetBytes(normalizedBuilder.ToString())) : "",
                    PackageBytes = packageBytes,
                    DllCount = dllCount,
                    DllBytes = dllBytes
                };
            }
        }
        catch { }
        return new();
    }

    private static string BuildSourceSignature(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                return $"F|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            }
            if (Directory.Exists(path))
            {
                using var sha = SHA256.Create();
                long count = 0, bytes = 0, newest = 0;
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        count++; bytes += fi.Length; newest = Math.Max(newest, fi.LastWriteTimeUtc.Ticks);
                        string relative = Normalize(Path.GetRelativePath(path, file));
                        byte[] meta = Encoding.UTF8.GetBytes(relative + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "\n");
                        sha.TransformBlock(meta, 0, meta.Length, null, 0);
                    }
                    catch { }
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return $"D|{count}|{bytes}|{newest}|{Hex(sha.Hash)}";
            }
        }
        catch { }
        return "MISSING";
    }

    public static string HashFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { fullPath = path; }

        try
        {
            if (!File.Exists(fullPath)) return "";
            var info = new FileInfo(fullPath);
            string signature = info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            long nowTicks = DateTime.UtcNow.Ticks;

            lock (FileHashCacheLock)
            {
                if (FileHashCache.TryGetValue(fullPath, out var cached) &&
                    string.Equals(cached.Signature, signature, StringComparison.Ordinal))
                    return cached.Sha256;
            }

            string hash;
            using (var fs = File.OpenRead(fullPath))
                hash = HashStream(fs);
            if (hash.Length == 0) return "";

            lock (FileHashCacheLock)
            {
                if (FileHashCache.Count >= MaxFileHashCacheEntries && !FileHashCache.ContainsKey(fullPath))
                {
                    foreach (string key in FileHashCache.OrderBy(x => x.Value.CachedUtcTicks).Select(x => x.Key).Take(512).ToList())
                        FileHashCache.Remove(key);
                }
                FileHashCache[fullPath] = new FileHashCacheEntry
                {
                    Signature = signature,
                    CachedUtcTicks = nowTicks,
                    Sha256 = hash
                };
            }
            return hash;
        }
        catch { return ""; }
    }

    public static string HashStream(Stream stream)
    {
        byte[]? buffer = null;
        try
        {
            using var sha = SHA256.Create();
            buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            int read;
            int bytesSinceYield = 0;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                bytesSinceYield += read;
                if (bytesSinceYield >= 8 * 1024 * 1024)
                {
                    bytesSinceYield = 0;
                    try { System.Threading.Thread.Sleep(1); } catch { }
                }
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Hex(sha.Hash);
        }
        catch { return ""; }
        finally
        {
            if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static string HashBytes(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(bytes));
    }

    private static string Normalize(string entry) => (entry ?? "").Replace('\\', '/').TrimStart('/');
    private static string Hex(byte[]? bytes) => Convert.ToHexString(bytes ?? Array.Empty<byte>()).ToLowerInvariant();
}
