using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class BadBlockEntityPersistedState
{
	public List<BadBlockEntityRecord> Entries { get; set; } = new List<BadBlockEntityRecord>();
}
