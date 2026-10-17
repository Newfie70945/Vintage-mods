namespace BlockLogger.Admin;

public sealed class CleanupReport
{
	public int LoadedEntities { get; set; }

	public int Creatures { get; set; }

	public int InanimateEntities { get; set; }

	public int DeadOrDespawning { get; set; }

	public int DroppedItems { get; set; }

	public int Removed { get; set; }

	public bool DryRun { get; set; }
}
