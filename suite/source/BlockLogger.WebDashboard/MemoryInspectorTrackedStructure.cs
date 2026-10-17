namespace BlockLogger.WebDashboard;

public sealed class MemoryInspectorTrackedStructure
{
	public string Category { get; set; } = "";

	public string Name { get; set; } = "";

	public string RuntimeType { get; set; } = "";

	public long Count { get; set; }

	public string Note { get; set; } = "";
}
