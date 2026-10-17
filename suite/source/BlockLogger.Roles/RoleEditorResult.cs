namespace BlockLogger.Roles;

public sealed class RoleEditorResult
{
	public bool Success { get; set; }

	public bool Conflict { get; set; }

	public string Message { get; set; } = "";

	public RoleEditorSnapshot? Snapshot { get; set; }
}
