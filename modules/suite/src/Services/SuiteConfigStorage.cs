using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

/// <summary>
/// Central storage layout for all Server Essentials Suite configuration/state JSON that belongs
/// in Vintage Story's ModConfig tree. Active files live under one folder so the ModConfig root
/// stays clean, while old root-level files are relocated automatically on upgrade.
/// </summary>
public static class SuiteConfigStorage
{
    public const string FolderName = "NewfiesServerEssentialsSuite";
    public const string RelativeFolder = FolderName;

    // All Suite-owned JSON files that have historically lived directly under ModConfig.
    // This intentionally includes small Suite state stores that use the ModConfig API so no
    // Newfie's Server Essentials JSON is left scattered in the ModConfig root.
    private static readonly string[] OwnedFiles =
    {
        "NewfiesBlocklogger.json",
        "blocklogger-v2.json",
        "NewfiesServerEssentialsFeatures.json",
        "NewfiesAnimalLimiter.json",
        "animal_limiter.json",
        "NewfiesAnimalLimiter.removals.json",
        "NewfiesLoginProtection.json",
        "loginprotection.json",
        "NewfiesAntiCheat.json",
        "NewfiesSeedPrivacy.json",
        "NewfiesAntiCheatModDbVerificationCache.json",
        "NewfiesAutomatedRestarts.json",
        "NewfiesChatDisplay.json",
        "NewfiesPlayerCommands.json",
        "NewfiesAfk.json",
        "NewfiesStarterKit.json",
        "NewfiesWarps.json",
        "NewfiesPlayerList.json",
        "NewfiesWebServer.json",
        "NewfiesWebAdminAccounts.json",
        "NewfiesCompatibilityIgnores.json",
        "NewfiesModUpdateIgnores.json",
        "NewfiesWebMap.json",
        "NewfiesAnnouncements.json",
        "NewfiesOptimizationExperiments.json",
        "NewfiesPerformanceCenter.json",
        "NewfiesOperationsCenter.json",
        "NewfiesServerEssentialsAdmin.json",
        "NewfiesSupporterLampState.json",
        "NewfiesSupporterNicknames.json",
        "NewfiesCustomPatches.json"
    };

    public static IReadOnlyList<string> Files => OwnedFiles;

