using System.Collections.Generic;

namespace BlockLogger.ServerHealth;

public sealed class ServerHealthSnapshot
{
	public bool Enabled { get; set; }

	public ServerHealthSample Current { get; set; } = new ServerHealthSample();

	public List<ServerHealthSample> Samples { get; set; } = new List<ServerHealthSample>();

	public List<ServerSlowSpike> SlowSpikes { get; set; } = new List<ServerSlowSpike>();

	public List<ServerDiagnosticIncident> DiagnosticIncidents { get; set; } = new List<ServerDiagnosticIncident>();

	public ServerDiagnosticSummary DiagnosticSummary { get; set; } = new ServerDiagnosticSummary();

	public ServerSmartAnalysis SmartAnalysis { get; set; } = new ServerSmartAnalysis();

	public List<ServerProfilerHotspot> Hotspots { get; set; } = new List<ServerProfilerHotspot>();

	public List<ServerProfilerModHotspot> ModHotspots { get; set; } = new List<ServerProfilerModHotspot>();

	public List<ServerProfilerPhase> Phases { get; set; } = new List<ServerProfilerPhase>();

	public List<ServerProfileSlowTick> ProfileSlowTicks { get; set; } = new List<ServerProfileSlowTick>();

	public List<ServerProfilerOffThreadReport> OffThreadReports { get; set; } = new List<ServerProfilerOffThreadReport>();

	public int LoadedModCount { get; set; }

	public bool ProfileRunning { get; set; }

	public int ProfileDurationSeconds { get; set; }

	public int ProfileRemainingSeconds { get; set; }

	public bool AutomaticIncidentProfilingEnabled { get; set; }

	public int AutomaticIncidentProfileDurationSeconds { get; set; }

	public int AutomaticIncidentProfileCooldownSeconds { get; set; }

	public long LastAutomaticIncidentProfileUnixMilliseconds { get; set; }

	public long LastAutomaticIncidentTriggerUnixMilliseconds { get; set; }

	public double LastAutomaticIncidentTriggerDelayMs { get; set; }

	public ServerProfileSummary LastProfile { get; set; } = new ServerProfileSummary();
}
