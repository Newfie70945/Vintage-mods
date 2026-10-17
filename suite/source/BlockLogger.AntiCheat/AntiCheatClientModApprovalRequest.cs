namespace BlockLogger.AntiCheat;

public sealed class AntiCheatClientModApprovalRequest
{
	public string Action { get; set; } = "approve";

	public string Id { get; set; } = "";

	public string ModId { get; set; } = "";

	public string Version { get; set; } = "";

	public string ContentSha256 { get; set; } = "";

	public string PackageSha256 { get; set; } = "";

	public string Label { get; set; } = "";

	public string Notes { get; set; } = "";
}
