namespace BlockLogger.Services;

public sealed class CustomPatchSuggestion
{
	public string Source { get; set; } = "";

	public string Title { get; set; } = "";

	public string Detail { get; set; } = "";

	public string Recommendation { get; set; } = "";

	public string ModId { get; set; } = "";

	public string Confidence { get; set; } = "Low";
}
