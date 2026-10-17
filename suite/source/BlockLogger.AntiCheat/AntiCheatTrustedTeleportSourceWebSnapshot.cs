using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatTrustedTeleportSourceWebSnapshot
{
	public string Code { get; set; } = "";

	public string Name { get; set; } = "";

	public string Status { get; set; } = "Active";

	public string TrustScope { get; set; } = "Movement + Reach";

	public string Verification { get; set; } = "";

	public long ObservedCount { get; set; }

	public long LastSeenUtc { get; set; }

	public string LastPlayerName { get; set; } = "";

	public string LastReason { get; set; } = "";

	public List<AntiCheatTrustedTeleportEventWebSnapshot> RecentEvents { get; set; } = new List<AntiCheatTrustedTeleportEventWebSnapshot>();
}
