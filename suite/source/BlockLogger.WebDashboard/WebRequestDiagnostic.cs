namespace BlockLogger.WebDashboard;

public sealed class WebRequestDiagnostic
{
	public long UnixMilliseconds { get; set; }

	public string Method { get; set; } = "";

	public string Path { get; set; } = "";

	public int StatusCode { get; set; }

	public long ResponseBytes { get; set; }

	public double TotalMilliseconds { get; set; }

	public double QueueWaitMilliseconds { get; set; }

	public double MainThreadWaitMilliseconds { get; set; }

	public double MainThreadWorkMilliseconds { get; set; }

	public string Note { get; set; } = "";
}
