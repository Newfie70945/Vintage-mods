using System.Collections.Generic;

namespace BlockLogger.PlayerCommands;

public sealed class StarterKitUsageDatabase
{
	public int SchemaVersion { get; set; } = 1;

	public List<string> ClaimedPlayerUids { get; set; } = new List<string>();
}
