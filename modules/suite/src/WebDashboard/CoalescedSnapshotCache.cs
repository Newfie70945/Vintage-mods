namespace BlockLogger.WebDashboard;

// Fixed, server-owned keys only. Cold requests and stale refreshes share one builder
// per key, so multiple browsers cannot multiply game-thread snapshot work.
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

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Func<long> clock;
    private long accessSequence;
    private long hits, misses, refreshes, failures, evictions, clears;
    internal const int Capacity = 128;

    public CoalescedSnapshotCache(Func<long>? clock = null)
    {
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    public bool TryGet(string key, out object? payload)
    {
        lock (gate)
        {
            payload = entries.TryGetValue(key, out var entry) ? entry.Payload : null;
            return payload != null;
        }
    }

    public Task<object> GetAsync(string key, int maxAgeMs, Func<Task<object>> builder,
        out bool servedCached, Action<Exception>? onRefreshError = null, bool allowRefresh = true, bool waitForRefresh = false)
    {
        Entry entry;
        TaskCompletionSource<object>? completion = null;
        Task<object> result;
        long generation = 0;
        lock (gate)
        {
            if (!allowRefresh && (!entries.TryGetValue(key, out var paused) || paused.Payload == null))
            {
                servedCached = false;
                return Task.FromException<object>(new InvalidOperationException("Snapshot refresh is paused; retry when the panel is enabled."));
            }
            if (!entries.TryGetValue(key, out entry!))
            {
                if (entries.Count >= Capacity)
                {
                    var victim = entries.Where(p => p.Value.Pending == null).MinBy(p => p.Value.LastAccess);
                    if (victim.Key == null) throw new InvalidOperationException("Snapshot cache is busy; retry shortly.");
                    entries.Remove(victim.Key);
                    evictions++;
                }
                entries[key] = entry = new Entry();
            }
            entry.LastAccess = ++accessSequence;
            servedCached = entry.Payload != null;
            if (servedCached) hits++; else misses++;
            bool expired = entry.Stale || Math.Max(0, clock() - entry.CapturedAt) > maxAgeMs;
            if ((!servedCached || (expired && allowRefresh)) && entry.Pending == null)
            {
                refreshes++;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                entry.Pending = completion.Task;
                generation = entry.Generation;
            }
            // Performance reads await the shared replacement instead of requiring a
            // second click. Other routes retain stale-while-refresh behavior.
            bool awaitReplacement = waitForRefresh && allowRefresh && expired;
            result = servedCached && !awaitReplacement ? Task.FromResult(entry.Payload!) : entry.Pending!;
            if (awaitReplacement) servedCached = false;
        }

        // Reserve under the lock, but schedule outside it: builders may complete
        // synchronously or invoke callbacks which read the cache again.
        if (completion != null)
            _ = RefreshAsync(entry, generation, completion, builder, onRefreshError);
        return result;
    }

    private async Task RefreshAsync(Entry entry, long generation,
        TaskCompletionSource<object> completion, Func<Task<object>> builder, Action<Exception>? onError)
    {
        try
        {
            object payload = await builder().ConfigureAwait(false);
            lock (gate)
            {
                // A mutation may invalidate the cache while a build is in flight.
                // That older build must never mark the post-mutation cache fresh.
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
            lock (gate) { entry.Pending = null; failures++; }
            completion.TrySetException(ex);
            // Stale readers return immediately and do not await refresh failures.
            // Observe the task while retaining the exception for cold waiters.
            _ = completion.Task.Exception;
            try { onError?.Invoke(ex); } catch { }
        }
    }

    public void Invalidate(Func<string, bool>? affected = null)
    {
        lock (gate)
        {
            foreach (var pair in entries)
            {
                if (affected != null && !affected(pair.Key)) continue;
                var entry = pair.Value;
                entry.Generation++;
                entry.Stale = true;
            }
        }
    }

    public object GetDiagnostics()
    {
        lock (gate)
        {
            long now = clock();
            return new { entries = entries.Count, capacity = Capacity, pending = entries.Values.Count(e => e.Pending != null), hits, misses, refreshes, failures, evictions, clears,
                keys = entries.OrderBy(p => p.Key).Select(p => new { key = p.Key, hasPayload = p.Value.Payload != null, pending = p.Value.Pending != null, stale = p.Value.Stale, ageMs = p.Value.Payload == null ? (long?)null : Math.Max(0, now - p.Value.CapturedAt) }).ToArray(),
                memoryNote = "Entry counts and refresh lifetimes are measured; object graph bytes require a heap capture." };
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            // In-flight builders keep their own Entry, so a late completion cannot
            // repopulate the dictionary or overwrite a newly opened panel's data.
            entries.Clear();
            clears++;
        }
    }
}
