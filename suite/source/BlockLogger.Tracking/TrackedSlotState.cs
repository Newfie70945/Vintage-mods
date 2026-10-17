namespace BlockLogger.Tracking;

public readonly record struct TrackedSlotState(string ItemCode, int StackSize)
{
	public static TrackedSlotState Empty { get; } = new TrackedSlotState(string.Empty, 0);
}
