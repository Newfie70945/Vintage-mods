using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

internal sealed class AntiCheatHistoryState
{
	public int SchemaVersion { get; set; } = 1;

	public long NextId { get; set; } = 1L;

	public List<AntiCheatIncidentRecord> Incidents { get; set; } = new List<AntiCheatIncidentRecord>();
}
