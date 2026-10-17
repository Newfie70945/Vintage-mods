namespace BlockLogger.WebDashboard;

public sealed class FeatureCircuitBreakerStatus
{
	public string Key { get; set; } = "";

	public string Name { get; set; } = "";

	public int ConsecutiveFailures { get; set; }

	public long TotalFailures { get; set; }

	public bool Open { get; set; }

	public long OpenUntilUnixMilliseconds { get; set; }

	public string LastError { get; set; } = "";

	public long LastFailureUnixMilliseconds { get; set; }

	public string Safety { get; set; } = "Safe";
}
