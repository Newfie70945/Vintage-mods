using System.Collections.Generic;
using BlockLogger.Models;

namespace BlockLogger.Services;

public sealed class RollbackHistory
{
	public bool AreaMode { get; set; }

	public string PlayerUid { get; set; } = "";

	public string WorldId { get; set; } = "";

	public List<LogEventRecord> Events { get; } = new List<LogEventRecord>();

	public List<ContainerSnapshotRecord> Snapshots { get; } = new List<ContainerSnapshotRecord>();
}
