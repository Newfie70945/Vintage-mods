namespace BlockLogger.AnimalLimiter;

public sealed class AnimalLimiterRecommendationInfo
{
	public string Pattern { get; init; } = "";

	public int SuggestedLimit { get; init; }

	public int RadiusChunks { get; init; }

	public int RegionsObserved { get; init; }

	public int TotalEligible { get; init; }

	public int PeakEligible { get; init; }

	public double AverageEligible { get; init; }

	public int MedianEligible { get; init; }

	public int UpperQuartileEligible { get; init; }

	public int EstimatedWouldRemoveNow { get; init; }

	public bool HasEffectiveRule { get; init; }

	public string EffectiveRule { get; init; } = "";

	public int? EffectiveLimit { get; init; }

	public string Confidence { get; init; } = "Low";

	public string Reason { get; init; } = "";
}
