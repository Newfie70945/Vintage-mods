using System.Collections.Generic;

namespace BlockLogger.ServerHealth;

public sealed class ServerModPerformanceDetails
{
	public string ModId { get; set; } = "";

	public bool ProfileAvailable { get; set; }

	public long ProfileCompletedUnixMilliseconds { get; set; }

	public int ProfileDurationSeconds { get; set; }

	public int ProfileTickCount { get; set; }

	public double ProfileAverageTickMs { get; set; }

	public double ProfileP95TickMs { get; set; }

	public double ProfileP99TickMs { get; set; }

	public double ProfileMaxTickMs { get; set; }

	public ServerProfilerModHotspot? Summary { get; set; }

	public List<ServerProfilerHotspot> Hotspots { get; set; } = new List<ServerProfilerHotspot>();

	public List<ServerProfilerPhase> Phases { get; set; } = new List<ServerProfilerPhase>();

	public List<ServerProfilerOffThreadReport> OffThreadReports { get; set; } = new List<ServerProfilerOffThreadReport>();

	public List<ServerProfileSlowTick> SlowTicks { get; set; } = new List<ServerProfileSlowTick>();
}
