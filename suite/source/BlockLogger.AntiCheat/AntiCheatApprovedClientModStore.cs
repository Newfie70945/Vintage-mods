using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatApprovedClientModStore
{
	public int SchemaVersion { get; set; } = 1;

	public List<AntiCheatApprovedClientModFingerprint> Entries { get; set; } = new List<AntiCheatApprovedClientModFingerprint>();
}
