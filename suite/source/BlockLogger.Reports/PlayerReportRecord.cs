using System.Collections.Generic;

namespace BlockLogger.Reports;

public sealed class PlayerReportRecord
{
	public long Id { get; set; }

	public long CreatedUnixMilliseconds { get; set; }

	public long UpdatedUnixMilliseconds { get; set; }

	public string Status { get; set; } = "Open";

	public string ReporterUid { get; set; } = "";

	public string ReporterName { get; set; } = "";

	public string TargetUid { get; set; } = "";

	public string TargetName { get; set; } = "";

	public string Reason { get; set; } = "";

	public string AssignedStaff { get; set; } = "";

	public List<PlayerReportNote> Notes { get; set; } = new List<PlayerReportNote>();

	public List<PlayerReportEvent> Events { get; set; } = new List<PlayerReportEvent>();

	public int NoteCount { get; set; }

	public int EventCount { get; set; }

	public int HistoryOffset { get; set; }

	public int HistoryLimit { get; set; }
}
