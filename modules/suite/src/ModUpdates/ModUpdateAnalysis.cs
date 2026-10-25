using System;
using System.Collections.Generic;
using System.Linq;
using ProperVersion;

namespace BlockLogger.ModUpdates;

/// <summary>One declared dependency of an installed mod, resolved against what is installed and enabled.</summary>
public sealed class ModDependencyInfo
{
    public string ModId { get; set; } = "";
    public string RequiredVersion { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    /// <summary>Ok | Missing | Disabled | TooOld | Unknown</summary>
    public string Status { get; set; } = "Ok";
    public string Detail { get; set; } = "";
}

/// <summary>
/// A staff decision to stop flagging one mod as outdated. It is tied to the game version, the installed
/// version and (for updates) the release that was on offer, so it expires by itself when any of them change.
/// </summary>
public sealed class ModUpdateIgnoreRecord
{
    public string ModId { get; set; } = "";
    public string Status { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string RecommendedVersion { get; set; } = "";
    public string IgnoredBy { get; set; } = "";
    public long IgnoredUnixMilliseconds { get; set; }
}

public sealed class ModUpdateIgnoreStore
{
    public int SchemaVersion { get; set; } = 1;
    public List<ModUpdateIgnoreRecord> Ignores { get; set; } = new();
}

/// <summary>Pure, engine-independent rules for the Mods page. Kept separate so they can be tested alone.</summary>
public static class ModUpdateAnalysis
{
    /// <summary>Statuses a person can choose to ignore. Staged, Current and Core never need it.</summary>
    public static bool IsIgnorable(string status) =>
        status is "Update" or "NotFound" or "Retracted" or "Published" or "Error";

    public static bool IsIgnoreActive(ModUpdateIgnoreRecord? record, string gameVersion, string installedVersion,
        string recommendedVersion, string status)
    {
        if (record == null) return false;
        if (!string.Equals(record.GameVersion, gameVersion, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(record.InstalledVersion, installedVersion, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(record.Status, status, StringComparison.Ordinal)) return false;
        // A newer release than the one the person looked at deserves a fresh look.
        return !string.Equals(status, "Update", StringComparison.Ordinal) ||
               string.Equals(record.RecommendedVersion, recommendedVersion, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Why an ignore stopped applying, or "" while it still applies. Used for the "expired" hint.</summary>
    public static string IgnoreExpiryReason(ModUpdateIgnoreRecord record, string gameVersion, string installedVersion,
        string recommendedVersion, string status)
    {
        if (!string.Equals(record.GameVersion, gameVersion, StringComparison.OrdinalIgnoreCase))
            return $"game version changed ({record.GameVersion} -> {gameVersion})";
        if (!string.Equals(record.InstalledVersion, installedVersion, StringComparison.OrdinalIgnoreCase))
            return $"installed version changed ({record.InstalledVersion} -> {installedVersion})";
        if (!string.Equals(record.Status, status, StringComparison.Ordinal))
            return $"status changed ({record.Status} -> {status})";
        if (status == "Update" && !string.Equals(record.RecommendedVersion, recommendedVersion, StringComparison.OrdinalIgnoreCase))
            return $"a newer release is available ({record.RecommendedVersion} -> {recommendedVersion})";
        return "";
    }

    /// <summary>Vintage Story dependency versions are minimums: the installed version must be at least the declared one.</summary>
    public static string CompareToRequirement(string required, string installed)
    {
        required = (required ?? "").Trim();
        installed = (installed ?? "").Trim();
        if (required.Length == 0 || required == "*") return "Ok";
        if (installed.Length == 0) return "Unknown";
        if (!SemVer.TryParse(required, out var need) || !SemVer.TryParse(installed, out var have)) return "Unknown";
        return have >= need ? "Ok" : "TooOld";
    }

    /// <param name="declared">dependency id -> minimum version, as written in the mod's modinfo.</param>
    /// <param name="installedVersions">every installed non-core mod, enabled or not.</param>
    /// <param name="enabledVersions">only mods that are loaded; includes "game".</param>
    public static List<ModDependencyInfo> ResolveDependencies(IReadOnlyDictionary<string, string> declared,
        IReadOnlyDictionary<string, string> installedVersions, IReadOnlyDictionary<string, string> enabledVersions)
    {
        var result = new List<ModDependencyInfo>();
        foreach (var pair in declared.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(pair.Key, "game", StringComparison.OrdinalIgnoreCase)) continue;
            var info = new ModDependencyInfo { ModId = pair.Key, RequiredVersion = pair.Value ?? "" };
            if (enabledVersions.TryGetValue(pair.Key, out var loaded))
            {
                info.InstalledVersion = loaded;
                info.Status = CompareToRequirement(info.RequiredVersion, loaded);
                info.Detail = info.Status switch
                {
                    "TooOld" => $"needs {info.RequiredVersion} or newer, {loaded} is loaded",
                    "Unknown" => "version could not be compared",
                    _ => ""
                };
            }
            else if (installedVersions.TryGetValue(pair.Key, out var present))
            {
                info.InstalledVersion = present;
                info.Status = "Disabled";
                info.Detail = "installed but not enabled";
            }
            else
            {
                info.Status = "Missing";
                info.Detail = "not installed";
            }
            result.Add(info);
        }
        return result;
    }

    /// <summary>mod id -> ids of the other mods that declare it as a dependency.</summary>
    public static Dictionary<string, List<string>> ReverseDependencies(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> declaredByMod)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in declaredByMod.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            foreach (var dependency in mod.Value.Keys)
            {
                if (string.Equals(dependency, "game", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dependency, mod.Key, StringComparison.OrdinalIgnoreCase)) continue;
                if (!result.TryGetValue(dependency, out var list)) result[dependency] = list = new List<string>();
                if (!list.Contains(mod.Key, StringComparer.OrdinalIgnoreCase)) list.Add(mod.Key);
            }
        return result;
    }

    /// <summary>Plain-language game requirement taken from the mod's own "game" dependency.</summary>
    public static (string Requirement, string Compatibility) DescribeGameRequirement(string required, string running)
    {
        required = (required ?? "").Trim();
        if (required.Length == 0 || required == "*") return ("", "Unknown");
        return CompareToRequirement(required, running) switch
        {
            "Ok" => (required, "Compatible"),
            "TooOld" => (required, "RequiresNewerGame"),
            _ => (required, "Unknown")
        };
    }
}
