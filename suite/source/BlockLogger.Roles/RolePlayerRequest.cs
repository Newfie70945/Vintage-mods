namespace BlockLogger.Roles;

public sealed class RolePlayerRequest
{
	public string PlayerUid { get; set; } = "";

	public string RoleCode { get; set; } = "";

	public string ExpectedRoleCode { get; set; } = "";
}
