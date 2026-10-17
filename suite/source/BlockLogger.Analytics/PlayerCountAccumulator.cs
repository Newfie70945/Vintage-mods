namespace BlockLogger.Analytics;

public abstract class PlayerCountAccumulator
{
	public bool HasObservation { get; set; }

	public int Low { get; set; }

	public int High { get; set; }

	public long ObservedMilliseconds { get; set; }

	public double WeightedPlayerMilliseconds { get; set; }
}
