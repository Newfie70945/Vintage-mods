using System;
using System.Collections.Generic;
using System.Linq;
using BlockLogger.Services;
using Vintagestory.API.Server;

namespace BlockLogger.PlayerCommands;

public sealed class WarpDatabase
{
	public const string ConfigFile = "NewfiesWarps.json";

	public const int CurrentSchemaVersion = 2;

	public int SchemaVersion { get; set; } = 2;

	public List<WarpDefinition> Warps { get; set; } = new List<WarpDefinition>();

	public void Clamp()
	{
		SchemaVersion = Math.Max(2, SchemaVersion);
		if (Warps == null)
		{
			List<WarpDefinition> list = (Warps = new List<WarpDefinition>());
		}
		Dictionary<string, WarpDefinition> dictionary = new Dictionary<string, WarpDefinition>(StringComparer.OrdinalIgnoreCase);
		foreach (WarpDefinition item in Warps.Where((WarpDefinition x) => x != null))
		{
			item.Clamp();
			if (!string.IsNullOrWhiteSpace(item.Name))
			{
				dictionary[item.Name] = item;
			}
		}
		Warps = dictionary.Values.OrderBy((WarpDefinition x) => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	public static WarpDatabase Load(ICoreServerAPI api)
	{
		WarpDatabase warpDatabase = ModConfigRecovery.LoadOrNull<WarpDatabase>(api, "NewfiesWarps.json", "Warps") ?? new WarpDatabase();
		warpDatabase.Clamp();
		SuiteConfigStorage.Store(api, warpDatabase, "NewfiesWarps.json");
		return warpDatabase;
	}
}
