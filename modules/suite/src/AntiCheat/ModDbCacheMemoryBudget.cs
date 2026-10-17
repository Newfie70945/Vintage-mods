using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

internal static class ModDbCacheMemoryBudget
{
    internal const int MaximumEntries = 1024;
    internal const int MaximumPending = 512;
    internal const long MaximumEstimatedBytes = 64L * 1024 * 1024;

    // Conservative retained-size estimate: strings, list storage, fingerprint arrays
    // and objects. Do not allocate a second JSON copy merely to measure a cache entry.
    internal static long Estimate(AntiCheatModDbVerificationCacheEntry entry)
    {
        static long Text(string? value) => 64L + (value?.Length ?? 0) * 2L;
        long bytes = 256 + Text(entry.ModId) + Text(entry.Version) + Text(entry.Status) + Text(entry.Message);
        var seen = new HashSet<AntiCheatModDbReleaseFingerprintResolution>();
        if (entry.Release != null) seen.Add(entry.Release);
        if (entry.Releases != null)
            foreach (var release in entry.Releases) if (release != null) seen.Add(release);
        foreach (var release in seen)
        {
            bytes += 512 + Text(release.Version) + Text(release.FileName) + Text(release.DownloadUrl) +
                Text(release.PackageSha256) + Text(release.ContentSha256);
            if (release.Files != null)
                foreach (var file in release.Files)
                    if (file != null) bytes += 128 + Text(file.Path) + Text(file.Sha256);
            if (release.Assemblies != null)
                foreach (var assembly in release.Assemblies)
                    if (assembly != null)
                        bytes += 256 + Text(assembly.AssemblyName) + Text(assembly.Sha256) + Text(assembly.BehaviorSha256) +
                            16L * ((assembly.SimilarityMinHash?.Count ?? 0) + (assembly.BehaviorMinHash?.Count ?? 0));
        }
        return bytes;
    }
}
