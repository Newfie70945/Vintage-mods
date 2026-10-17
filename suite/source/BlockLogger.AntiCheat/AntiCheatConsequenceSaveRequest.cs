using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatConsequenceSaveRequest
{
	public List<AntiCheatConsequenceRule> Rules { get; set; } = new List<AntiCheatConsequenceRule>();

	public bool ApplyCurrent { get; set; }
}
