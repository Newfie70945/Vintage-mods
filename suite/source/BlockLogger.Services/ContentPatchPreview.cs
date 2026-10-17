namespace BlockLogger.Services;

public sealed class ContentPatchPreview
{
	public string BeforeJson { get; set; } = "";

	public string AfterJson { get; set; } = "";

	public string PatchJson { get; set; } = "";

	public string Note { get; set; } = "";
}
