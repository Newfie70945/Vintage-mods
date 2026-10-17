using BlockLogger.WebDashboard;

static void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
}

long now = 10;
var cache = new CoalescedSnapshotCache(() => now);
int builds = 0;
var first = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
Task<object> Build() { Interlocked.Increment(ref builds); return first.Task; }
var readers = Enumerable.Range(0, 64).Select(index => cache.GetAsync("page", 100, Build, out _)).ToArray();
Check(builds == 1, "64 simultaneous cold readers must schedule just one build");
first.SetResult("first");
Check((await Task.WhenAll(readers)).All(x => Equals(x, "first")), "all cold readers receive the result");
_ = cache.GetAsync("page", 100, Build, out var cached);
Check(cached && builds == 1, "fresh cache is immediate and does not rebuild");

now = 200;
var next = new TaskCompletionSource<object>();
Task<object> Refresh() { Interlocked.Increment(ref builds); return next.Task; }
var staleReaders = Enumerable.Range(0, 64).Select(index => cache.GetAsync("page", 100, Refresh, out _)).ToArray();
Check(staleReaders.All(x => x.IsCompletedSuccessfully && Equals(x.Result, "first")), "stale readers return immediately");
Check(builds == 2, "stale refreshes are coalesced");
// A write occurs after this refresh started. Its old result must not become fresh.
cache.Invalidate();
next.SetResult("outdated");
await cache.GetAsync("page", 100, () => { builds++; return Task.FromResult<object>("after write"); }, out _);
Check(builds == 3, "an invalidated in-flight build cannot mark the entry fresh");
Check(Equals(await cache.GetAsync("page", 100, Build, out _), "after write"), "post-write result cached");

int failures = 0;
var failedCache = new CoalescedSnapshotCache();
try
{
    await failedCache.GetAsync("cold", 100, () => Task.FromException<object>(new InvalidOperationException("expected")),
        out _, _ => failures++);
    throw new Exception("expected a cold-load failure");
}
catch (InvalidOperationException) { }
Check(failures == 1, "refresh errors reported once");
Check(Equals(await failedCache.GetAsync("cold", 100, () => Task.FromResult<object>("retry"), out _), "retry"),
    "failed loads release the key for retry");

cache.Invalidate();
Check(Equals(await cache.GetAsync("page", 100, Build, out _, allowRefresh: false), "after write") && builds == 3,
    "paused refreshes serve existing data without scheduling work");
Console.WriteLine("PASS: cold and stale coalescing, freshness, mutation race, failed-load retry, paused refresh");

var stopped = new CoalescedSnapshotCache(() => now);
int stoppedBuilds = 0;
try
{
    await stopped.GetAsync("cold", 100, () => { stoppedBuilds++; return Task.FromResult<object>("unexpected"); }, out _, allowRefresh: false);
    throw new Exception("a paused cold cache must reject the request");
}
catch (InvalidOperationException) { }
Check(stoppedBuilds == 0, "paused cold reads must not allocate an entry or schedule game-thread work");
var pendingBeforeStop = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
var oldRead = stopped.GetAsync("large", 100, () => pendingBeforeStop.Task, out _);
stopped.Clear();
Check(!stopped.TryGet("large", out _), "stopping the listener must release cached payloads");
Check(Equals(await stopped.GetAsync("large", 100, () => Task.FromResult<object>("reopened"), out _), "reopened"),
    "a restarted listener must build its own snapshot");
pendingBeforeStop.SetResult("stopped result");
Check(Equals(await oldRead, "stopped result"), "already waiting readers must still complete");
Check(stopped.TryGet("large", out var reopened) && Equals(reopened, "reopened"),
    "a stopped in-flight builder must not overwrite the restarted listener's snapshot");
stopped.Clear();
Check(!stopped.TryGet("large", out _), "clear must release a successfully cached payload too");
Console.WriteLine("PASS: cold pause admission, cached payload release, listener restart and late-builder isolation");

var counts = System.Text.Json.JsonSerializer.SerializeToElement(cache.GetDiagnostics());
Check(counts.GetProperty("hits").GetInt64() >= 64 && counts.GetProperty("refreshes").GetInt64() == 3, "cache counters describe actual coalescing");
var errors = System.Text.Json.JsonSerializer.SerializeToElement(failedCache.GetDiagnostics());
Check(errors.GetProperty("failures").GetInt64() == 1, "failed refresh is visible in diagnostics");
cache.Clear();
counts = System.Text.Json.JsonSerializer.SerializeToElement(cache.GetDiagnostics());
Check(counts.GetProperty("entries").GetInt32() == 0 && counts.GetProperty("clears").GetInt64() == 1, "cleanup counters distinguish lifetime activity from retained entries");
Console.WriteLine("PASS: cache diagnostics report coalesced reads, failed refreshes and cleared retained entries.");
