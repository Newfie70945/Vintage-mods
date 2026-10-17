using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class MemoryInspectorSnapshot
{
	public string Id { get; set; } = "";

	public long UnixMilliseconds { get; set; }

	public string Label { get; set; } = "";

	public double ManagedHeapMb { get; set; }

	public double WorkingSetMb { get; set; }

	public double PrivateMemoryMb { get; set; }

	public double GcCommittedMb { get; set; }

	public double GcFragmentedMb { get; set; }

	public double EstimatedNativeMb { get; set; }

	public double TotalAllocatedMb { get; set; }

	public int Gen0Collections { get; set; }

	public int Gen1Collections { get; set; }

	public int Gen2Collections { get; set; }

	public int Players { get; set; }

	public int LoadedEntities { get; set; }

	public int LoadedChunks { get; set; }

	public List<MemoryInspectorTrackedStructure> Structures { get; set; } = new List<MemoryInspectorTrackedStructure>();
}
