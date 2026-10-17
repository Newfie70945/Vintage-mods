namespace BlockLogger.WebDashboard;

public sealed class NetworkPerformanceSample
{
	public long UnixMilliseconds { get; set; }

	public long TotalSentBytes { get; set; }

	public long TotalReceivedBytes { get; set; }

	public double SentBytesPerSecond { get; set; }

	public double ReceivedBytesPerSecond { get; set; }

	public int Players { get; set; }
}
