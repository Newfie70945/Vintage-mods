using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Vintagestory.API.Server;

namespace NewfiesServerEssentials.Discord.Config;

internal static class DiscordConfigStorage
{
    internal const string FolderName = "NewfiesServerEssentialsSuite";

    private static readonly string[] OwnedFiles =
    {
        "NewfiesServerEssentialsDiscord.json",
        "NewfiesDiscordLink.json"
    };

    internal static string RelativePath(string fileName) =>
        FolderName + "/" + Normalize(fileName);

    internal static string GetConfigRoot(ICoreServerAPI api)
    {
        string modConfigRoot;
        try { modConfigRoot = api.GetOrCreateDataPath("ModConfig"); }
        catch
        {
            modConfigRoot = Path.Combine(api.DataBasePath, "ModConfig");
            Directory.CreateDirectory(modConfigRoot);
        }

        string root = Path.Combine(modConfigRoot, FolderName);
        Directory.CreateDirectory(root);
        return root;
    }

    internal static T? Load<T>(ICoreServerAPI api, string fileName) where T : class
    {
        GetConfigRoot(api);
        return api.LoadModConfig<T>(RelativePath(fileName));
    }

    internal static void Store<T>(ICoreServerAPI api, T value, string fileName)
    {
        GetConfigRoot(api);
        api.StoreModConfig(value, RelativePath(fileName));
    }

    internal static void InitializeAndRelocateLegacyFiles(ICoreServerAPI api)
    {
        string root = GetConfigRoot(api);
        string backupRoot = Path.Combine(root, "backups");
        Directory.CreateDirectory(backupRoot);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        string relocationRoot = Path.Combine(backupRoot, "discord-relocation-" + stamp);
        bool usedRelocationRoot = false;
        int moved = 0;
        int conflicts = 0;
        var fatalFailures = new List<string>();

        foreach (string fileName in OwnedFiles)
        {
            string destination = Path.Combine(root, fileName);
            foreach (string source in LegacyCandidates(api, fileName))
            {
                if (!File.Exists(source)) continue;

                try
                {
                    if (!usedRelocationRoot)
                    {
                        Directory.CreateDirectory(relocationRoot);
                        usedRelocationRoot = true;
                    }

                    string backup = UniquePath(Path.Combine(relocationRoot, fileName));
                    File.Copy(source, backup, overwrite: false);

                    if (!File.Exists(destination))
                    {
                        File.Copy(source, destination, overwrite: false);
                        if (!FilesEqual(source, destination))
                            throw new IOException("Copied Discord config did not verify byte-for-byte.");
                        File.Delete(source);
                        moved++;
                    }
                    else if (FilesEqual(source, destination))
                    {
                        File.Delete(source);
                    }
                    else
                    {
                        File.Delete(source);
                        conflicts++;
                        api.Logger.Warning(
                            "[NSE Discord] Root and centralized copies of {0} differed. Kept ModConfig/{1}/{0}; archived the old root copy at {2}.",
                            fileName,
                            FolderName,
                            backup);
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Error(
                        "[NSE Discord] Could not relocate legacy root config {0}: {1}: {2}",
                        fileName,
                        ex.GetType().Name,
                        ex.Message);
                    fatalFailures.Add($"{fileName}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        if (fatalFailures.Count > 0)
        {
            throw new InvalidOperationException(
                "NSE Discord could not safely centralize one or more root-level config files. " +
                "No defaults were written. Resolve the file/permission problem and restart. " +
                string.Join(" | ", fatalFailures.Take(5)));
        }

        if (moved + conflicts > 0)
        {
            api.Logger.Notification(
                "[NSE Discord] Config storage centralized at ModConfig/{0}/. Moved={1}, conflicts archived={2}.",
                FolderName,
                moved,
                conflicts);
        }
    }

    private static IEnumerable<string> LegacyCandidates(ICoreServerAPI api, string fileName)
    {
        string normalized = Normalize(fileName);
        var roots = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine(api.DataBasePath, "ModConfig"),
            Path.Combine(api.DataBasePath, "modconfig")
        };
        try { roots.Add(api.GetOrCreateDataPath("ModConfig")); } catch { }

        foreach (string root in roots)
            yield return Path.Combine(root, normalized);
    }

    private static string Normalize(string fileName)
    {
        string value = (fileName ?? "").Replace('\\', '/');
        value = value[(value.LastIndexOf('/') + 1)..];
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..")
            throw new ArgumentException("Invalid config filename.", nameof(fileName));
        return value;
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
