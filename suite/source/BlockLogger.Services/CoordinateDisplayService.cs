using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public static class CoordinateDisplayService
{
	public static string FormatConfigured(ICoreServerAPI api, string? mode, int x, int y, int z)
	{
		mode = NormalizeMode(mode);
		if (1 == 0)
		{
		}
		string result;
		if (mode == "absolute")
		{
			result = $"Abs [{x},{y},{z}]";
		}
		else
		{
			result = ((!(mode == "relativetoworldcenter")) ? $"Spawn [{FormatSigned(GetSpawnRelative(api, x, z).X)},{y},{FormatSigned(GetSpawnRelative(api, x, z).Z)}]" : $"Center [{FormatSigned(x - GetWorldCenterX(api))},{y},{FormatSigned(z - GetWorldCenterZ(api))}]");
		}
		if (1 == 0)
		{
		}
		return result;
	}

	public static string GetModeLabel(string? mode)
	{
		string text = NormalizeMode(mode);
		if (1 == 0)
		{
		}
		string result;
		if (text == "absolute")
		{
			result = "Absolute";
		}
		else
		{
			result = ((!(text == "relativetoworldcenter")) ? "RelativeToDefaultSpawn" : "RelativeToWorldCenter");
		}
		if (1 == 0)
		{
		}
		return result;
	}

	public static string FormatModeHelp()
	{
		return "CoordinateDisplayMode: Absolute | RelativeToDefaultSpawn | RelativeToWorldCenter";
	}

	private static Vec3i GetSpawnRelative(ICoreServerAPI api, int x, int z)
	{
		Vec3i defaultSpawnOrWorldCenter = GetDefaultSpawnOrWorldCenter(api);
		return new Vec3i(x - defaultSpawnOrWorldCenter.X, 0, z - defaultSpawnOrWorldCenter.Z);
	}

	public static Vec3i GetDefaultSpawnOrWorldCenter(ICoreServerAPI api)
	{
		int worldCenterX = GetWorldCenterX(api);
		int worldCenterZ = GetWorldCenterZ(api);
		try
		{
			PlayerSpawnPos playerSpawnPos = api.WorldManager.SaveGame?.DefaultSpawn;
			if (playerSpawnPos != null)
			{
				return new Vec3i(playerSpawnPos.x, playerSpawnPos.y.GetValueOrDefault(), playerSpawnPos.z);
			}
		}
		catch
		{
		}
		return new Vec3i(worldCenterX, 0, worldCenterZ);
	}

	private static int GetWorldCenterX(ICoreServerAPI api)
	{
		return api.WorldManager.MapSizeX / 2;
	}

	private static int GetWorldCenterZ(ICoreServerAPI api)
	{
		return api.WorldManager.MapSizeZ / 2;
	}

	private static string NormalizeMode(string? mode)
	{
		string text = (mode ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty)
			.ToLowerInvariant();
		if (1 == 0)
		{
		}
		string result;
		switch (text)
		{
		case "absolute":
			result = "absolute";
			break;
		case "relativetoworldcenter":
		case "center":
		case "worldcenter":
			result = "relativetoworldcenter";
			break;
		case "relativetodefaultspawn":
		case "spawn":
		case "defaultspawn":
		case "relativetospawn":
			result = "relativetodefaultspawn";
			break;
		default:
			result = "relativetodefaultspawn";
			break;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static string FormatSigned(int value)
	{
		return value.ToString("+#;-#;0");
	}
}
