using System.Collections.Generic;

namespace BlockLogger.PlayerCommands;

public sealed class PlayerTeleportProfile
{
	public string PlayerUid { get; set; } = "";

	public string LastKnownName { get; set; } = "";

	public PlayerTeleportPosition? Back { get; set; }

	public string BackReason { get; set; } = "";

	public long BackUpdatedAtUnixMilliseconds { get; set; }

	public List<PlayerHomeDefinition> Homes { get; set; } = new List<PlayerHomeDefinition>();
}
