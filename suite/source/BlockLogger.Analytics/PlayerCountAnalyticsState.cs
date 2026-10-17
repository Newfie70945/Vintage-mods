using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.Analytics;

public sealed class PlayerCountAnalyticsState
{
	public int SchemaVersion { get; set; } = 1;

	public List<PlayerCountDailyAccumulator> Daily { get; set; } = new List<PlayerCountDailyAccumulator>();

	public List<PlayerCountHourlyAccumulator> Hourly { get; set; } = new List<PlayerCountHourlyAccumulator>();

	public PlayerCountAnalyticsState Clone()
	{
		return new PlayerCountAnalyticsState
		{
			SchemaVersion = SchemaVersion,
			Daily = Daily.Select((PlayerCountDailyAccumulator x) => new PlayerCountDailyAccumulator
			{
				Date = x.Date,
				HasObservation = x.HasObservation,
				Low = x.Low,
				High = x.High,
				ObservedMilliseconds = x.ObservedMilliseconds,
				WeightedPlayerMilliseconds = x.WeightedPlayerMilliseconds
			}).ToList(),
			Hourly = Hourly.Select((PlayerCountHourlyAccumulator x) => new PlayerCountHourlyAccumulator
			{
				Date = x.Date,
				Hour = x.Hour,
				HasObservation = x.HasObservation,
				Low = x.Low,
				High = x.High,
				ObservedMilliseconds = x.ObservedMilliseconds,
				WeightedPlayerMilliseconds = x.WeightedPlayerMilliseconds
			}).ToList()
		};
	}
}
