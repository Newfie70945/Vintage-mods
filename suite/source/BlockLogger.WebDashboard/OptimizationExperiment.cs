namespace BlockLogger.WebDashboard;

public sealed class OptimizationExperiment
{
	public string Id { get; set; } = "";

	public string ProviderId { get; set; } = "tungsten";

	public string ProviderName { get; set; } = "Tungsten";

	public string OptimizationCode { get; set; } = "";

	public string OptimizationName { get; set; } = "";

	public bool TargetEnabled { get; set; }

	public long CreatedUnixMilliseconds { get; set; }

	public string RuntimeId { get; set; } = "";

	public string Status { get; set; } = "AwaitingRestart";

	public OptimizationMetricSnapshot Baseline { get; set; } = new OptimizationMetricSnapshot();

	public OptimizationMetricSnapshot? After { get; set; }

	public string Result { get; set; } = "";

	public string Confidence { get; set; } = "Low";

	public double ImprovementScore { get; set; }

	public string Reason { get; set; } = "";
}
