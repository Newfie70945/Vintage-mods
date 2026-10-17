namespace BlockLogger.Roles;

public sealed class RoleEditorRequest
{
	public string Action { get; set; } = "save";

	public string Revision { get; set; } = "";

	public RoleEditorRow Role { get; set; } = new RoleEditorRow();

	public bool? UseRoleNameColors { get; set; }
}
