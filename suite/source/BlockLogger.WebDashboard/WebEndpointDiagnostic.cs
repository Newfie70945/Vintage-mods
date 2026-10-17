namespace BlockLogger.WebDashboard;

public sealed class WebEndpointDiagnostic
{
	public string Method { get; set; } = "";

	public string Path { get; set; } = "";

	public long Requests { get; set; }

	public long Errors { get; set; }

	public long ResponseBytes { get; set; }

	public double AverageMilliseconds { get; set; }

	public double P95Milliseconds { get; set; }

	public double MaxMilliseconds { get; set; }

	public double AverageMainThreadWaitMilliseconds { get; set; }

	public double AverageMainThreadWorkMilliseconds { get; set; }

	public double AverageQueueWaitMilliseconds { get; set; }
}
