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

var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
var counted = new CountingPayload();
var serialized = new SerializedSnapshot(counted);
var concurrentBytes = await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() => serialized.GetBytes(options, out _))));
Check(counted.Reads == 1 && concurrentBytes.All(x => ReferenceEquals(x, concurrentBytes[0])), "concurrent snapshot readers must share one serialized body");
counted.Value = "changed";
Check(ReferenceEquals(serialized.GetBytes(options, out bool reused), concurrentBytes[0]) && reused, "existing snapshot must retain its byte identity");
var replacement = new SerializedSnapshot(counted).GetBytes(options, out bool firstReuse);
Check(!firstReuse && System.Text.Encoding.UTF8.GetString(replacement).Contains("changed"), "new snapshot must serialize independently after invalidation");
var huge = new SerializedSnapshot(new { text = new string('x', SerializedSnapshot.MaxRetainedBytes + 1) });
var huge1 = huge.GetBytes(options, out _);
var huge2 = huge.GetBytes(options, out var hugeReused);
Check(!hugeReused && !ReferenceEquals(huge1, huge2), "oversized responses must bypass byte retention");
var flakyPayload = new CountingPayload { Throw = true };
var flaky = new SerializedSnapshot(flakyPayload);
try { flaky.GetBytes(options, out _); throw new Exception("expected serialization failure"); } catch (InvalidOperationException) { }
flakyPayload.Throw = false;
Check(flaky.GetBytes(options, out _).Length > 0, "failed serialization must release lock and allow retry");
var benchPayload = new { text = new string('x', 25000) };
var bench = new SerializedSnapshot(benchPayload);
bench.GetBytes(options, out _);
long allocated = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 1000; i++) bench.GetBytes(options, out _);
long cachedAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
allocated = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 1000; i++) System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(benchPayload, options);
long uncachedAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
Check(cachedAllocation < uncachedAllocation / 10, "snapshot byte reuse must materially reduce repeated serialization allocations");
Console.WriteLine($"PASS: shared serialization, refreshed identity, bounded bytes, failure retry; 1000 warm reads allocated {cachedAllocation:N0} B vs {uncachedAllocation:N0} B uncached.");

WebRequestDiagnostic Request(long start, long end, double wall, double gc = 0) => new()
{
    UnixMilliseconds = start, EndUnixMilliseconds = end,
    WorkWindows = new() { new() { StartUnixMilliseconds = start, EndUnixMilliseconds = end, WallMilliseconds = wall, GcPauseMilliseconds = gc } }
};
var overlapping = Request(9500, 10300, 800, 600);
Check(WebIncidentEvidence.WorkMilliseconds(overlapping, 10000, 11000, 500) == 200, "coincident GC must be discounted from callback evidence");
Check(WebIncidentEvidence.WorkMilliseconds(Request(20000, 21000, 1000), 10000, 11000, 500) == 0, "unrelated callback must not be blamed");
Check(WebIncidentEvidence.WorkMilliseconds(new() { MainThreadWorkMilliseconds = 10000 }, 10000, 11000, 500) == 0, "old aggregate-only work must remain correlation, not measured overlap");
Check(WebIncidentEvidence.WorkMilliseconds(Request(0, 10000, 10000), 10000, 11000, 500) == 1500, "partial callback overlap must be clipped to the incident window");
Check(WebIncidentEvidence.WorkMilliseconds(Request(0, 10000, 10000, 9000), 10000, 11000, 500) == 0, "partial overlap cannot assume GC occurred outside the overlapping interval");
var spanning = new WebRequestDiagnostic { UnixMilliseconds = 100, EndUnixMilliseconds = 11000, QueueWaitMilliseconds = 10900 };
Check(WebIncidentEvidence.Select(new[] { spanning }, 10000, 11000, 500).Count == 1, "long-running request must match by interval, not start proximity");
Check(WebIncidentEvidence.WorkMilliseconds(spanning, 10000, 11000, 500) == 0, "queue wait must not be executing work");
Check(WebIncidentEvidence.Select(Enumerable.Range(0, 30).Select(i => new WebRequestDiagnostic { RequestId = i, UnixMilliseconds = 10000, EndUnixMilliseconds = 11000 }), 10000, 11000, 500).Count == 8, "incident web evidence must be bounded");
Console.WriteLine("PASS: interval matching, GC discount, legacy evidence, wait-only requests and retained evidence bounds.");

// Performance reads must publish the refresh initiated by this request, not an
// expired snapshot which would require another button click to retrieve.
long freshNow=100;int freshBuilds=0;
var freshCache=new CoalescedSnapshotCache(()=>freshNow);
await freshCache.GetAsync("timeline",100,()=>Task.FromResult<object>("old"),out _);
freshNow=250;
var freshReplacement=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
Task<object> FreshBuilder(){freshBuilds++;return freshReplacement.Task;}
var freshReaders=Enumerable.Range(0,64).Select(index=>freshCache.GetAsync("timeline",100,FreshBuilder,out _,waitForRefresh:true)).ToArray();
Check(freshBuilds==1&&freshReaders.All(t=>!t.IsCompleted),"expired Performance readers must await one shared freshReplacement");
freshReplacement.SetResult("new");
Check((await Task.WhenAll(freshReaders)).All(v=>Equals(v,"new")),"first refresh must return new graph data without a second click");
Check(Equals(await freshCache.GetAsync("timeline",100,()=>throw new Exception("fresh snapshot rebuilt"),out bool hit,waitForRefresh:true),"new")&&hit,"fresh Performance data retains its cache lifetime");
freshNow=400;
try{await freshCache.GetAsync("timeline",100,()=>Task.FromException<object>(new IOException("busy")),out _,waitForRefresh:true);throw new Exception("expired refresh failure was hidden");}catch(IOException){}
Check(freshCache.TryGet("timeline",out var retained)&&Equals(retained,"new"),"failed fresh refresh preserves last successful snapshot");
Check(Equals(await freshCache.GetAsync("timeline",100,()=>throw new Exception("paused refresh ran"),out _,allowRefresh:false,waitForRefresh:true),"new"),"paused Performance cache does not start work");
Console.WriteLine("PASS: first-click fresh Performance snapshots, 64-reader coalescing, cached fresh hits, visible refresh failure and paused-cache preservation");

sealed class CountingPayload
{
    public int Reads;
    public bool Throw;
    public string Value = "original";
    public string Text { get { Interlocked.Increment(ref Reads); if (Throw) throw new InvalidOperationException("expected"); return Value; } }
}
