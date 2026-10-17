using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class WebAdminAccountStore
{
	public const int CurrentSchemaVersion = 1;

	public int SchemaVersion { get; set; } = 1;

	public List<WebAdminAccountRecord> Accounts { get; set; } = new List<WebAdminAccountRecord>();
}
