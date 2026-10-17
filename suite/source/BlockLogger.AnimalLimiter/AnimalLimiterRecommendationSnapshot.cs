using System.Collections.Generic;

namespace BlockLogger.AnimalLimiter;

public sealed class AnimalLimiterRecommendationSnapshot
{
	public long ScanId { get; init; }

	public bool Ready { get; init; }

	public string Error { get; init; } = "";

	public string Note { get; init; } = "";

	public List<AnimalLimiterRecommendationInfo> Recommendations { get; init; } = new List<AnimalLimiterRecommendationInfo>();
}
