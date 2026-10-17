using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

internal static class ModDbCacheMemoryBudget
{
	internal const int MaximumEntries = 1024;

	internal const int MaximumPending = 512;

	internal const long MaximumEstimatedBytes = 67108864L;

	internal static long Estimate(AntiCheatModDbVerificationCacheEntry entry)
	{
		long num = 256 + Text(entry.ModId) + Text(entry.Version) + Text(entry.Status) + Text(entry.Message);
		HashSet<AntiCheatModDbReleaseFingerprintResolution> hashSet = new HashSet<AntiCheatModDbReleaseFingerprintResolution>();
		if (entry.Release != null)
		{
			hashSet.Add(entry.Release);
		}
		if (entry.Releases != null)
		{
			foreach (AntiCheatModDbReleaseFingerprintResolution release in entry.Releases)
			{
				if (release != null)
				{
					hashSet.Add(release);
				}
			}
		}
		foreach (AntiCheatModDbReleaseFingerprintResolution item in hashSet)
		{
			num += 512 + Text(item.Version) + Text(item.FileName) + Text(item.DownloadUrl) + Text(item.PackageSha256) + Text(item.ContentSha256);
			if (item.Files != null)
			{
				foreach (AntiCheatModDbFileFingerprintResolution file in item.Files)
				{
					if (file != null)
					{
						num += 128 + Text(file.Path) + Text(file.Sha256);
					}
				}
			}
			if (item.Assemblies == null)
			{
				continue;
			}
			foreach (AntiCheatModDbAssemblyFingerprintResolution assembly in item.Assemblies)
			{
				if (assembly != null)
				{
					num += 256 + Text(assembly.AssemblyName) + Text(assembly.Sha256) + Text(assembly.BehaviorSha256) + 16L * (long)((assembly.SimilarityMinHash?.Count ?? 0) + (assembly.BehaviorMinHash?.Count ?? 0));
				}
			}
		}
		return num;
		static long Text(string? value)
		{
			return 64 + (long)(value?.Length ?? 0) * 2L;
		}
	}
}
