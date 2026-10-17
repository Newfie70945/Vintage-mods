using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.Services;

public static class SuiteTimingRegistry
{
	private sealed class MetricState
	{
		public Queue<double> Recent { get; } = new Queue<double>();

		public Queue<RateBucket> RateBuckets { get; } = new Queue<RateBucket>();

		public long TotalCount { get; set; }

		public double TotalMilliseconds { get; set; }

		public double LastMilliseconds { get; set; }

		public double MaxMilliseconds { get; set; }

		public long ActiveSecond { get; set; }

		public long ActiveSecondCount { get; set; }

		public double ActiveSecondMilliseconds { get; set; }
	}

	private readonly record struct RateBucket(long UnixSecond, long Count, double Milliseconds);

	private const int WindowSize = 128;

	private const int RateWindowSeconds = 60;

	private const int MaxRateBuckets = 70;

	private static readonly object Gate = new object();

	private static readonly Dictionary<string, MetricState> Metrics = new Dictionary<string, MetricState>(StringComparer.Ordinal);

	public static void Record(string name, double milliseconds)
	{
		if (string.IsNullOrWhiteSpace(name) || double.IsNaN(milliseconds) || double.IsInfinity(milliseconds))
		{
			return;
		}
		milliseconds = Math.Max(0.0, milliseconds);
		lock (Gate)
		{
			if (!Metrics.TryGetValue(name, out MetricState value))
			{
				value = new MetricState();
				Metrics[name] = value;
			}
			value.LastMilliseconds = milliseconds;
			value.TotalCount++;
			value.TotalMilliseconds += milliseconds;
			if (milliseconds > value.MaxMilliseconds)
			{
				value.MaxMilliseconds = milliseconds;
			}
			value.Recent.Enqueue(milliseconds);
			while (value.Recent.Count > 128)
			{
				value.Recent.Dequeue();
			}
			long num = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			if (value.ActiveSecond == 0)
			{
				value.ActiveSecond = num;
			}
			if (num != value.ActiveSecond)
			{
				if (value.ActiveSecondCount > 0)
				{
					value.RateBuckets.Enqueue(new RateBucket(value.ActiveSecond, value.ActiveSecondCount, value.ActiveSecondMilliseconds));
				}
				value.ActiveSecond = num;
				value.ActiveSecondCount = 0L;
				value.ActiveSecondMilliseconds = 0.0;
				while (value.RateBuckets.Count > 0 && (num - value.RateBuckets.Peek().UnixSecond >= 60 || value.RateBuckets.Count > 70))
				{
					value.RateBuckets.Dequeue();
				}
			}
			value.ActiveSecondCount++;
			value.ActiveSecondMilliseconds += milliseconds;
		}
	}

	public static IReadOnlyList<SuiteTimingMetricSnapshot> GetSnapshot()
	{
		lock (Gate)
		{
			return (from pair in Metrics
				select BuildSnapshot(pair.Key, pair.Value) into x
				orderby x.P95Milliseconds descending, x.AverageMilliseconds descending
				select x).ThenBy((SuiteTimingMetricSnapshot x) => x.Name, StringComparer.Ordinal).ToArray();
		}
	}

	private static SuiteTimingMetricSnapshot BuildSnapshot(string name, MetricState state)
	{
		double[] array = state.Recent.ToArray();
		Array.Sort(array);
		double averageMilliseconds = ((array.Length == 0) ? 0.0 : array.Average());
		double p95Milliseconds = ((array.Length == 0) ? 0.0 : array[(int)Math.Min(array.Length - 1, Math.Ceiling((double)array.Length * 0.95) - 1.0)]);
		long num = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		long num2 = 0L;
		double num3 = 0.0;
		foreach (RateBucket rateBucket in state.RateBuckets)
		{
			if (num - rateBucket.UnixSecond < 60)
			{
				num2 += rateBucket.Count;
				num3 += rateBucket.Milliseconds;
			}
		}
		if (state.ActiveSecond > 0 && num - state.ActiveSecond < 60)
		{
			num2 += state.ActiveSecondCount;
			num3 += state.ActiveSecondMilliseconds;
		}
		return new SuiteTimingMetricSnapshot
		{
			Name = name,
			Samples = array.Length,
			TotalCount = state.TotalCount,
			LastMilliseconds = state.LastMilliseconds,
			AverageMilliseconds = averageMilliseconds,
			P95Milliseconds = p95Milliseconds,
			MaxMilliseconds = state.MaxMilliseconds,
			CallsPerMinute = num2,
			TimePerMinuteMilliseconds = num3
		};
	}
}
