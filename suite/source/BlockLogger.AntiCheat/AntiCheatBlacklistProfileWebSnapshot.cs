using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatBlacklistProfileWebSnapshot
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public int ModCount { get; set; }

	public int FingerprintCount { get; set; }

	public List<string> ModIds { get; set; } = new List<string>();

	public long CreatedUtc { get; set; }

	public long UpdatedUtc { get; set; }
}
