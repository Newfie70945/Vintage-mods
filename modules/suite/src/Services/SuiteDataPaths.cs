using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public static class SuiteDataPaths
{
    public static string GetSuiteRoot(ICoreServerAPI api)
    {
        // Keep generated data separate from human-edited ModConfig files.
        // ICoreAPICommon.GetOrCreateDataPath() resolves beneath VintagestoryData.
        var modData = api.GetOrCreateDataPath("ModData");
        var suiteRoot = Path.Combine(modData, "NewfiesServerEssentialsSuite");
        Directory.CreateDirectory(suiteRoot);
        return suiteRoot;
    }

    public static string GetWorldRoot(ICoreServerAPI api)
    {
        var root = GetSuiteRoot(api);
        var worlds = Path.Combine(root, "worlds");
        Directory.CreateDirectory(worlds);
        var worldId = api.World.SavegameIdentifier ?? "unknownworld";
        var folder = MakeWorldFolder(worldId);
        var worldRoot = Path.Combine(worlds, folder);
        Directory.CreateDirectory(worldRoot);
        // Optional child folders are created lazily by the feature that actually writes to them.
        // This prevents empty backups/diagnostics/exports folders from appearing just because
        // the suite started. ("exports" is currently unused and is no longer created at all.)
        return worldRoot;
    }

    public static string GetBackupsRoot(ICoreServerAPI api)
    {
        var path = Path.Combine(GetWorldRoot(api), "backups");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string MakeWorldFolder(string worldId)
    {
        worldId ??= "unknownworld";
        var sb = new StringBuilder();
        foreach (char ch in worldId)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_') sb.Append(ch);
            else if (char.IsWhiteSpace(ch)) sb.Append('-');
            if (sb.Length >= 40) break;
        }
        var prefix = sb.Length == 0 ? "world" : sb.ToString().Trim('-');
        if (prefix.Length == 0) prefix = "world";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(worldId))).ToLowerInvariant()[..12];
        return $"{prefix}-{hash}";
    }
}
