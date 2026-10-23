using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace NewfiesOptimizer;

public sealed class RuntimeProfiler : IDisposable
{
    public const string HarmonyId = "newfies.serveroptimizer.profiling.0.9.0";
    private static RuntimeProfiler? active;
    public Func<ModuleStatus[]>? ExternalModules { get; set; }
    public Func<IReadOnlyDictionary<string,long>>? ExternalCounters { get; set; }
    private readonly ICoreServerAPI api;
    private readonly ServerMain? server;
    private readonly OptimizerConfig config;
    private readonly Harmony harmony = new(HarmonyId);
    private readonly Dictionary<MethodBase, string> hooks = new();
    private readonly ConcurrentDictionary<MethodBase, HotspotCounter> timingCounters = new();
    private readonly Dictionary<Type, FieldInfo[]> callbackFields = new();
    private readonly HashSet<string> blockedHookGroups = new();
    private readonly Dictionary<string, ModuleStatus> modules = new();
    private readonly Process process = Process.GetCurrentProcess();
    private readonly Func<int>? chunkCount;
    private readonly string outputDirectory;
    private long listenerId;
    private long lastTickCount, sent, received, allocated;
    private int gc0, gc1, gc2;
    private double previousElapsed, previousCpuMs, previousGcPause, previousWorkMs, lastConflictCheck, lastPresence;
    private int mainNativeThreadId;
    private double? previousMainCpuMs;
    private Task? export;
    private int exportSerial;
    private bool disposed;
    private SessionEvidence? evidence;
    private readonly MethodInfo? processMethod = typeof(ServerMain).GetMethod(nameof(ServerMain.Process));
    private readonly object diagnosticsGate = new();
    private int heartbeatFailures;
    public ProfileSession? Session { get; private set; }
    public string? LastExport { get; private set; }
    public string? ExportFailure { get; private set; }
    public bool IsRunning => Session?.Running == true;

    public RuntimeProfiler(ICoreServerAPI api, OptimizerConfig config)
    {
        this.api = api; this.config = config; server = api.World as ServerMain;
        outputDirectory = api.GetOrCreateDataPath("NewfiesOptimizer");
        if (server is not null)
        {
            // Inspect a single count field; never clone AllLoadedChunks or enumerate terrain for telemetry.
            if (AccessTools.Field(typeof(ServerMain), "loadedChunks")?.GetValue(server) is System.Collections.IDictionary dictionary)
                chunkCount = () => dictionary.Count;
        }
        foreach (string area in new[] { "entityOptimization", "chunkOptimization", "blockEntities", "memoryManagement", "networking", "scheduling" })
            modules[area] = area switch
            {
                "entityOptimization" => new(area, "scopeLimited", "Entity search, AI delegate and repulsion allocation reductions are separate engine modules. Entity activation and AI tick cadence are unchanged."),
                "memoryManagement" => new(area, "scopeLimited", "Enabled allocation modules reduce temporary objects. Stopped captures can be released explicitly. No forced GC, heap compaction or global latency-mode changes."),
                "scheduling" => new(area, "scopeLimited", "Optional bounded main-thread pathfinding assistance is a separate module. Off by default; changes path scheduling, not the A* algorithm."),
                _ => new(area, "notImplemented", "This gameplay area is not modified by the implemented engine modules.")
            };
        SetModule("tickMetrics", config.Profiling.TickMetrics ? "idle" : "disabled", "No hook while profiling is stopped.");
        SetModule("systemTimings", config.Profiling.SystemTimings ? "idle" : "disabled", "Optional sampled inclusive engine-system timing.");
        SetModule("phaseTimings", config.Profiling.PhaseTimings ? "idle" : "disabled", "Optional inspected main-thread phases only.");
        SetModule("callbackTimings", config.Profiling.CallbackTimings ? "idle" : "disabled", "Optional sampled callback attribution, bounded by MaxHotspots.");
        if (config.Benchmarking.WriteSessionEvidence)
        {
            evidence = new(outputDirectory, config.Benchmarking.MaxEvidenceBytes);
            api.Event.PlayerNowPlaying += PlayerConnected;
            api.Event.PlayerDisconnect += PlayerDisconnected;
        }
        listenerId = api.Event.RegisterGameTickListener(Heartbeat, 1000);
    }