    public static string GetModConfigRoot(ICoreServerAPI api)
    {
        // Ask Vintage Story for the canonical ModConfig path so case/platform differences are
        // handled by the game. The fallback exists only for defensive compatibility.
        try
        {
            return api.GetOrCreateDataPath("ModConfig");
        }
        catch
        {
            string fallback = Path.Combine(api.DataBasePath, "ModConfig");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public static string GetConfigRoot(ICoreServerAPI api)
    {
        string root = Path.Combine(GetModConfigRoot(api), FolderName);
        Directory.CreateDirectory(root);
        return root;
    }

    public static string GetBackupRoot(ICoreServerAPI api)
    {
        string root = Path.Combine(GetConfigRoot(api), "backups");
        Directory.CreateDirectory(root);
        return root;
    }

    public static string NormalizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Config filename cannot be empty.", nameof(fileName));

        string normalized = fileName.Replace('\\', '/');
        normalized = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (string.IsNullOrWhiteSpace(normalized) || normalized is "." or "..")
            throw new ArgumentException("Config filename is invalid.", nameof(fileName));

        return normalized;
    }

    public static string RelativePath(string fileName) =>
        FolderName + "/" + NormalizeFileName(fileName);

    public static string DisplayPath(string fileName) => RelativePath(fileName);

    public static string GetConfigPath(ICoreServerAPI api, string fileName) =>
        Path.Combine(GetConfigRoot(api), NormalizeFileName(fileName));

    public static T? Load<T>(ICoreServerAPI api, string fileName)
        where T : class
    {
        GetConfigRoot(api);
        return api.LoadModConfig<T>(RelativePath(fileName));
    }

    public static void Store<T>(ICoreServerAPI api, T value, string fileName)
    {
        GetConfigRoot(api);
        api.StoreModConfig(value, RelativePath(fileName));
    }

    /// <summary>
    /// Moves pre-1.2.22 root-level Suite files into the centralized config folder. The source is
    /// copied and byte-verified before it is removed. When both source and destination exist, the
    /// destination remains authoritative and the old root copy is archived under backups.
    /// </summary>
    public static void InitializeAndRelocateLegacyFiles(ICoreServerAPI api)
    {
        string configRoot = GetConfigRoot(api);
        string backupRoot = GetBackupRoot(api);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        string relocationBackupRoot = Path.Combine(backupRoot, "relocation-" + stamp);
        bool usedBackupRoot = false;
        int moved = 0;
        int deduplicated = 0;
        int conflicts = 0;
        int backupArtifactsMoved = 0;
        var fatalFailures = new List<string>();

        bool standaloneAfkInstalled = IsStandaloneAfkInstalled(api);

        foreach (string fileName in OwnedFiles)
        {
            // Do not steal the standalone AFK mod's active root config while that mod is still
            // installed. The integrated subsystem disables itself in this coexistence case.
            // Once the standalone mod is removed, the next startup safely relocates the file.
            if (standaloneAfkInstalled && string.Equals(fileName, "NewfiesAfk.json", StringComparison.OrdinalIgnoreCase))
                continue;

            string destination = Path.Combine(configRoot, fileName);
            foreach (string source in FindLegacyRootCandidates(api, fileName))
            {
                if (!File.Exists(source)) continue;

                try
                {
                    if (!usedBackupRoot)
                    {
                        Directory.CreateDirectory(relocationBackupRoot);
                        usedBackupRoot = true;
                    }

                    string backup = UniquePath(Path.Combine(relocationBackupRoot, fileName));
                    File.Copy(source, backup, overwrite: false);

                    if (!File.Exists(destination))
                    {
                        File.Copy(source, destination, overwrite: false);
                        if (!FilesEqual(source, destination))
                            throw new IOException("Copied config did not verify byte-for-byte.");

                        File.Delete(source);
                        moved++;
                        api.Logger.Notification(
                            "[ServerEssentials/ConfigStorage] Moved {0} into ModConfig/{1}/.",
                            fileName,
                            FolderName);
                    }
                    else if (FilesEqual(source, destination))
                    {
                        File.Delete(source);
                        deduplicated++;
                    }
                    else
                    {
                        // Never overwrite an already-centralized active file. Preserve the stale
                        // root copy in the relocation backup and remove it from ModConfig root.
                        File.Delete(source);
                        conflicts++;
                        api.Logger.Warning(
                            "[ServerEssentials/ConfigStorage] Both root and centralized copies of {0} existed and differed. " +
                            "Kept ModConfig/{1}/{0}; archived the old root copy at {2}.",
                            fileName,
                            FolderName,
                            backup);
                    }
                }
                catch (Exception ex)
                {
                    // Do not make startup destructive when relocation cannot be completed. The
                    // root file stays in place unless its verified copy/archive already succeeded.
                    api.Logger.Error(
                        "[ServerEssentials/ConfigStorage] Could not relocate legacy root config {0}: {1}: {2}",
                        fileName,
                        ex.GetType().Name,
                        ex.Message);
                    fatalFailures.Add($"{fileName}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            backupArtifactsMoved += RelocateLegacyBackupArtifacts(api, fileName, backupRoot);
        }

        if (fatalFailures.Count > 0)
        {
            throw new InvalidOperationException(
                "Server Essentials could not safely centralize one or more root-level config files. " +
                "No defaults were written. Resolve the file/permission problem and restart. " +
                string.Join(" | ", fatalFailures.Take(5)));
        }

        if (moved + deduplicated + conflicts + backupArtifactsMoved > 0)
        {
            api.Logger.Notification(
                "[ServerEssentials/ConfigStorage] Centralized config layout ready at ModConfig/{0}/. " +
                "Moved={1}, duplicate roots removed={2}, conflicts archived={3}, old backup artifacts moved={4}.",
                FolderName,
                moved,
                deduplicated,
                conflicts,
                backupArtifactsMoved);
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
            return api.ModLoader.Mods.Any(mod =>
                mod?.Info != null &&
                string.Equals(mod.Info.ModID, "newfiesafk", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> FindLegacyRootCandidates(ICoreServerAPI api, string fileName)
    {
        string normalized = NormalizeFileName(fileName);
        var roots = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine(api.DataBasePath, "ModConfig"),
            Path.Combine(api.DataBasePath, "modconfig")
        };

        try { roots.Add(GetModConfigRoot(api)); } catch { }

        foreach (string root in roots)
        {
            string candidate = Path.Combine(root, normalized);
            string centralized = Path.Combine(root, FolderName, normalized);
            if (!string.Equals(candidate, centralized, StringComparison.OrdinalIgnoreCase))
                yield return candidate;
        }
    }

    private static int RelocateLegacyBackupArtifacts(ICoreServerAPI api, string fileName, string backupRoot)
    {
        int moved = 0;
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem)) return 0;

        var roots = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine(api.DataBasePath, "ModConfig"),
            Path.Combine(api.DataBasePath, "modconfig")
        };
        try { roots.Add(GetModConfigRoot(api)); } catch { }

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;

            foreach (string pattern in new[]
                     {
                         $"{stem}.pre-migration-v*-to-v*-*{extension}",
                         $"{stem}.corrupt-*{extension}"
                     })
            {
                foreach (string source in SafeGetFiles(root, pattern))
                {
                    // Never recurse into the centralized folder when root happens to be its parent.
                    if (source.StartsWith(GetConfigRoot(api) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        string destination = UniquePath(Path.Combine(backupRoot, Path.GetFileName(source)));
                        File.Move(source, destination);
                        moved++;
                    }
                    catch { }
                }
            }
        }

        return moved;
    }

    private static IEnumerable<string> SafeGetFiles(string directory, string pattern)
    {
        try { return Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly); }
        catch { return Array.Empty<string>(); }
    }

    private static bool FilesEqual(string left, string right)
    {
        var a = new FileInfo(left);
        var b = new FileInfo(right);
        if (a.Length != b.Length) return false;

        using var sha = SHA256.Create();
        using var leftStream = File.OpenRead(left);
        byte[] leftHash = sha.ComputeHash(leftStream);
        using var rightStream = File.OpenRead(right);
        byte[] rightHash = sha.ComputeHash(rightStream);
        return leftHash.SequenceEqual(rightHash);
    }

    private static string UniquePath(string desired)
    {
        if (!File.Exists(desired)) return desired;

        string directory = Path.GetDirectoryName(desired) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(desired);
        string extension = Path.GetExtension(desired);
        for (int i = 2; i < 10000; i++)
        {
            string candidate = Path.Combine(directory, $"{stem}-{i}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }
}
