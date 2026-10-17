using System;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public static class CoordinateDisplayService
{
    public static string FormatConfigured(ICoreServerAPI api, string? mode, int x, int y, int z)
    {
        mode = NormalizeMode(mode);
        return mode switch
        {
            "absolute" => $"Abs [{x},{y},{z}]",
            "relativetoworldcenter" => $"Center [{FormatSigned(x - GetWorldCenterX(api))},{y},{FormatSigned(z - GetWorldCenterZ(api))}]",
            _ => $"Spawn [{FormatSigned(GetSpawnRelative(api, x, z).X)},{y},{FormatSigned(GetSpawnRelative(api, x, z).Z)}]"
        };
    }

    public static string GetModeLabel(string? mode)
    {
        return NormalizeMode(mode) switch
        {
            "absolute" => "Absolute",
            "relativetoworldcenter" => "RelativeToWorldCenter",
            _ => "RelativeToDefaultSpawn"
        };
    }

    public static string FormatModeHelp()
    {
        return "CoordinateDisplayMode: Absolute | RelativeToDefaultSpawn | RelativeToWorldCenter";
    }

    private static Vec3i GetSpawnRelative(ICoreServerAPI api, int x, int z)
    {
        var spawn = GetDefaultSpawnOrWorldCenter(api);
        return new Vec3i(x - spawn.X, 0, z - spawn.Z);
    }

    /// <summary>
    /// Returns a stable spawn origin without touching WorldManager.DefaultSpawnPosition.
    /// Vintage Story 1.22 can throw from that getter when the live default-spawn
    /// object has not been populated even though the savegame has a stored spawn.
    /// </summary>
    public static Vec3i GetDefaultSpawnOrWorldCenter(ICoreServerAPI api)
    {
        int centerX = GetWorldCenterX(api);
        int centerZ = GetWorldCenterZ(api);

        try
        {
            var spawn = api.WorldManager.SaveGame?.DefaultSpawn;
            if (spawn != null)
                return new Vec3i(spawn.x, spawn.y ?? 0, spawn.z);
        }
        catch
        {
            // Coordinate display must never make an admin command fail.
        }

        return new Vec3i(centerX, 0, centerZ);
    }

    private static int GetWorldCenterX(ICoreServerAPI api) => api.WorldManager.MapSizeX / 2;
    private static int GetWorldCenterZ(ICoreServerAPI api) => api.WorldManager.MapSizeZ / 2;

    private static string NormalizeMode(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        return normalized switch
        {
            "absolute" => "absolute",
            "relativetoworldcenter" or "center" or "worldcenter" => "relativetoworldcenter",
            "relativetodefaultspawn" or "spawn" or "defaultspawn" or "relativetospawn" => "relativetodefaultspawn",
            _ => "relativetodefaultspawn"
        };
    }

    private static string FormatSigned(int value) => value.ToString("+#;-#;0");
}
