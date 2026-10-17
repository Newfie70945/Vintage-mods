using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public static class SuiteConfigStorage
{
	public const string FolderName = "NewfiesServerEssentialsSuite";

	public const string RelativeFolder = "NewfiesServerEssentialsSuite";

	private static readonly string[] OwnedFiles = new string[29]
	{
		"NewfiesBlocklogger.json", "blocklogger-v2.json", "NewfiesServerEssentialsFeatures.json", "NewfiesAnimalLimiter.json", "animal_limiter.json", "NewfiesAnimalLimiter.removals.json", "NewfiesLoginProtection.json", "loginprotection.json", "NewfiesAntiCheat.json", "NewfiesAntiCheatModDbVerificationCache.json",
		"NewfiesAutomatedRestarts.json", "NewfiesChatDisplay.json", "NewfiesPlayerCommands.json", "NewfiesAfk.json", "NewfiesStarterKit.json", "NewfiesWarps.json", "NewfiesPlayerList.json", "NewfiesWebServer.json", "NewfiesWebAdminAccounts.json", "NewfiesCompatibilityIgnores.json",
		"NewfiesWebMap.json", "NewfiesAnnouncements.json", "NewfiesOptimizationExperiments.json", "NewfiesPerformanceCenter.json", "NewfiesOperationsCenter.json", "NewfiesServerEssentialsAdmin.json", "NewfiesSupporterLampState.json", "NewfiesSupporterNicknames.json", "NewfiesCustomPatches.json"
	};

	public static IReadOnlyList<string> Files => OwnedFiles;

	public static string GetModConfigRoot(ICoreServerAPI api)
	{
		try
		{
			return api.GetOrCreateDataPath("ModConfig");
		}
		catch
		{
			string text = Path.Combine(api.DataBasePath, "ModConfig");
			Directory.CreateDirectory(text);
			return text;
		}
	}

	public static string GetConfigRoot(ICoreServerAPI api)
	{
		string text = Path.Combine(GetModConfigRoot(api), "NewfiesServerEssentialsSuite");
		Directory.CreateDirectory(text);
		return text;
	}

	public static string GetBackupRoot(ICoreServerAPI api)
	{
		string text = Path.Combine(GetConfigRoot(api), "backups");
		Directory.CreateDirectory(text);
		return text;
	}

	public static string NormalizeFileName(string fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName))
		{
			throw new ArgumentException("Config filename cannot be empty.", "fileName");
		}
		string text = fileName.Replace('\\', '/');
		text = text.Substring(text.LastIndexOf('/') + 1);
		bool flag = string.IsNullOrWhiteSpace(text);
		bool flag2 = flag;
		if (!flag2)
		{
			bool flag3 = ((text == "." || text == "..") ? true : false);
			flag2 = flag3;
		}
		if (flag2)
		{
			throw new ArgumentException("Config filename is invalid.", "fileName");
		}
		return text;
	}

	public static string RelativePath(string fileName)
	{
		return "NewfiesServerEssentialsSuite/" + NormalizeFileName(fileName);
	}

	public static string DisplayPath(string fileName)
	{
		return RelativePath(fileName);
	}

	public static string GetConfigPath(ICoreServerAPI api, string fileName)
	{
		return Path.Combine(GetConfigRoot(api), NormalizeFileName(fileName));
	}

	public static T? Load<T>(ICoreServerAPI api, string fileName) where T : class
	{
		GetConfigRoot(api);
		return api.LoadModConfig<T>(RelativePath(fileName));
	}

	public static void Store<T>(ICoreServerAPI api, T value, string fileName)
	{
		GetConfigRoot(api);
		api.StoreModConfig(value, RelativePath(fileName));
	}

	public static void InitializeAndRelocateLegacyFiles(ICoreServerAPI api)
	{
		string configRoot = GetConfigRoot(api);
		string backupRoot = GetBackupRoot(api);
		string text = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
		string text2 = Path.Combine(backupRoot, "relocation-" + text);
		bool flag = false;
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		List<string> list = new List<string>();
		bool flag2 = IsStandaloneAfkInstalled(api);
		string[] ownedFiles = OwnedFiles;
		foreach (string text3 in ownedFiles)
		{
			if (flag2 && string.Equals(text3, "NewfiesAfk.json", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string text4 = Path.Combine(configRoot, text3);
			foreach (string item in FindLegacyRootCandidates(api, text3))
			{
				if (!File.Exists(item))
				{
					continue;
				}
				try
				{
					if (!flag)
					{
						Directory.CreateDirectory(text2);
						flag = true;
					}
					string text5 = UniquePath(Path.Combine(text2, text3));
					File.Copy(item, text5, overwrite: false);
					if (!File.Exists(text4))
					{
						File.Copy(item, text4, overwrite: false);
						if (!FilesEqual(item, text4))
						{
							throw new IOException("Copied config did not verify byte-for-byte.");
						}
						File.Delete(item);
						num++;
						api.Logger.Notification("[ServerEssentials/ConfigStorage] Moved {0} into ModConfig/{1}/.", text3, "NewfiesServerEssentialsSuite");
					}
					else if (FilesEqual(item, text4))
					{
						File.Delete(item);
						num2++;
					}
					else
					{
						File.Delete(item);
						num3++;
						api.Logger.Warning("[ServerEssentials/ConfigStorage] Both root and centralized copies of {0} existed and differed. Kept ModConfig/{1}/{0}; archived the old root copy at {2}.", text3, "NewfiesServerEssentialsSuite", text5);
					}
				}
				catch (Exception ex)
				{
					api.Logger.Error("[ServerEssentials/ConfigStorage] Could not relocate legacy root config {0}: {1}: {2}", text3, ex.GetType().Name, ex.Message);
					list.Add($"{text3}: {ex.GetType().Name}: {ex.Message}");
				}
			}
			num4 += RelocateLegacyBackupArtifacts(api, text3, backupRoot);
		}
		if (list.Count > 0)
		{
			throw new InvalidOperationException("Server Essentials could not safely centralize one or more root-level config files. No defaults were written. Resolve the file/permission problem and restart. " + string.Join(" | ", list.Take(5)));
		}
		if (num + num2 + num3 + num4 > 0)
		{
			api.Logger.Notification("[ServerEssentials/ConfigStorage] Centralized config layout ready at ModConfig/{0}/. Moved={1}, duplicate roots removed={2}, conflicts archived={3}, old backup artifacts moved={4}.", "NewfiesServerEssentialsSuite", num, num2, num3, num4);
		}
	}

	public static string? FindLegacyRootFile(ICoreServerAPI api, string fileName)
	{
		return FindLegacyRootCandidates(api, fileName).FirstOrDefault(File.Exists);
	}

	private static bool IsStandaloneAfkInstalled(ICoreServerAPI api)
	{
		try
		{
			return api.ModLoader.Mods.Any((Mod mod) => mod?.Info != null && string.Equals(mod.Info.ModID, "newfiesafk", StringComparison.OrdinalIgnoreCase));
		}
		catch
		{
			return false;
		}
	}

	private static IEnumerable<string> FindLegacyRootCandidates(ICoreServerAPI api, string fileName)
	{
		string normalized = NormalizeFileName(fileName);
		HashSet<string> roots = new HashSet<string>(StringComparer.Ordinal)
		{
			Path.Combine(api.DataBasePath, "ModConfig"),
			Path.Combine(api.DataBasePath, "modconfig")
		};
		try
		{
			roots.Add(GetModConfigRoot(api));
		}
		catch
		{
		}
		foreach (string root in roots)
		{
			string candidate = Path.Combine(root, normalized);
			string centralized = Path.Combine(root, "NewfiesServerEssentialsSuite", normalized);
			if (!string.Equals(candidate, centralized, StringComparison.OrdinalIgnoreCase))
			{
				yield return candidate;
			}
		}
	}

	private static int RelocateLegacyBackupArtifacts(ICoreServerAPI api, string fileName, string backupRoot)
	{
		int num = 0;
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
		string extension = Path.GetExtension(fileName);
		if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
		{
			return 0;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal)
		{
			Path.Combine(api.DataBasePath, "ModConfig"),
			Path.Combine(api.DataBasePath, "modconfig")
		};
		try
		{
			hashSet.Add(GetModConfigRoot(api));
		}
		catch
		{
		}
		foreach (string item in hashSet)
		{
			if (!Directory.Exists(item))
			{
				continue;
			}
			string[] array = new string[2]
			{
				fileNameWithoutExtension + ".pre-migration-v*-to-v*-*" + extension,
				fileNameWithoutExtension + ".corrupt-*" + extension
			};
			foreach (string pattern in array)
			{
				foreach (string item2 in SafeGetFiles(item, pattern))
				{
					if (!item2.StartsWith(GetConfigRoot(api) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
					{
						try
						{
							string destFileName = UniquePath(Path.Combine(backupRoot, Path.GetFileName(item2)));
							File.Move(item2, destFileName);
							num++;
						}
						catch
						{
						}
					}
				}
			}
		}
		return num;
	}

	private static IEnumerable<string> SafeGetFiles(string directory, string pattern)
	{
		try
		{
			return Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
		}
		catch
		{
			return Array.Empty<string>();
		}
	}

	private static bool FilesEqual(string left, string right)
	{
		FileInfo fileInfo = new FileInfo(left);
		FileInfo fileInfo2 = new FileInfo(right);
		if (fileInfo.Length != fileInfo2.Length)
		{
			return false;
		}
		using SHA256 sHA = SHA256.Create();
		using FileStream inputStream = File.OpenRead(left);
		byte[] array = sHA.ComputeHash(inputStream);
		using FileStream inputStream2 = File.OpenRead(right);
		byte[] array2 = sHA.ComputeHash(inputStream2);
		return array.SequenceEqual(array2);
	}

	private static string UniquePath(string desired)
	{
		if (!File.Exists(desired))
		{
			return desired;
		}
		string path = Path.GetDirectoryName(desired) ?? ".";
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(desired);
		string extension = Path.GetExtension(desired);
		for (int i = 2; i < 10000; i++)
		{
			string text = Path.Combine(path, $"{fileNameWithoutExtension}-{i}{extension}");
			if (!File.Exists(text))
			{
				return text;
			}
		}
		return Path.Combine(path, $"{fileNameWithoutExtension}-{Guid.NewGuid():N}{extension}");
	}
}
