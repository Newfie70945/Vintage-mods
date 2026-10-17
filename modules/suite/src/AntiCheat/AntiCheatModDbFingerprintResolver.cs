using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NewfiesAntiCheat.Shared;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatModDbAssemblyFingerprintResolution
{
    public string AssemblyName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public int StructureTokenCount { get; set; }
    public List<ulong> SimilarityMinHash { get; set; } = new();
    public string BehaviorSha256 { get; set; } = "";
    public int BehaviorTokenCount { get; set; }
    public List<ulong> BehaviorMinHash { get; set; } = new();
}

public sealed class AntiCheatModDbFileFingerprintResolution
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Bytes { get; set; }
}

public sealed class AntiCheatModDbPackageManifestResolution
{
    public string ContentSha256 { get; set; } = "";
    public int FileCount { get; set; }
    public bool ManifestComplete { get; set; }
    public List<AntiCheatModDbFileFingerprintResolution> Files { get; set; } = new();
}

public sealed class AntiCheatModDbReleaseFingerprintResolution
{
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public long PackageBytes { get; set; }
    public int DllCount { get; set; }
    public long DllBytes { get; set; }
    public int FileCount { get; set; }
    public bool ManifestComplete { get; set; }
    public List<AntiCheatModDbFileFingerprintResolution> Files { get; set; } = new();
    public List<AntiCheatModDbAssemblyFingerprintResolution> Assemblies { get; set; } = new();
}

public sealed class AntiCheatModDbFingerprintResolution
{
    public bool Success { get; set; }
    public string ModId { get; set; } = "";
    // Backward-compatible newest-release summary fields.
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public List<string> AssemblyNames { get; set; } = new();
    public List<string> AssemblySha256s { get; set; } = new();
    public List<AntiCheatModDbReleaseFingerprintResolution> Releases { get; set; } = new();
    public string Message { get; set; } = "";
}

/// <summary>
/// Resolves the newest Mod DB releases for an identifier and fingerprints them without
/// installing or loading the downloaded mod. The caller controls how many recent releases are processed.
/// </summary>
public static class AntiCheatModDbFingerprintResolver
{
    public const int DefaultReleaseFingerprints = 6;
    public const int MaxReleaseFingerprints = 20;
    private const long MaxDownloadBytes = 128L * 1024 * 1024;
    private const long MaxEntryBytes = 64L * 1024 * 1024;
    private const long MaxTotalUncompressedBytes = 256L * 1024 * 1024;
    private const int MaxManifestEntries = 2048;
    private static readonly HttpClient Http = CreateHttpClient();

    private sealed class ReleaseCandidate
    {
        public long ReleaseId { get; set; }
        public string Version { get; set; } = "";
        public string FileName { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NewfiesServerEssentialsSuite/" + SuiteRelease.Version);
        return http;
    }

