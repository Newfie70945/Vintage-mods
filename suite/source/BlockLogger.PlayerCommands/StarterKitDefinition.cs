using System.Collections.Generic;
using System.Linq;
using BlockLogger.Services;
using Vintagestory.API.Server;

namespace BlockLogger.PlayerCommands;

public sealed class StarterKitDefinition
{
	public const string ConfigFile = "NewfiesStarterKit.json";

	public const int CurrentSchemaVersion = 1;

	public int SchemaVersion { get; set; } = 1;

	public string UpdatedBy { get; set; } = "";

	public long UpdatedAtUnixMilliseconds { get; set; }

	public List<StarterKitItemDefinition> Items { get; set; } = new List<StarterKitItemDefinition>();

	public static StarterKitDefinition Load(ICoreServerAPI api)
	{
		StarterKitDefinition starterKitDefinition = ModConfigRecovery.LoadOrNull<StarterKitDefinition>(api, "NewfiesStarterKit.json", "StarterKit") ?? new StarterKitDefinition();
		StarterKitDefinition starterKitDefinition2 = starterKitDefinition;
		if (starterKitDefinition2.Items == null)
		{
			List<StarterKitItemDefinition> list = (starterKitDefinition2.Items = new List<StarterKitItemDefinition>());
		}
		starterKitDefinition.Items = (from x in starterKitDefinition.Items
			where x != null && x.Slot >= 0 && !string.IsNullOrWhiteSpace(x.StackBase64)
			orderby x.Slot
			select x).ToList();
		SuiteConfigStorage.Store(api, starterKitDefinition, "NewfiesStarterKit.json");
		return starterKitDefinition;
	}
}
