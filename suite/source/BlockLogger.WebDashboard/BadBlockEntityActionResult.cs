namespace BlockLogger.WebDashboard;

public sealed class BadBlockEntityActionResult
{
	public bool Success { get; set; }

	public string Message { get; set; } = "";

	public BadBlockEntitySnapshot Snapshot { get; set; } = new BadBlockEntitySnapshot();

	public object? Detail { get; set; }
}