    public string Start()
    {
        if (disposed) return "Profiler disposed.";
        if (!config.Enabled) return "Optimizer disabled in optimizer.json.";
        if (IsRunning) return "A capture is already running. Stop/export it before starting another.";
        if (EngineVersion.Actual != "1.22.7")
            return "Unsupported server version " + EngineVersion.Actual + "; no hooks installed.";
        if (server is null) return "Official ServerMain unavailable; no hooks installed.";
        Session = new(config.Profiling.MaxSamples, config.Profiling.MaxHotspots);
        blockedHookGroups.Clear(); timingCounters.Clear();
        previousElapsed = 0; lastTickCount = 0; previousWorkMs = 0; lastConflictCheck = 0; heartbeatFailures = 0;
        sent = ReadSent(); received = ReadReceived(); allocated = GC.GetTotalAllocatedBytes(false);
        gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
        previousGcPause = GC.GetTotalPauseDuration().TotalMilliseconds;
        process.Refresh(); previousCpuMs = process.TotalProcessorTime.TotalMilliseconds;
        previousMainCpuMs = null; mainNativeThreadId = 0;
        active = this;
        if (config.Profiling.TickMetrics && processMethod is not null) InstallHook(processMethod, "tickMetrics", nameof(ProcessPrefix), nameof(ProcessPostfix));
        else if (config.Profiling.TickMetrics) SetModule("tickMetrics", "failed", "Official Process method unavailable; process telemetry continues without engine hook.");
        if (!config.Compatibility.SafeMode)
        {
            if (config.Profiling.SystemTimings) InstallOptionalGroup("systemTimings", InstallSystemHooks);
            if (config.Profiling.PhaseTimings) InstallOptionalGroup("phaseTimings", InstallPhaseHooks);
            if (config.Profiling.CallbackTimings) InstallOptionalGroup("callbackTimings", InstallCallbackHooks);
        }
        else foreach (string group in new[] { "systemTimings", "phaseTimings", "callbackTimings" })
            SetModule(group, "disabled", "Compatibility.SafeMode skips optional detailed timing hooks. Set false explicitly on a test server to enable configured hooks.");
        return "Capture started. Bounded telemetry is sampled every " + config.Profiling.SampleIntervalSeconds + "s. Engine optimization switches are independent of profiler capture.";
    }

    public string Stop(bool exportReport = true)
    {
        if (!IsRunning) return "No running capture. Existing captured data is retained for report/export.";
        // Capture the final interval before freezing; Hook tick counters remain actual completed ticks.
        try { TrySample(force: true); }
        catch (Exception e) { SetModule("sampling", "failed", "Final sample unavailable: " + e.GetType().Name + ". Partial measurements retained."); }
        finally { Session!.Stop(); RemoveOwnHooks(); }
        string exportResult = exportReport && config.Profiling.ExportOnStop ? QueueExport() : "Use /optimizer export to write JSON/CSV.";
        return "Capture stopped. Data retained. " + exportResult;
    }

    public (bool Success, string Message) ClearCapture()
    {
        if (IsRunning) return (false, "Stop the NOS capture before clearing it.");
        Session = null;
        timingCounters.Clear(); blockedHookGroups.Clear();
        return (true, "Stopped capture released. Existing exported files are unchanged; any export in progress owns its report snapshot. No forced collection was requested.");
    }

    public string QueueExport()
    {
        if (Session is null) return "No capture to export. Run /optimizer profile start first.";
        if (export is { IsCompleted: false }) return "One export is already running; no extra export was queued.";
        var snapshot = Snapshot(); // Bounded snapshot on the owning thread; serialization and disk I/O are off-thread.
        string stem = "profile-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + ++exportSerial;
        ExportFailure = null;
        export = Task.Run(() =>
        {
            try { ReportExporter.Write(outputDirectory, stem, snapshot, config.Logging.MaxExports); LastExport = stem + ".json / .csv"; }
            catch (Exception e) { ExportFailure = e.GetType().Name + ": " + e.Message; }
        });
        return "Export queued in NewfiesOptimizer/ (one concurrent export maximum).";
    }

    public ProfileReport Snapshot() => (Session?.Snapshot(Capabilities(), GetModules(), EngineVersion.Actual)
        ?? throw new InvalidOperationException("No capture.")) with
        { EngineCounters = ExternalCounters?.Invoke() ?? new Dictionary<string,long>() };

