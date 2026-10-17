using System;
using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatBlacklistProfile
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N");

	public string Name { get; set; } = "Blacklist profile";

	public List<string> ModIds { get; set; } = new List<string>();

	public List<AntiCheatForbiddenFingerprint> Fingerprints { get; set; } = new List<AntiCheatForbiddenFingerprint>();

	public long CreatedUtc { get; set; }

	public long UpdatedUtc { get; set; }
}
