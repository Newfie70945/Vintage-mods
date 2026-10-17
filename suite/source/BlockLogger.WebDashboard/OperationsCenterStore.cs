using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class OperationsCenterStore
{
	public int SchemaVersion { get; set; } = 2;

	public List<ServerAlertRecord> Alerts { get; set; } = new List<ServerAlertRecord>();

	public List<ServerActionRecord> Actions { get; set; } = new List<ServerActionRecord>();

	public List<WebAuditRecord> Audit { get; set; } = new List<WebAuditRecord>();

	public List<PlayerLoadBucket> PlayerLoad { get; set; } = new List<PlayerLoadBucket>();

	public List<ConfigSnapshotRecord> ConfigSnapshots { get; set; } = new List<ConfigSnapshotRecord>();

	public List<WorldSaveObservation> WorldSaves { get; set; } = new List<WorldSaveObservation>();

	public List<NetworkPerformanceSample> NetworkSamples { get; set; } = new List<NetworkPerformanceSample>();

	public List<PerformanceSnapshotRecord> PerformanceSnapshots { get; set; } = new List<PerformanceSnapshotRecord>();

	public List<MemoryInspectorSnapshot> MemoryInspectorSnapshots { get; set; } = new List<MemoryInspectorSnapshot>();
}