    public static async Task<AntiCheatModDbFingerprintResolution> ResolveExactVersionAsync(string modId, string version, string targetGameVersion, CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeModId(modId);
        string requestedVersion = (version ?? "").Trim();
        if (normalized.Length == 0) return Fail(modId, "A valid Mod DB identifier is required.");
        if (requestedVersion.Length == 0) return Fail(normalized, "The client did not report a version, so an exact Mod DB release cannot be verified.");

        try
        {
            // A ModDB author can re-upload the same semantic version more than once. Treat every
            // official upload that declares the requested ModID + version as an acceptable candidate
            // instead of assuming the newest release ID is the only legitimate byte sequence.
            var candidates = await TryResolveExactReleaseCandidatesFromV1Async(normalized, requestedVersion, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
                return Fail(normalized, $"The Vintage Story Mod DB does not expose an exact '{requestedVersion}' release for this mod ID.");

            var releases = new List<AntiCheatModDbReleaseFingerprintResolution>();
            var failures = new List<string>();
            foreach (var candidate in candidates.Take(8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var release = await FingerprintReleaseAsync(normalized, candidate, cancellationToken).ConfigureAwait(false);
                    if (!VersionEquivalent(release.Version, requestedVersion))
                    {
                        failures.Add($"{candidate.FileName}: package declared '{release.Version}'");
                        continue;
                    }
                    releases.Add(release);
                }
                catch (Exception ex)
                {
                    failures.Add((candidate.Version.Length > 0 ? candidate.Version : candidate.FileName) + ": " + ex.Message);
                }
            }

            if (releases.Count == 0)
                return Fail(normalized, "Could not fingerprint any official upload for the exact Mod DB version" + (failures.Count > 0 ? ": " + string.Join("; ", failures.Take(3)) : "."));

            var newest = releases[0];
            var result = new AntiCheatModDbFingerprintResolution
            {
                Success = true,
                ModId = normalized,
                Version = newest.Version,
                FileName = newest.FileName,
                DownloadUrl = newest.DownloadUrl,
                PackageSha256 = newest.PackageSha256,
                ContentSha256 = newest.ContentSha256,
                AssemblyNames = releases.SelectMany(x => x.Assemblies).Select(x => x.AssemblyName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList(),
                AssemblySha256s = releases.SelectMany(x => x.Assemblies).Select(x => x.Sha256).Where(IsSha256).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList(),
                Releases = releases,
                Message = $"Resolved {releases.Count} official Mod DB upload{(releases.Count == 1 ? "" : "s")} for exact release {normalized} {requestedVersion}."
            };
            if (failures.Count > 0) result.Message += $" {failures.Count} matching upload(s) could not be processed.";
            return result;
        }
        catch (Exception ex)
        {
            return Fail(normalized, $"Could not resolve exact Mod DB release: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static async Task<AntiCheatModDbFingerprintResolution> ResolveAsync(string modId, string targetGameVersion, int releaseDepth = DefaultReleaseFingerprints, CancellationToken cancellationToken = default)
    {
        releaseDepth = Math.Clamp(releaseDepth, 1, MaxReleaseFingerprints);
        string normalized = NormalizeModId(modId);
        if (normalized.Length == 0) return Fail(modId, "A valid Mod DB identifier is required.");

        try
        {
            var candidates = await TryResolveLatestReleaseCandidatesFromV1Async(normalized, releaseDepth, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                string? compatible = await TryResolveCompatibleDownloadUrlAsync(normalized, targetGameVersion, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(compatible))
                    candidates.Add(new ReleaseCandidate { ReleaseId = 0, DownloadUrl = compatible, FileName = FileNameFromUrl(compatible) });
            }
            if (candidates.Count == 0) return Fail(normalized, "The Vintage Story Mod DB did not expose a downloadable release for this mod ID.");

            var releases = new List<AntiCheatModDbReleaseFingerprintResolution>();
            var failures = new List<string>();
            foreach (var candidate in candidates.Take(releaseDepth))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    releases.Add(await FingerprintReleaseAsync(normalized, candidate, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    failures.Add((candidate.Version.Length > 0 ? candidate.Version : candidate.FileName) + ": " + ex.Message);
                }
            }
            if (releases.Count == 0)
                return Fail(normalized, "Could not fingerprint any of the latest Mod DB releases" + (failures.Count > 0 ? ": " + string.Join("; ", failures.Take(3)) : "."));

            var newest = releases[0];
            var result = new AntiCheatModDbFingerprintResolution
            {
                Success = true,
                ModId = normalized,
                Version = newest.Version,
                FileName = newest.FileName,
                DownloadUrl = newest.DownloadUrl,
                PackageSha256 = newest.PackageSha256,
                ContentSha256 = newest.ContentSha256,
                AssemblyNames = releases.SelectMany(x => x.Assemblies).Select(x => x.AssemblyName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList(),
                AssemblySha256s = releases.SelectMany(x => x.Assemblies).Select(x => x.Sha256).Where(IsSha256).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList(),
                Releases = releases,
                Message = $"Resolved {releases.Count} release fingerprint{(releases.Count == 1 ? "" : "s")} for {normalized}: {string.Join(", ", releases.Select(x => string.IsNullOrWhiteSpace(x.Version) ? x.FileName : x.Version))}."
            };
            if (failures.Count > 0) result.Message += $" {failures.Count} release(s) could not be processed.";
            return result;
        }
        catch (Exception ex)
        {
            return Fail(normalized, $"Could not resolve Mod DB fingerprints: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<List<ReleaseCandidate>> TryResolveLatestReleaseCandidatesFromV1Async(string modId, int releaseDepth, CancellationToken ct)
    {
        string url = "https://mods.vintagestory.at/api/mod/" + Uri.EscapeDataString(modId);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return new();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("mod", out var mod) || mod.ValueKind != JsonValueKind.Object ||
            !mod.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return new();

        var result = new List<ReleaseCandidate>();
        foreach (var item in releases.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string direct = GetString(item, "mainfile");
            string fileName = GetString(item, "filename");
            string version = GetString(item, "modversion");
            long releaseId = GetInt64(item, "releaseid");
            if (string.IsNullOrWhiteSpace(direct) || !LooksLikeZipUrl(direct)) continue;
            result.Add(new ReleaseCandidate { ReleaseId = releaseId, Version = version, FileName = fileName, DownloadUrl = direct });
        }
        return result.OrderByDescending(x => x.ReleaseId).ThenByDescending(x => x.Version, StringComparer.OrdinalIgnoreCase).Take(Math.Clamp(releaseDepth, 1, MaxReleaseFingerprints)).ToList();
    }

    private static async Task<List<ReleaseCandidate>> TryResolveExactReleaseCandidatesFromV1Async(string modId, string requestedVersion, CancellationToken ct)
    {
        string url = "https://mods.vintagestory.at/api/mod/" + Uri.EscapeDataString(modId);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return new();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("mod", out var mod) || mod.ValueKind != JsonValueKind.Object ||
            !mod.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return new();

        var result = new List<ReleaseCandidate>();
        foreach (var item in releases.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string version = GetString(item, "modversion");
            if (!VersionEquivalent(version, requestedVersion)) continue;
            string direct = GetString(item, "mainfile");
            if (string.IsNullOrWhiteSpace(direct) || !LooksLikeZipUrl(direct)) continue;
            result.Add(new ReleaseCandidate
            {
                ReleaseId = GetInt64(item, "releaseid"),
                Version = version,
                FileName = GetString(item, "filename"),
                DownloadUrl = direct
            });
        }
        return result.OrderByDescending(x => x.ReleaseId).Take(8).ToList();
    }

    private static bool VersionEquivalent(string? left, string? right)
    {
        static string NormalizeVersion(string? value)
        {
            string v = (value ?? "").Trim();
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase) && v.Length > 1 && char.IsDigit(v[1])) v = v[1..];
            return v.Trim();
        }
        return string.Equals(NormalizeVersion(left), NormalizeVersion(right), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<AntiCheatModDbReleaseFingerprintResolution> FingerprintReleaseAsync(string requestedModId, ReleaseCandidate candidate, CancellationToken ct)
    {
        Uri uri = MakeOfficialDownloadUri(candidate.DownloadUrl);
        byte[] bytes = await DownloadBoundedAsync(uri, ct).ConfigureAwait(false);
        if (bytes.Length == 0) throw new InvalidDataException("Release download was empty.");

        string packageSha = Sha256(bytes);
        using var memory = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
        var modInfoEntry = archive.Entries.FirstOrDefault(e => string.Equals(NormalizeEntryPath(e.FullName), "modinfo.json", StringComparison.OrdinalIgnoreCase));
        if (modInfoEntry == null) throw new InvalidDataException("Package does not contain a root modinfo.json.");

        string reportedModId = "", packageVersion = "";
        using (var info = modInfoEntry.Open())
        using (var doc = JsonDocument.Parse(info))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                reportedModId = GetString(doc.RootElement, "modid").Trim();
                packageVersion = GetString(doc.RootElement, "version").Trim();
            }
        }
        if (!string.Equals(reportedModId, requestedModId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Package identity mismatch: requested '{requestedModId}', package declares '{reportedModId}'.");

        var assemblyResults = new List<AntiCheatModDbAssemblyFingerprintResolution>();
        long assemblyBytes = 0;
        foreach (var entry in archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name) && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            if (entry.Length < 0 || entry.Length > MaxEntryBytes) continue;
            assemblyBytes += entry.Length;
            if (assemblyBytes > MaxTotalUncompressedBytes) break;
            byte[] dll = ReadEntryBounded(entry);
            var similarity = TryBuildAssemblySimilarity(dll, Path.GetFileNameWithoutExtension(entry.Name));
            similarity.Sha256 = Sha256(dll);
            assemblyResults.Add(similarity);
        }

        var manifest = ComputeZipContentManifest(archive);
        return new AntiCheatModDbReleaseFingerprintResolution
        {
            Version = string.IsNullOrWhiteSpace(packageVersion) ? candidate.Version : packageVersion,
            FileName = string.IsNullOrWhiteSpace(candidate.FileName) ? FileNameFromUrl(uri.ToString()) : candidate.FileName,
            DownloadUrl = uri.ToString(),
            PackageSha256 = packageSha,
            ContentSha256 = manifest.ContentSha256,
            PackageBytes = bytes.LongLength,
            DllCount = assemblyResults.Count,
            DllBytes = assemblyResults.Sum(x => Math.Max(0L, x.FileSizeBytes)),
            FileCount = manifest.FileCount,
            ManifestComplete = manifest.ManifestComplete,
            Files = manifest.Files,
            Assemblies = assemblyResults.Take(32).ToList()
        };
    }

    private static AntiCheatModDbAssemblyFingerprintResolution TryBuildAssemblySimilarity(byte[]? dll, string fallbackName)
    {
        var result = new AntiCheatModDbAssemblyFingerprintResolution { AssemblyName = fallbackName, FileSizeBytes = dll?.LongLength ?? 0 };
        if (dll is not { Length: > 0 }) return result;
        try
        {
            using var stream = new MemoryStream(dll, writable: false);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata) return result;
            MetadataReader reader = pe.GetMetadataReader();
            if (reader.IsAssembly)
            {
                var definition = reader.GetAssemblyDefinition();
                string name = reader.GetString(definition.Name);
                if (!string.IsNullOrWhiteSpace(name)) result.AssemblyName = name;
            }
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            var behaviorTokens = new List<string>();
            foreach (var handle in reader.AssemblyReferences)
            {
                string name = reader.GetString(reader.GetAssemblyReference(handle).Name);
                if (!string.IsNullOrWhiteSpace(name)) tokens.Add("R:" + name);
            }
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                string typeNameOnly = reader.GetString(type.Name);
                if (string.IsNullOrWhiteSpace(typeNameOnly) || typeNameOnly.StartsWith("<", StringComparison.Ordinal)) continue;
                string ns = reader.GetString(type.Namespace);
                string typeName = string.IsNullOrWhiteSpace(ns) ? typeNameOnly : ns + "." + typeNameOnly;
                tokens.Add("T:" + typeName);
                foreach (var mh in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(mh);
                    string methodName = reader.GetString(method.Name);
                    if (string.IsNullOrWhiteSpace(methodName) || methodName.StartsWith("<", StringComparison.Ordinal)) continue;
                    int parameterCount = 0;
                    foreach (var ph in method.GetParameters()) if (reader.GetParameter(ph).SequenceNumber > 0) parameterCount++;
                    tokens.Add("M:" + typeName + ":" + methodName + "/" + parameterCount);
                    if (method.RelativeVirtualAddress != 0 && behaviorTokens.Count < 4096)
                    {
                        try
                        {
                            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                            byte[]? il = body.GetILBytes();
                            string behavior = IlBehaviorFingerprint.BuildMethodToken(il, parameterCount, body.ExceptionRegions.Length);
                            if (behavior.Length > 0) behaviorTokens.Add(behavior);
                        }
                        catch { }
                    }
                }
                foreach (var fh in type.GetFields())
                {
                    string fieldName = reader.GetString(reader.GetFieldDefinition(fh).Name);
                    if (string.IsNullOrWhiteSpace(fieldName) || fieldName.StartsWith("<", StringComparison.Ordinal)) continue;
                    tokens.Add("F:" + typeName + ":" + fieldName);
                }
                if (tokens.Count >= 4096) break;
            }
            var ordered = tokens.OrderBy(x => x, StringComparer.Ordinal).Take(4096).ToList();
            result.StructureTokenCount = ordered.Count;
            result.SimilarityMinHash = StructuralSimilarity.ComputeMinHash(ordered);
            var orderedBehavior = behaviorTokens.OrderBy(x => x, StringComparer.Ordinal).Take(4096).ToList();
            result.BehaviorTokenCount = orderedBehavior.Count;
            if (orderedBehavior.Count > 0)
            {
                result.BehaviorSha256 = Sha256(Encoding.UTF8.GetBytes(string.Join("\n", orderedBehavior)));
                result.BehaviorMinHash = StructuralSimilarity.ComputeMinHash(orderedBehavior);
            }
        }
        catch { }
        return result;
    }

    private static async Task<string?> TryResolveCompatibleDownloadUrlAsync(string modId, string targetGameVersion, CancellationToken ct)
    {
        string url = $"https://mods.vintagestory.at/api/v2/mods/install-information?ids={Uri.EscapeDataString(modId)}&gv={Uri.EscapeDataString(targetGameVersion)}";
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in data.EnumerateObject())
            if (string.Equals(prop.Name, modId, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Object && TryGetString(prop.Value, "fileUrl", out string fileUrl) && LooksLikeZipUrl(fileUrl)) return fileUrl;
        return null;
    }

    private static async Task<byte[]> DownloadBoundedAsync(Uri uri, CancellationToken ct)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > MaxDownloadBytes) throw new InvalidDataException($"Release exceeds the {MaxDownloadBytes / 1024 / 1024} MiB fingerprint download limit.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        int capacity = contentLength.HasValue && contentLength.Value > 0 && contentLength.Value <= int.MaxValue ? (int)contentLength.Value : 0;
        using var output = new MemoryStream(capacity);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > MaxDownloadBytes) throw new InvalidDataException($"Release exceeds the {MaxDownloadBytes / 1024 / 1024} MiB fingerprint download limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private static byte[] ReadEntryBounded(ZipArchiveEntry entry)
    {
        if (entry.Length < 0 || entry.Length > MaxEntryBytes) throw new InvalidDataException("Assembly entry exceeds the fingerprint safety limit.");
        using var input = entry.Open();
        using var output = new MemoryStream(entry.Length <= int.MaxValue ? (int)entry.Length : 0);
        input.CopyTo(output);
        if (output.Length > MaxEntryBytes) throw new InvalidDataException("Assembly entry exceeds the fingerprint safety limit.");
        return output.ToArray();
    }

    private static Uri MakeOfficialDownloadUri(string value)
    {
        Uri uri = Uri.TryCreate(value, UriKind.Absolute, out var absolute) ? absolute : new Uri(new Uri("https://mods.vintagestory.at/"), value.TrimStart('/'));
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || !(uri.Host.Equals("mods.vintagestory.at", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".vintagestory.at", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Mod DB returned a download outside an approved Vintage Story HTTPS host.");
        if (!LooksLikeZipUrl(uri.ToString())) throw new InvalidDataException("Mod DB release is not a ZIP package.");
        return uri;
    }

    public static AntiCheatModDbPackageManifestResolution FingerprintLocalPackageManifest(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath)) throw new FileNotFoundException("Evidence package was not found.", zipPath);
        var info = new FileInfo(zipPath);
        if (info.Length <= 0 || info.Length > MaxDownloadBytes) throw new InvalidDataException($"Package exceeds the {MaxDownloadBytes / 1024 / 1024} MiB fingerprint limit.");
        using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        return ComputeZipContentManifest(archive);
    }

    private static AntiCheatModDbPackageManifestResolution ComputeZipContentManifest(ZipArchive archive)
    {
        var builder = new StringBuilder();
        var files = new List<AntiCheatModDbFileFingerprintResolution>();
        long total = 0;
        int fileCount = 0;
        bool manifestComplete = true;
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            if (entry.Length < 0 || entry.Length > MaxEntryBytes) throw new InvalidDataException($"Archive entry '{entry.FullName}' exceeds the fingerprint safety limit.");
            total += entry.Length;
            if (total > MaxTotalUncompressedBytes) throw new InvalidDataException("Archive exceeds the uncompressed fingerprint safety limit.");
            string path = NormalizeEntryPath(entry.FullName);
            using var stream = entry.Open();
            string sha = Sha256(stream);
            // Preserve the exact normalized-content algorithm used by the client: root modinfo.json
            // is excluded from ContentSha256, but it remains in the diagnostic file manifest so an
            // evidence diff can still show metadata-only changes.
            if (!string.Equals(path, "modinfo.json", StringComparison.OrdinalIgnoreCase))
                builder.Append(path).Append('|').Append(entry.Length).Append('|').Append(sha).Append('\n');
            fileCount++;
            bool uniquePath = seenPaths.Add(path);
            if (files.Count < MaxManifestEntries && uniquePath)
                files.Add(new AntiCheatModDbFileFingerprintResolution { Path = path, Sha256 = sha, Bytes = entry.Length });
            else
                manifestComplete = false;
        }
        return new AntiCheatModDbPackageManifestResolution
        {
            ContentSha256 = Sha256(Encoding.UTF8.GetBytes(builder.ToString())),
            FileCount = fileCount,
            ManifestComplete = manifestComplete && files.Count == fileCount,
            Files = files
        };
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static string Sha256(Stream stream) { using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant(); }
    private static string NormalizeEntryPath(string value) => (value ?? "").Replace('\\', '/').TrimStart('/');
    private static string FileNameFromUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return Path.GetFileName(value);
        string fromQuery = Uri.UnescapeDataString(uri.Query ?? "");
        int dl = fromQuery.IndexOf("dl=", StringComparison.OrdinalIgnoreCase);
        if (dl >= 0) return Uri.UnescapeDataString(fromQuery[(dl + 3)..].TrimStart('?', '&'));
        return Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
    }
    private static bool LooksLikeZipUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Contains(".zip", StringComparison.OrdinalIgnoreCase);
    }
    private static string GetString(JsonElement obj, string name) => TryGetString(obj, name, out string value) ? value : "";
    private static long GetInt64(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject()) if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetInt64(out long value)) return value;
        return 0;
    }
    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = "";
        foreach (var p in obj.EnumerateObject()) if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) { value = p.Value.GetString() ?? ""; return true; }
        return false;
    }
    private static string NormalizeModId(string? value)
    {
        string v = (value ?? "").Trim().ToLowerInvariant();
        if (v.Length is < 1 or > 64) return "";
        foreach (char c in v) if (!(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')) return "";
        return v;
    }
    private static bool IsSha256(string? value) => value != null && value.Length == 64 && value.All(Uri.IsHexDigit);
    private static AntiCheatModDbFingerprintResolution Fail(string modId, string message) => new() { Success = false, ModId = NormalizeModId(modId), Message = message };
}