    public string Status()
    {
        string summary = "Newfie's Server Optimizer 0.9.0 preview | " + (IsRunning ? "recording" : "stopped") + " | game " + EngineVersion.Actual + "; engine module activation is listed separately.";
        if (Session is not null) summary += $" Capture {Session.ElapsedSeconds:F1}s, {Session.TickCount} completed ticks.";
        if (LastExport is not null) summary += " Last export: " + LastExport + ".";
        if (ExportFailure is not null) summary += " Export failed: " + ExportFailure + ".";
        if (evidence is not null) summary += $" Evidence dropped={evidence.Dropped}; " + (evidence.Failure ?? "session evidence enabled (opt-in)") + ".";
        return summary + "\n" + string.Join("\n", GetModules().Where(m => m.Status is "conflict" or "failed").Select(m => m.Name + ": " + m.Reason));
    }

    public MetricCapability[] Capabilities() => [
        new("tickProcessing", HookUsable("tickMetrics"), "Official pre-sleep millisecond tick work, not entire Process call including sleep; unavailable if hook disabled/conflicted."),
        new("processCpuMemoryGc", config.Profiling.ProcessMetrics, "Process CPU, GC allocation/collection/total pause counters and process memory sampled at configured cadence."),
        new("mainThreadCpu", config.Profiling.MainThreadCpu && mainNativeThreadId != 0 && previousMainCpuMs.HasValue, "Optional OS thread CPU; Linux/Windows only; null if thread counters unavailable."),
        new("worldCounts", config.Profiling.WorldCounts, "Connected Playing clients, loaded entities and optional inspected dictionary Count; no chunk copies or entity walks."),
        new("loadedChunks", config.Profiling.WorldCounts && chunkCount is not null, "Internal 1.22.7 loadedChunks dictionary count, sampled without cloning or retaining terrain objects."),
        new("networkPayloadCounters", config.Profiling.NetworkCounters, "Engine aggregate sent/received TCP+UDP payload byte counters; excludes OS framing and per-player rates."),
        new("systemTimings", HookUsable("systemTimings"), "Optional sampled inclusive ServerSystem.OnServerTick methods, identified by engine type."),
        new("phaseTimings", HookUsable("phaseTimings"), "Optional entity tick loop, callback dispatch, autosave preparation and main-thread packet dispatch; inclusive samples."),
        new("registeredCallbackCosts", HookUsable("callbackTimings"), "Optional inspected official callback wrappers, attributed by callback assembly/type/method; bounded unique methods."),
        new("aiAndPathfindingBreakdown", false, "Entity loop includes AI but per-AI/worker timing is deferred pending representative profiling and hook overhead evidence."),
        new("chunkGenerationAndIoTimings", false, "Worker-thread chunk generation, serialization and disk completions are not instrumented; queued chunk-column count is available."),
        new("blockEntityIndividualCosts", false, "Callbacks can be sampled by method with callbackTimings; individual block identities are not recorded."),
        new("fullSaveDuration", false, "Optional autosave preparation timing excludes asynchronous serialization/disk completion; no full-save latency claim."),
        new("packetQueuesAndPlayerSync", false, "Engine payload counters and optional inclusive packet dispatch do not measure queue latency or per-player synchronization."),
        new("correctnessAndThroughput", false, "Requires external authoritative scenario evidence, actual clients and before/after world validation."),
        new("sessionEvidence", evidence is not null && evidence.Failure is null && evidence.Dropped == 0, "Optional opt-in Playing-session UID events only; no automated interaction success is asserted. Dropped/capped/failed evidence is incomplete.")
    ];

    private ModuleStatus[] GetModules()
    {
        lock (diagnosticsGate)
        {
            var values = modules.Values.ToList();
            if (ExternalModules != null) values.AddRange(ExternalModules());
            if (evidence is not null) values.Add(new("sessionEvidence", evidence.Failure is null && evidence.Dropped == 0 ? "active" : "failed", $"Dropped events: {evidence.Dropped}; " + (evidence.Failure ?? "Opt-in evidence; presence capped at 200 Playing clients.")));
            return values.OrderBy(m => m.Name).ToArray();
        }
    }
    private void SetModule(string name, string status, string reason) { lock (diagnosticsGate) modules[name] = new(name, status, reason); }
    private bool HookUsable(string name) { lock (diagnosticsGate) return modules.TryGetValue(name, out var m) && m.Status is "active" or "captured"; }

