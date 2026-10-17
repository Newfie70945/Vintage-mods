using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class OptimizationExperimentStore
{
	public int SchemaVersion { get; set; } = 2;

	public OptimizationExperiment? Pending { get; set; }

	public List<OptimizationExperiment> History { get; set; } = new List<OptimizationExperiment>();
}
