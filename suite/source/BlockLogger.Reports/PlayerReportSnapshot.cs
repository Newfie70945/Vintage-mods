using System.Collections.Generic;

namespace BlockLogger.Reports;

public sealed class PlayerReportSnapshot
{
	public long GeneratedUnixMilliseconds { get; set; }

	public int OpenCount { get; set; }

	public int InvestigatingCount { get; set; }

	public int ResolvedCount { get; set; }

	public int TotalCount { get; set; }

	public string RootCommandOwner { get; set; } = "Unknown";

	public bool RootCommandRegisteredBySuite { get; set; }

	public List<PlayerReportRecord> Reports { get; set; } = new List<PlayerReportRecord>();

	public bool StorageReady { get; set; }

	public string StorageError { get; set; } = "";
}