    private void InstallHook(MethodBase method, string module, string prefix, string postfix)
    {
        if (blockedHookGroups.Contains(module)) return;
        try
        {
            var info = Harmony.GetPatchInfo(method);
            string[] owners = info?.Owners.Where(x => x != HarmonyId).Distinct().ToArray() ?? [];
            if (owners.Length > 0 && config.Compatibility.DisableHookOnConflict)
            {
                DisableHookGroup(module, "conflict", method.DeclaringType?.Name + "." + method.Name + ": " + string.Join(", ", owners)); return;
            }
            if (prefix == nameof(TimingPrefix))
            {
                string name = method.DeclaringType!.Name + "." + method.Name;
                var counter = Session!.FindHotspot(method, name, Category(name));
                if (counter is null) { DisableHookGroup(module, "failed", "Hotspot limit too small for requested timing hooks."); return; }
                timingCounters[method] = counter;
            }
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(RuntimeProfiler), prefix), postfix: new HarmonyMethod(typeof(RuntimeProfiler), postfix));
            hooks[method] = module;
            SetModule(module, "active", "Observational hooks installed; " + (owners.Length > 0 ? "other owners explicitly allowed: " + string.Join(",", owners) : "no other Harmony owners detected"));
        }
        catch (Exception e) { DisableHookGroup(module, "failed", method.Name + ": " + e.GetType().Name + "; stock engine method remains active."); }
    }

    private void DisableHookGroup(string module, string status, string reason)
    {
        blockedHookGroups.Add(module);
        foreach (var pair in hooks.Where(p => p.Value == module).ToArray())
        {
            harmony.Unpatch(pair.Key, HarmonyPatchType.All, HarmonyId); hooks.Remove(pair.Key);
            timingCounters.TryRemove(pair.Key, out _);
        }
        SetModule(module, status, "Entire timing group disabled: " + reason);
    }

    private void InstallSystemHooks()
    {
        var methods = EngineHookTargets.SystemMethods(server!);
        if (methods.Length == 0) throw new MissingMethodException("No concrete engine system tick methods.");
        foreach (var method in methods) InstallHook(method, "systemTimings", nameof(TimingPrefix), nameof(TimingPostfix));
    }
    private void InstallPhaseHooks()
    {
        foreach (var method in EngineHookTargets.PhaseMethods()) InstallHook(method, "phaseTimings", nameof(TimingPrefix), nameof(TimingPostfix));
    }
    private void InstallCallbackHooks()
    {
        foreach (var method in EngineHookTargets.CallbackMethods())
        {
            var type = method.DeclaringType!;
            callbackFields[type] = new[] { AccessTools.Field(type, "HandlerBare"), AccessTools.Field(type, "Handler") }.Where(f => f is not null).Cast<FieldInfo>().ToArray();
            if (callbackFields[type].Length == 0) throw new MissingFieldException(type.FullName, "Handler/HandlerBare");
            InstallHook(method, "callbackTimings", nameof(CallbackPrefix), nameof(TimingPostfix));
        }
    }

    private void InstallOptionalGroup(string group, Action install)
    {
        try { install(); }
        catch (Exception e) { DisableHookGroup(group, "failed", "Target discovery/installation failed: " + e.GetType().Name + ". Other independent profiler groups continue."); }
        lock (diagnosticsGate)
            if (modules.TryGetValue(group, out var status) && status.Status == "idle")
                modules[group] = new(group, "failed", "No timing targets installed; measurements unavailable.");
    }

    private void CheckConflicts()
    {
        if (!config.Compatibility.DisableHookOnConflict) return;
        foreach (var pair in hooks.ToArray())
        {
            var owners = Harmony.GetPatchInfo(pair.Key)?.Owners.Where(o => o != HarmonyId).ToArray() ?? [];
            if (owners.Length == 0) continue;
            DisableHookGroup(pair.Value, "conflict", "Late Harmony owner detected; group disabled until next capture: " + pair.Key.Name + ": " + string.Join(",", owners));
        }
    }
    private void RemoveOwnHooks()
    {
        try { harmony.UnpatchAll(HarmonyId); }
        catch (Exception e) { SetModule("cleanup", "failed", "Could not remove profiling patches: " + e.GetType().Name + "; observers disabled."); }
        finally { hooks.Clear(); timingCounters.Clear(); if (active == this) active = null; }
        foreach (var module in new[] { "tickMetrics", "systemTimings", "phaseTimings", "callbackTimings" })
        {
            lock (diagnosticsGate) if (modules.TryGetValue(module, out var value) && value.Status == "active") modules[module] = value with { Status = "captured", Reason = "Capture stopped; hooks removed, measurements retained." };
        }
        if (active == this) active = null;
    }

    private void Heartbeat(float unused)
    {
        if (disposed) return;
        try
        {
            if (evidence is not null && server is not null && (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds - lastPresence >= config.Benchmarking.PresenceIntervalSeconds)
            {
                lastPresence = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
                int count = 0;
                foreach (var c in server.Clients.Values)
                    if (c.Player is not null && c.IsPlayingClient && count++ < 200) evidence.Emit("presence", c.Player.PlayerUID, config.Benchmarking.RunId, server.Config.VerifyPlayerAuth);
            }
            if (!IsRunning) return;
            TrySample(force: false);
            if (Session!.ElapsedSeconds - lastConflictCheck >= 10) { lastConflictCheck = Session.ElapsedSeconds; CheckConflicts(); }
            if (config.Profiling.AutoStopSeconds > 0 && Session.ElapsedSeconds >= config.Profiling.AutoStopSeconds) Stop();
        }
        catch (Exception e)
        {
            if (++heartbeatFailures == 1) api.Logger.Warning("[NewfiesOptimizer] Sampling failed ({0}); engine gameplay continues.", e.GetType().Name);
            SetModule("sampling", "failed", e.GetType().Name + "; capture disabled after three failures.");
            if (heartbeatFailures >= 3 && IsRunning) { Session!.Stop(); RemoveOwnHooks(); }
        }
    }

    private void TrySample(bool force)
    {
        var s = Session;
        if (s?.Running != true) return;
        double elapsed = s.ElapsedSeconds;
        double interval = elapsed - previousElapsed;
        if (interval <= .001 || (!force && interval < config.Profiling.SampleIntervalSeconds)) return;
        long workStart = Stopwatch.GetTimestamp();
        long ticks = s.TickCount;
        var info = GC.GetGCMemoryInfo();
        long allocatedNow = GC.GetTotalAllocatedBytes(false);
        long sentNow = ReadSent(), receivedNow = ReadReceived();
        int gc0Now = GC.CollectionCount(0), gc1Now = GC.CollectionCount(1), gc2Now = GC.CollectionCount(2);
        double pause = GC.GetTotalPauseDuration().TotalMilliseconds;
        double cpuMs = previousCpuMs;
        double? mainCpu = null;
        long? workingSet = null, privateBytes = null;
        if (config.Profiling.ProcessMetrics)
        {
            process.Refresh(); cpuMs = process.TotalProcessorTime.TotalMilliseconds;
            workingSet = process.WorkingSet64; privateBytes = process.PrivateMemorySize64;
        }
        if (!config.Compatibility.SafeMode && config.Profiling.MainThreadCpu && mainNativeThreadId != 0)
        {
            try
            {
                foreach (ProcessThread thread in process.Threads)
                {
                    using (thread)
                    {
                        if (thread.Id != mainNativeThreadId) continue;
                        double cpu = thread.TotalProcessorTime.TotalMilliseconds;
                        if (previousMainCpuMs.HasValue) mainCpu = Math.Max(0, cpu - previousMainCpuMs.Value) / (interval * 10);
                        previousMainCpuMs = cpu;
                    }
                }
            }
            catch { previousMainCpuMs = null; }
        }
        var tick = s.GetTickSummary();
        double totalWorkMs = (tick.AverageMs ?? 0) * tick.Count;
        int? players = null, entities = null, chunks = null, generating = null;
        if (config.Profiling.WorldCounts && server is not null)
        {
            int n = 0; foreach (var client in server.Clients.Values) if (client.Player is not null && client.IsPlayingClient) n++;
            players = n; entities = api.World.LoadedEntities.Count; chunks = chunkCount?.Invoke();
            generating = api.WorldManager.CurrentGeneratingChunkCount;
        }
        double ownMs = Stopwatch.GetElapsedTime(workStart).TotalMilliseconds;
        s.AddSample(new()
        {
            TimestampUtc = DateTimeOffset.UtcNow, ElapsedSeconds = elapsed, TickCount = ticks,
            EffectiveTps = HookUsable("tickMetrics") ? (ticks - lastTickCount) / interval : null,
            AverageTickMs = ticks > lastTickCount ? (totalWorkMs - previousWorkMs) / (ticks - lastTickCount) : null,
            P95TickMs = tick.P95Ms,
            ProcessCpuPercent = config.Profiling.ProcessMetrics ? Math.Max(0, cpuMs - previousCpuMs) / (interval * 10) : null,
            MainThreadCpuPercent = mainCpu,
            ManagedHeapBytes = config.Profiling.ProcessMetrics ? GC.GetTotalMemory(false) : null,
            GcCommittedBytes = config.Profiling.ProcessMetrics ? info.TotalCommittedBytes : null,
            GcFragmentedBytes = config.Profiling.ProcessMetrics ? info.FragmentedBytes : null,
            WorkingSetBytes = workingSet, PrivateMemoryBytes = privateBytes,
            AllocatedBytesDelta = config.Profiling.ProcessMetrics ? Math.Max(0, allocatedNow - allocated) : null,
            Gc0Delta = config.Profiling.ProcessMetrics ? gc0Now - gc0 : null,
            Gc1Delta = config.Profiling.ProcessMetrics ? gc1Now - gc1 : null,
            Gc2Delta = config.Profiling.ProcessMetrics ? gc2Now - gc2 : null,
            GcPauseMsDelta = config.Profiling.ProcessMetrics ? Math.Max(0, pause - previousGcPause) : null,
            Players = players, LoadedEntities = entities, LoadedChunks = chunks, GeneratingChunkColumns = generating,
            NetworkSentBytesDelta = config.Profiling.NetworkCounters && sentNow >= sent ? sentNow - sent : null,
            NetworkReceivedBytesDelta = config.Profiling.NetworkCounters && receivedNow >= received ? receivedNow - received : null,
            ProfilerOwnWorkMs = ownMs
        });
        s.AddSampleOverhead(Stopwatch.GetElapsedTime(workStart).TotalMilliseconds);
        previousElapsed = elapsed; lastTickCount = ticks; previousCpuMs = cpuMs; previousWorkMs = totalWorkMs;
        allocated = allocatedNow; sent = sentNow; received = receivedNow; gc0 = gc0Now; gc1 = gc1Now; gc2 = gc2Now; previousGcPause = pause;
    }

    private long ReadSent() => server is null ? 0 : Volatile.Read(ref server.TotalSentBytes) + Volatile.Read(ref server.TotalSentBytesUdp);
    private long ReadReceived() => server is null ? 0 : Volatile.Read(ref server.TotalReceivedBytes) + Volatile.Read(ref server.TotalReceivedBytesUdp);
    private void PlayerConnected(IServerPlayer player) => evidence?.Emit("connected", player.PlayerUID, config.Benchmarking.RunId, server?.Config.VerifyPlayerAuth == true);
    private void PlayerDisconnected(IServerPlayer player) => evidence?.Emit("disconnected", player.PlayerUID, config.Benchmarking.RunId, server?.Config.VerifyPlayerAuth == true);

    public struct TickState { public int Index; public long Count; public long Start; }
    public static void ProcessPrefix(ServerMain __instance, out TickState __state)
    {
        __state = default;
        var owner = active;
        if (owner?.IsRunning != true) return;
        try
        {
            long start = Stopwatch.GetTimestamp();
            int index = __instance.StatsCollectorIndex;
            __state = new() { Index = index, Count = __instance.StatsCollector[index].ticksTotal, Start = start };
            if (owner.mainNativeThreadId == 0 && !owner.config.Compatibility.SafeMode && owner.config.Profiling.MainThreadCpu)
                owner.mainNativeThreadId = OperatingSystem.IsLinux() ? NativeGetTid() : OperatingSystem.IsWindows() ? (int)NativeGetCurrentThreadId() : 0;
            owner.Session!.AddHookOverhead(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch { owner.SetModule("tickMetrics", "failed", "Tick prefix unavailable; no gameplay modification."); }
    }
    public static void ProcessPostfix(ServerMain __instance, TickState __state)
    {
        var owner = active;
        if (owner?.IsRunning != true || __state.Start == 0) return;
        long start = Stopwatch.GetTimestamp();
        try
        {
            int index = __instance.StatsCollectorIndex;
            var stats = __instance.StatsCollector[index];
            if ((index != __state.Index || stats.ticksTotal != __state.Count) && stats.ticksTotal > 0)
            {
                int last = (stats.tickTimeIndex + stats.tickTimes.Length - 1) % stats.tickTimes.Length;
                owner.Session!.Tick(stats.tickTimes[last]);
            }
            owner.Session!.AddHookOverhead(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch { owner.SetModule("tickMetrics", "failed", "Tick collector unavailable; stock simulation unchanged."); }
    }

    public struct TimingState { public HotspotCounter? Counter; public long Started; public bool Sampled; }
    public static void TimingPrefix(MethodBase __originalMethod, out TimingState __state)
    {
        __state = default;
        var owner = active; var s = owner?.Session;
        if (s?.Running != true) return;
        try
        {
            long observerStarted = Stopwatch.GetTimestamp();
            if (!owner!.timingCounters.TryGetValue(__originalMethod, out var counter)) return;
            bool sampled = counter.Calls % owner!.config.Profiling.SystemSampleEvery == 0;
            __state = new() { Counter = counter, Sampled = sampled, Started = sampled ? Stopwatch.GetTimestamp() : 0 };
            s.AddHookOverhead(Stopwatch.GetElapsedTime(observerStarted).TotalMilliseconds);
        }
        catch { /* Observers must never throw into gameplay. */ }
    }
    public static void CallbackPrefix(object __instance, out TimingState __state)
    {
        __state = default;
        var owner = active; var s = owner?.Session;
        if (s?.Running != true) return;
        try
        {
            long observerStarted = Stopwatch.GetTimestamp();
            // Bound metadata and cache method descriptors; no callback/player/block instances are retained.
            var type = __instance.GetType();
            if (!owner!.callbackFields.TryGetValue(type, out var fields)) return;
            Delegate? callback = null;
            foreach (var field in fields) if (field.GetValue(__instance) is Delegate found) { callback = found; break; }
            if (callback is null) return;
            var method = callback.Method;
            if (!s.TryExistingHotspot(method, out var counter))
            {
                if (!s.HasHotspotCapacity) { s.OmitHotspotLookup(); return; }
                string name = method.DeclaringType?.Assembly.GetName().Name + ":" + method.DeclaringType?.FullName + "." + method.Name;
                counter = s.FindHotspot(method, name, "registeredCallbacks");
            }
            if (counter is null) return;
            bool sampled = counter.Calls % owner!.config.Profiling.SystemSampleEvery == 0;
            __state = new() { Counter = counter, Sampled = sampled, Started = sampled ? Stopwatch.GetTimestamp() : 0 };
            s.AddHookOverhead(Stopwatch.GetElapsedTime(observerStarted).TotalMilliseconds);
        }
        catch { /* Handler and original exception behavior are never altered. */ }
    }
    public static void TimingPostfix(TimingState __state)
    {
        var s = active?.Session;
        if (s?.Running != true || __state.Counter is null) return;
        try
        {
            long observerStarted = Stopwatch.GetTimestamp();
            s.Call(__state.Counter, __state.Sampled, __state.Sampled ? Stopwatch.GetElapsedTime(__state.Started).TotalMilliseconds : 0);
            s.AddHookOverhead(Stopwatch.GetElapsedTime(observerStarted).TotalMilliseconds);
        }
        catch { /* Observational only. */ }
    }
    private static string Category(string name) => name.Contains("Entity", StringComparison.Ordinal) ? "entities" : name.Contains("Chunk", StringComparison.Ordinal) ? "chunks" : name.Contains("Save", StringComparison.Ordinal) ? "autosavePreparation" : name.Contains("Block", StringComparison.Ordinal) ? "blockSimulation" : name.Contains("Trigger", StringComparison.Ordinal) ? "callbackDispatch" : "serverSystems";
    [DllImport("libc", EntryPoint = "gettid")] private static extern int NativeGetTid();
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")] private static extern uint NativeGetCurrentThreadId();

    public void Dispose()
    {
        if (disposed) return;
        try { if (IsRunning) Stop(); }
        catch (Exception e) { api.Logger.Warning("[NewfiesOptimizer] Final capture unavailable ({0}); continuing cleanup.", e.GetType().Name); }
        finally
        {
            disposed = true; Session?.Stop(); RemoveOwnHooks();
            try { api.Event.UnregisterGameTickListener(listenerId); } catch { }
            try { api.Event.PlayerNowPlaying -= PlayerConnected; api.Event.PlayerDisconnect -= PlayerDisconnected; } catch { }
            try { evidence?.Dispose(); } catch { }
            if (export is not null) try { export.Wait(TimeSpan.FromSeconds(5)); } catch { }
            process.Dispose();
        }
    }
}
