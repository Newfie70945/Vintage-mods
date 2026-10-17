using System.Collections.Generic;

namespace BlockLogger.AnimalLimiter;

public sealed class AnimalLimiterRuleSnapshot
{
	public bool Available { get; init; } = true;

	public bool Enabled { get; init; }

	public bool LimitAllEntities { get; init; }

	public int DefaultLimit { get; init; }

	public int DefaultRadiusChunks { get; init; }

	public List<string> Exclusions { get; init; } = new List<string>();

	public List<AnimalLimiterRuleInfo> Rules { get; init; } = new List<AnimalLimiterRuleInfo>();
}
