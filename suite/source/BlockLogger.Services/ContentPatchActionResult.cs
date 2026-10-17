namespace BlockLogger.Services;

public sealed class ContentPatchActionResult
{
	public bool Success { get; set; }

	public string Message { get; set; } = "";

	public ContentPatchSnapshot? Snapshot { get; set; }

	public ContentPatchDefinition? Generated { get; set; }

	public ContentPatchPreview? Preview { get; set; }
}
