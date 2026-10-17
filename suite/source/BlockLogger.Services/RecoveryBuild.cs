using System.Collections.Generic;

namespace BlockLogger.Services;

public sealed class RecoveryBuild
{
	public List<RecoveryOperation> Operations { get; } = new List<RecoveryOperation>();

	public Dictionary<string, int> Reasons { get; } = new Dictionary<string, int>();

	public void Skip(string reason)
	{
		Reasons[reason] = Reasons.GetValueOrDefault(reason) + 1;
	}
}
