namespace BlockLogger.AnimalLimiter;

public sealed class AnimalLimiterRuleInfo
{
	public string Pattern { get; init; } = "";

	public int Limit { get; init; }

	public int RadiusChunks { get; init; }

	public bool UsesDefaultRadius { get; init; }

	public int RegisteredMatches { get; init; }
}
