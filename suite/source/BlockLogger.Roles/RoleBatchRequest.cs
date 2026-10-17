using System.Collections.Generic;

namespace BlockLogger.Roles;

public sealed class RoleBatchRequest
{
	public string Revision { get; set; } = "";

	public string RoleCode { get; set; } = "";

	public bool Preview { get; set; } = true;

	public List<RolePlayerRequest> Players { get; set; } = new List<RolePlayerRequest>();
}
