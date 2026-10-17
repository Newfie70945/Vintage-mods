namespace BlockLogger.Services;

public sealed class CustomPatchActionResult
{
	public bool Success { get; set; }

	public string Message { get; set; } = "";

	public CustomPatchSnapshot? Snapshot { get; set; }

	public CustomPatchDefinition? Generated { get; set; }
}
