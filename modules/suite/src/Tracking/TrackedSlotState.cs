namespace BlockLogger.Tracking;

/// <summary>
/// Tiny value type used in the container hot path. Keeping this as a struct avoids
/// allocating a heap object for every slot state read/change.
/// </summary>
public readonly record struct TrackedSlotState(string ItemCode, int StackSize)
{
    public static TrackedSlotState Empty { get; } = new(string.Empty, 0);
}
