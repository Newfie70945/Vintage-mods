using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class PerformanceCenterStore
{
	public int SchemaVersion { get; set; } = 2;

	public List<PerformanceIncidentArchive> Incidents { get; set; } = new List<PerformanceIncidentArchive>();

	public List<PerformanceEntityScanSnapshot> EntityScans { get; set; } = new List<PerformanceEntityScanSnapshot>();

	public PerformanceEnvironmentSnapshot? CurrentEnvironment { get; set; }

	public List<PerformanceEnvironmentChange> EnvironmentHistory { get; set; } = new List<PerformanceEnvironmentChange>();
}
