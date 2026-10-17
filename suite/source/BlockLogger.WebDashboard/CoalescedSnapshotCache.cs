using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BlockLogger.WebDashboard;

internal sealed class CoalescedSnapshotCache
{
	private sealed class Entry
	{
		public object? Payload;

		public long CapturedAt;

		public bool Stale;

		public long Generation;

		public Task<object>? Pending;

		public long LastAccess;
	}

	private readonly object gate = new object();

	private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

	private readonly Func<long> clock;

	private long accessSequence;

	private long hits;

	private long misses;

	private long refreshes;

	private long failures;

	private long evictions;

	private long clears;

	internal const int Capacity = 128;

	public CoalescedSnapshotCache(Func<long>? clock = null)
	{
		this.clock = clock ?? ((Func<long>)(() => Environment.TickCount64));
	}

	public bool TryGet(string key, out object? payload)
	{
		lock (gate)
		{
			payload = (entries.TryGetValue(key, out Entry value) ? value.Payload : null);
			return payload != null;
		}
	}

	public Task<object> GetAsync(string key, int maxAgeMs, Func<Task<object>> builder, out bool servedCached, Action<Exception>? onRefreshError = null, bool allowRefresh = true)
	{
		TaskCompletionSource<object> taskCompletionSource = null;
		long generation = 0L;
		Entry value2;
		Task<object> result;
		lock (gate)
		{
			if (!allowRefresh && (!entries.TryGetValue(key, out Entry value) || value.Payload == null))
			{
				servedCached = false;
				return Task.FromException<object>(new InvalidOperationException("Snapshot refresh is paused; retry when the panel is enabled."));
			}
			if (!entries.TryGetValue(key, out value2))
			{
				if (entries.Count >= 128)
				{
					KeyValuePair<string, Entry> keyValuePair = entries.Where((KeyValuePair<string, Entry> p) => p.Value.Pending == null).MinBy((KeyValuePair<string, Entry> p) => p.Value.LastAccess);
					if (keyValuePair.Key == null)
					{
						throw new InvalidOperationException("Snapshot cache is busy; retry shortly.");
					}
					entries.Remove(keyValuePair.Key);
					evictions++;
				}
				value2 = (entries[key] = new Entry());
			}
			value2.LastAccess = ++accessSequence;
			servedCached = value2.Payload != null;
			if (servedCached)
			{
				hits++;
			}
			else
			{
				misses++;
			}
			bool flag = value2.Stale || Math.Max(0L, clock() - value2.CapturedAt) > maxAgeMs;
			if ((!servedCached || (flag & allowRefresh)) && value2.Pending == null)
			{
				refreshes++;
				taskCompletionSource = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
				value2.Pending = taskCompletionSource.Task;
				generation = value2.Generation;
			}
			result = (servedCached ? Task.FromResult(value2.Payload) : value2.Pending);
		}
		if (taskCompletionSource != null)
		{
			RefreshAsync(value2, generation, taskCompletionSource, builder, onRefreshError);
		}
		return result;
	}

	private async Task RefreshAsync(Entry entry, long generation, TaskCompletionSource<object> completion, Func<Task<object>> builder, Action<Exception>? onError)
	{
		try
		{
			object payload = await builder().ConfigureAwait(continueOnCapturedContext: false);
			lock (gate)
			{
				if (entry.Generation == generation)
				{
					entry.Payload = payload;
					entry.CapturedAt = clock();
					entry.Stale = false;
				}
				entry.Pending = null;
			}
			completion.TrySetResult(payload);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			lock (gate)
			{
				entry.Pending = null;
				failures++;
			}
			completion.TrySetException(ex2);
			_ = completion.Task.Exception;
			try
			{
				onError?.Invoke(ex2);
			}
			catch
			{
			}
		}
	}

	public void Invalidate(Func<string, bool>? affected = null)
	{
		lock (gate)
		{
			foreach (KeyValuePair<string, Entry> entry in entries)
			{
				if (affected == null || affected(entry.Key))
				{
					Entry value = entry.Value;
					value.Generation++;
					value.Stale = true;
				}
			}
		}
	}

	public object GetDiagnostics()
	{
		lock (gate)
		{
			long now = clock();
			return new
			{
				entries = entries.Count,
				capacity = 128,
				pending = entries.Values.Count((Entry e) => e.Pending != null),
				hits = hits,
				misses = misses,
				refreshes = refreshes,
				failures = failures,
				evictions = evictions,
				clears = clears,
				keys = (from p in entries
					orderby p.Key
					select new
					{
						key = p.Key,
						hasPayload = (p.Value.Payload != null),
						pending = (p.Value.Pending != null),
						stale = p.Value.Stale,
						ageMs = ((p.Value.Payload == null) ? ((long?)null) : new long?(Math.Max(0L, now - p.Value.CapturedAt)))
					}).ToArray(),
				memoryNote = "Entry counts and refresh lifetimes are measured; object graph bytes require a heap capture."
			};
		}
	}

	public void Clear()
	{
		lock (gate)
		{
			entries.Clear();
			clears++;
		}
	}
}
