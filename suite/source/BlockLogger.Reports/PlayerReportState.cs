using System.Collections.Generic;

namespace BlockLogger.Reports;

internal sealed class PlayerReportState
{
	public int SchemaVersion { get; set; } = 1;

	public long NextId { get; set; } = 1L;

	public List<PlayerReportRecord> Reports { get; set; } = new List<PlayerReportRecord>();
}
