namespace BlockLogger.ServerHealth;

public sealed class ServerProfileSummary
{
	public long CompletedUnixMilliseconds { get; set; }

	public int DurationSeconds { get; set; }

	public int TickCount { get; set; }

	public double AverageTickMs { get; set; }

	public double P95TickMs { get; set; }

	public double P99TickMs { get; set; }

	public double MaxTickMs { get; set; }

	public int AttributedSegments { get; set; }

	public int UnattributedSegments { get; set; }

	public double AttributedMs { get; set; }

	public double UnattributedMs { get; set; }

	public string CaptureKind { get; set; } = "Manual";

	public long TriggerIncidentUnixMilliseconds { get; set; }
}
