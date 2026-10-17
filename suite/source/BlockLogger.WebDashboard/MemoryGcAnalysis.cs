namespace BlockLogger.WebDashboard;

public sealed class MemoryGcAnalysis
{
	public bool Available { get; set; }

	public int SampleCount { get; set; }

	public string State { get; set; } = "Unavailable";

	public int Score { get; set; }

	public double HeapCurrentMb { get; set; }

	public double HeapMinimumMb { get; set; }

	public double HeapMaximumMb { get; set; }

	public double HeapSlopeMbPerMinute { get; set; }

	public double PrivateCurrentMb { get; set; }

	public double PrivateSlopeMbPerMinute { get; set; }

	public double EstimatedNativeCurrentMb { get; set; }

	public double EstimatedNativeSlopeMbPerMinute { get; set; }

	public double GcCommittedCurrentMb { get; set; }

	public double GcFragmentedCurrentMb { get; set; }

	public int Gen0Increase { get; set; }

	public int Gen1Increase { get; set; }

	public int Gen2Increase { get; set; }

	public double EstimatedPostGcFloorStartMb { get; set; }

	public double EstimatedPostGcFloorEndMb { get; set; }

	public double EstimatedPostGcFloorGrowthMb { get; set; }

	public string Finding { get; set; } = "";

	public string Recommendation { get; set; } = "";
}
