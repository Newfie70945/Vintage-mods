using System.Collections.Generic;

namespace BlockLogger.PlayerCommands;

public sealed class PlayerTeleportDatabase
{
	public int SchemaVersion { get; set; } = 1;

	public List<PlayerTeleportProfile> Players { get; set; } = new List<PlayerTeleportProfile>();
}
