using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BlockLogger.AnimalLimiter;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.ServerHealth;

/// <summary>
/// Low-overhead server diagnostics sampler plus bounded engine frame-profiler captures.
/// The continuous sampler does not leave Vintage Story's FrameProfiler enabled; manual and
/// major-incident attribution captures enable it only for a short, time-limited window.
/// </summary>
public sealed class ServerHealthService : IDisposable
{
    private const int SampleIntervalMs = 1000;
    private const int MaxSamples = 3600; // 60 minutes at one sample/second.
    private const int MaxSlowSpikes = 50;
    private const int MaxDiagnosticIncidents = 100;
    private const double SlowSpikeThresholdMs = 150;
    private const int MaxProfileAccumulatorKeys = 4096;
    private const int MaxPhaseAccumulatorKeys = 1024;
    private const int ManualMaxProfileLeafSamplesPerTick = 4096;
    private const int AutomaticMaxProfileLeafSamplesPerTick = 512;
    private const string OverflowProfileKey = "<other profiler paths>";
    private const string OverflowPhaseKey = "<other tick phases>";
    private const int AutomaticIncidentProfileDurationSeconds = 3;
    private const long AutomaticIncidentProfileCooldownMilliseconds = 300_000L;
    private const double AutomaticIncidentProfileTriggerDelayMs = 750d;
    private const int AutomaticProfileSampleIntervalMs = 100;
    private const int ManualProfileSampleIntervalMs = 50;

    private readonly ICoreServerAPI api;
    private readonly System.Func<LogStorageService?> storageAccessor;
    private readonly Process process;
    private readonly object sync = new();

    private readonly Queue<ServerHealthSample> samples = new();
    private readonly Queue<ServerSlowSpike> slowSpikes = new();
    private readonly Queue<ServerDiagnosticIncident> diagnosticIncidents = new();
    private ServerHealthSample? latestSample;
    private readonly Queue<WorldSaveLagWindow> worldSaveWindows = new();

    private long sampleListenerId = -1;
    private long lastSampleTimestamp;
    private TimeSpan lastCpuTime;
    private int cachedLoadedChunks;
    private int samplesSinceChunkRefresh = 30;
    private bool started;
    private bool enabled;
    private bool firstSamplingEnable = true;
    private long incidentSuppressionUntilUnixMilliseconds;
    private const int StartupWarmupMilliseconds = 45_000;
    private const int ReenableWarmupMilliseconds = 5_000;
    private const int MinimumSmartBaselineSamples = 60;

    // Manual engine frame-profiler capture.
    private bool profileRunning;
    private long profileGeneration;
    private bool profilerWasEnabled;
    private long profileListenerId = -1;
    private long lastProfileRootStart = long.MinValue;
    private DateTime profileStartedUtc;
    private int profileDurationSeconds;
    // Leaf work is kept separate from inclusive parent ranges to avoid the old
    // double-counting problem where a parent range and all of its children were
    // both added to the same "total work" ranking.
    private readonly Dictionary<string, ProfileAccumulator> profileAccumulators =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProfileAccumulator> phaseAccumulators =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModProfileAccumulator> modAccumulators =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<double> profileTickDurationsMs = new();
    private readonly List<ServerProfileSlowTick> capturedSlowProfileTicks = new();
    private readonly Dictionary<string, ServerProfileSlowCause> capturedSlowCauses = new(StringComparer.Ordinal);
    private List<ServerProfileSlowCause> lastSlowCauses = new();
    private readonly List<ProfileLeafSample> profileLeafBuffer = new(256);
    private List<ModIdentity> modIdentities = new();

    private bool offThreadSettingsCaptured;
    private bool offThreadPrintWasEnabled;
    private int offThreadThresholdWas;
    private HashSet<string> offThreadBaseline = new(StringComparer.Ordinal);

    private List<ServerProfilerHotspot> lastHotspots = new();
    private List<ServerProfilerModHotspot> lastModHotspots = new();
    private List<ServerProfilerPhase> lastPhases = new();
    private List<ServerProfileSlowTick> lastProfileSlowTicks = new();
    private List<ServerProfilerOffThreadReport> lastOffThreadReports = new();
    private ServerProfileSummary lastProfile = new();
    private bool automaticIncidentProfilingEnabled;
    private long lastAutomaticIncidentProfileUnixMilliseconds;
    private long lastAutomaticIncidentTriggerUnixMilliseconds;
    private double lastAutomaticIncidentTriggerDelayMs;
    private string profileCaptureKind = "Manual";
    private bool profileAutomatic;
    private int profileLeafLimit = ManualMaxProfileLeafSamplesPerTick;
    private readonly Dictionary<string, ModAttribution> profileAttributionCache = new(StringComparer.OrdinalIgnoreCase);
    private long profileTriggerIncidentUnixMilliseconds;

    // Smart analysis performs median/MAD and trend calculations over rolling
    // history. Cache it briefly so WebDashboard v2 live/section refreshes do not repeat
    // that work on every request while diagnosing an already slow server.
    private ServerSmartAnalysis cachedSmartAnalysis = new();
    private long cachedSmartAnalysisUnixMilliseconds;

    public ServerHealthService(ICoreServerAPI api, System.Func<LogStorageService?> storageAccessor)
    {
        this.api = api;
        this.storageAccessor = storageAccessor;
        process = Process.GetCurrentProcess();
    }

    public void Start(bool startEnabled)
    {
        lock (sync)
        {
            if (started) return;
            started = true;
            api.Event.GameWorldSave += OnWorldSave;
            enabled = false;

            if (startEnabled)
            {
                EnableSamplingUnsafe();
            }
            else
            {
                api.Logger.Notification(
                    "[ServerHealth] profiler/diagnostics are disabled. Enable them from the Profiler GUI when needed.");
            }
        }
    }

    private void OnWorldSave()
    {
        lock (sync) { if (!started || !enabled) return; }
        long startUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long startClock = Stopwatch.GetTimestamp();
        api.Event.RegisterCallback(_ =>
        {
            var window = new WorldSaveLagWindow(startUnix, Stopwatch.GetElapsedTime(startClock).TotalMilliseconds);
            lock (sync)
            {
                if (!started || !enabled) return;
                worldSaveWindows.Enqueue(window);
                while (worldSaveWindows.Count > 128) worldSaveWindows.Dequeue();
                // Callback ordering varies: the sampler can run before this callback.
                // Revisit bounded retained incidents so that either ordering works.
                foreach (var incident in diagnosticIncidents) CorrelateWorldSaveUnsafe(incident);
                cachedSmartAnalysisUnixMilliseconds = 0;
            }
        }, 1);
    }

    private void CorrelateWorldSaveUnsafe(ServerDiagnosticIncident incident)
    {
        var match = WorldSaveLagCorrelation.Match(worldSaveWindows, incident.UnixMilliseconds, incident.DelayMs);
        if (match == null || incident.WorldSaveWindowMs >= match.ObservedMilliseconds) return;
        bool first = incident.WorldSaveWindowMs <= 0;
        incident.WorldSaveWindowMs = match.ObservedMilliseconds;
        incident.WorldSaveStartedUnixMilliseconds = match.StartUnixMilliseconds;
        if (first)
            incident.Evidence = $"World save overlapped this sampling interval (observed main-thread window {match.ObservedMilliseconds:0}ms; correlation, not proven cause). " + incident.Evidence;
    }

    public bool Enabled
    {
        get
        {
            lock (sync) return enabled;
        }
    }

    public bool AutomaticIncidentProfilingEnabled
    {
        get
        {
            lock (sync) return automaticIncidentProfilingEnabled;
        }
    }

    public void SetAutomaticIncidentProfilingEnabled(bool value)
    {
        bool stopAutomatic;
        lock (sync)
        {
            automaticIncidentProfilingEnabled = value;
            stopAutomatic = !value && profileRunning && profileAutomatic;
        }
        if (stopAutomatic) FinishManualProfileSafe();
    }

    /// <summary>
    /// Returns the newest lightweight health sample without constructing a full
    /// ServerHealthSnapshot. Used by low-overhead observers such as world-save
    /// timing that only need the current scheduler-delay value.
    /// </summary>
    public ServerHealthSample Current
    {
        get
        {
            lock (sync)
            {
                return latestSample ?? new ServerHealthSample();
            }
        }
    }

    /// <summary>
    /// Tiny snapshot for the in-game live HUD. Avoids cloning/downsampling the
    /// 30-minute history and avoids running Smart Diagnosis for a 2-second UI tick.
    /// </summary>
    public ServerHealthLiveState GetLiveState()
    {
        lock (sync)
        {
            return new ServerHealthLiveState
            {
                Enabled = enabled,
                Current = latestSample ?? new ServerHealthSample(),
                ProfileRunning = profileRunning,
                ProfileDurationSeconds = profileDurationSeconds,
                ProfileRemainingSeconds = profileRunning ? GetRemainingSecondsUnsafe() : 0
            };
        }
    }

    // Uses already sampled leaf timings; never reads or enables the engine profiler here.
    public ServerTickContributors GetTickContributors()
    {
        lock (sync)
        {
            double sampledMs = profileTickDurationsMs.Sum();
            var rows = profileRunning
                ? profileAccumulators.OrderByDescending(p => p.Value.TotalMs).Take(12).Select(p =>
                {
                    var owner = ResolveModAttribution(p.Key);
                    return new ServerProfilerHotspot
                    {
                        Code = p.Key, TotalMs = p.Value.TotalMs, MaxSingleMs = p.Value.MaxSingleMs,
                        Calls = p.Value.Calls, ModId = owner.ModId, ModName = owner.ModName,
                        AttributionConfidence = owner.Confidence, IsCore = owner.IsCore,
                        PercentOfProfileTickTime = sampledMs <= 0 ? 0 : p.Value.TotalMs / sampledMs * 100
                    };
                }).ToList()
                : lastHotspots.Take(12).ToList();
            return new ServerTickContributors
            {
                Running = profileRunning,
                CaptureUnixMilliseconds = profileRunning ? new DateTimeOffset(profileStartedUtc).ToUnixTimeMilliseconds() : lastProfile.CompletedUnixMilliseconds,
                SampledTicks = profileRunning ? profileTickDurationsMs.Count : lastProfile.TickCount,
                RemainingSeconds = profileRunning ? GetRemainingSecondsUnsafe() : 0,
                Rows = rows
            };
        }
    }

    public int ClearModHotspots()
    {
        lock (sync)
        {
            int count = lastModHotspots.Count;
            lastModHotspots.Clear();
            cachedSmartAnalysis = new ServerSmartAnalysis();
            cachedSmartAnalysisUnixMilliseconds = 0;
            return count;
        }
    }

    public int ClearProfileSlowTicks()
    {
        lock (sync)
        {
            int count = lastProfileSlowTicks.Count;
            lastProfileSlowTicks.Clear();
            lastSlowCauses.Clear();
            cachedSmartAnalysis = new ServerSmartAnalysis();
            cachedSmartAnalysisUnixMilliseconds = 0;
            return count;
        }
    }

    public int ClearDiagnosticIncidents()
    {
        lock (sync)
        {
            int count = diagnosticIncidents.Count;
            diagnosticIncidents.Clear();
            slowSpikes.Clear();
            cachedSmartAnalysis = new ServerSmartAnalysis();
            cachedSmartAnalysisUnixMilliseconds = 0;
            return count;
        }
    }

    public void SetEnabled(bool value)
    {
        lock (sync)
        {
            if (!started)
            {
                enabled = value;
                return;
            }

            if (enabled == value) return;

            if (value)
            {
                EnableSamplingUnsafe();
            }
            else
            {
                enabled = false;
                StopSamplingUnsafe();
                StopManualProfileUnsafe();

                // A disabled profiler should not keep presenting stale rolling
                // diagnostics as if they were still current.
                samples.Clear();
                latestSample = null;
                slowSpikes.Clear();
                diagnosticIncidents.Clear();
                cachedSmartAnalysis = new ServerSmartAnalysis();
                cachedSmartAnalysisUnixMilliseconds = 0;
                cachedLoadedChunks = 0;
                samplesSinceChunkRefresh = 30;

                api.Logger.Notification("[ServerHealth] profiler/diagnostics disabled.");
            }
        }
    }

    private void EnableSamplingUnsafe()
    {
        if (enabled && sampleListenerId >= 0) return;

        enabled = true;
        long warmupNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        incidentSuppressionUntilUnixMilliseconds = warmupNow +
            (firstSamplingEnable ? StartupWarmupMilliseconds : ReenableWarmupMilliseconds);
        firstSamplingEnable = false;
        lastSampleTimestamp = Stopwatch.GetTimestamp();
        process.Refresh();
        lastCpuTime = process.TotalProcessorTime;
        samplesSinceChunkRefresh = 30;

        if (sampleListenerId < 0)
        {
            sampleListenerId = api.Event.RegisterGameTickListener(
                _ => Sample(),
                ex => api.Logger.Warning("[ServerHealth] sample failed: {0}: {1}", ex.GetType().Name, ex.Message),
                SampleIntervalMs);
        }

        api.Logger.Notification(
            "[ServerHealth] profiler/diagnostics enabled. 1s samples, 30 minute in-memory history.");
    }

    private void StopSamplingUnsafe()
    {
        if (sampleListenerId >= 0)
        {
            api.Event.UnregisterGameTickListener(sampleListenerId);
            sampleListenerId = -1;
        }
    }

    private void StopManualProfileUnsafe()
    {
        if (profileListenerId >= 0)
        {
            api.Event.UnregisterGameTickListener(profileListenerId);
            profileListenerId = -1;
        }

        if (profileRunning)
            api.World.FrameProfiler.Enabled = profilerWasEnabled;

        RestoreOffThreadProfilerSettings();

        profileRunning = false;
        profileDurationSeconds = 0;
        lastProfileRootStart = long.MinValue;
        ReleaseManualProfileWorkingSetUnsafe();
    }

    public ServerHealthSnapshot GetSnapshot(
        int maxGraphPoints = 360,
        int windowSeconds = 1800)
    {
        lock (sync)
        {
            int boundedWindowSeconds = Math.Clamp(windowSeconds, 60, 3600);
            long cutoff = DateTimeOffset.UtcNow
                .AddSeconds(-boundedWindowSeconds)
                .ToUnixTimeMilliseconds();

            var allSamples = samples.ToList();
            var source = allSamples
                .Where(x => x.UnixMilliseconds >= cutoff)
                .ToList();

            // A brand-new sampler may not yet have a full requested window.
            if (source.Count == 0 && allSamples.Count > 0)
                source.Add(allSamples[^1]);

            var reduced = Downsample(
                source,
                Math.Clamp(maxGraphPoints, 60, 1800));

            var current = allSamples.Count == 0
                ? new ServerHealthSample()
                : allSamples[^1];

            return new ServerHealthSnapshot
            {
                Enabled = enabled,
                Current = current,
                Samples = reduced,
                SlowSpikes = slowSpikes
                    .Where(x => x.UnixMilliseconds >= cutoff)
                    .Reverse()
                    .Take(20)
                    .ToList(),
                DiagnosticIncidents = diagnosticIncidents
                    .Where(x => x.UnixMilliseconds >= cutoff)
                    .Reverse()
                    .Take(60)
                    .ToList(),
                DiagnosticSummary = BuildDiagnosticSummaryUnsafe(allSamples),
                SmartAnalysis = GetSmartAnalysisUnsafe(
                    allSamples,
                    diagnosticIncidents.ToList()),
                Hotspots = lastHotspots.Take(30).ToList(),
                ModHotspots = lastModHotspots.Take(30).ToList(),
                Phases = lastPhases.Take(30).ToList(),
                ProfileSlowTicks = lastProfileSlowTicks.Take(60).ToList(),
                ProfileSlowCauses = lastSlowCauses.ToList(),
                OffThreadReports = lastOffThreadReports.Take(30).ToList(),
                LoadedModCount = modIdentities.Count > 0
                    ? modIdentities.Count
                    : api.ModLoader.Mods.Count(),
                ProfileRunning = profileRunning,
                ProfileDurationSeconds = profileDurationSeconds,
                ProfileRemainingSeconds = profileRunning
                    ? Math.Max(0, (int)Math.Ceiling(
                        profileDurationSeconds - (DateTime.UtcNow - profileStartedUtc).TotalSeconds))
                    : 0,
                AutomaticIncidentProfilingEnabled = automaticIncidentProfilingEnabled,
                AutomaticIncidentProfileDurationSeconds = AutomaticIncidentProfileDurationSeconds,
                AutomaticIncidentProfileCooldownSeconds = (int)(AutomaticIncidentProfileCooldownMilliseconds / 1000L),
                LastAutomaticIncidentProfileUnixMilliseconds = lastAutomaticIncidentProfileUnixMilliseconds,
                LastAutomaticIncidentTriggerUnixMilliseconds = lastAutomaticIncidentTriggerUnixMilliseconds,
                LastAutomaticIncidentTriggerDelayMs = lastAutomaticIncidentTriggerDelayMs,
                LastProfile = lastProfile
            };
        }
    }


    /// <summary>
    /// Returns the retained evidence for one mod from the most recent bounded
    /// engine profile. This is deliberately separate from the lightweight
    /// rolling sampler: attribution is only reported when FrameProfiler markers
    /// actually mapped back to the requested mod.
    /// </summary>
    public ServerModPerformanceDetails GetModPerformanceDetails(string modId)
    {
        string requested = (modId ?? string.Empty).Trim();

        lock (sync)
        {
            var mod = lastModHotspots.FirstOrDefault(x =>
                x.ModId.Equals(requested, StringComparison.OrdinalIgnoreCase));

            return new ServerModPerformanceDetails
            {
                ModId = requested,
                ProfileAvailable = lastProfile.CompletedUnixMilliseconds > 0,
                ProfileCompletedUnixMilliseconds = lastProfile.CompletedUnixMilliseconds,
                ProfileDurationSeconds = lastProfile.DurationSeconds,
                ProfileTickCount = lastProfile.TickCount,
                ProfileAverageTickMs = lastProfile.AverageTickMs,
                ProfileP95TickMs = lastProfile.P95TickMs,
                ProfileP99TickMs = lastProfile.P99TickMs,
                ProfileMaxTickMs = lastProfile.MaxTickMs,
                Summary = mod,
                Hotspots = lastHotspots
                    .Where(x => x.ModId.Equals(requested, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.TotalMs)
                    .Take(30)
                    .ToList(),
                Phases = lastPhases
                    .Where(x => x.ModId.Equals(requested, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.TotalMs)
                    .Take(20)
                    .ToList(),
                OffThreadReports = lastOffThreadReports
                    .Where(x => x.ModId.Equals(requested, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.DurationMs)
                    .Take(20)
                    .ToList(),
                SlowTicks = lastProfileSlowTicks
                    .Where(x => !string.IsNullOrWhiteSpace(x.TopCauseSummary) &&
                        x.TopCauseSummary.Contains(requested, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.TickMs)
                    .Take(20)
                    .ToList()
            };
        }
    }

    public bool StartManualProfile(int requestedSeconds, out string message)
        => StartProfile(requestedSeconds, automatic: false, out message);

    private bool StartProfile(int requestedSeconds, bool automatic, out string message)
    {
        int seconds = automatic
            ? Math.Clamp(requestedSeconds, 2, 5)
            : Math.Clamp(requestedSeconds, 5, 30);

        lock (sync)
        {
            if (!enabled)
            {
                message = "Profiler is disabled. Enable Profiler first.";
                return false;
            }

            if (profileRunning)
            {
                message = $"An engine profile is already running ({GetRemainingSecondsUnsafe()}s remaining).";
                return false;
            }

            profileRunning = true;
            profileDurationSeconds = seconds;
            profileStartedUtc = DateTime.UtcNow;
            profileAutomatic = automatic;
            profileLeafLimit = automatic ? AutomaticMaxProfileLeafSamplesPerTick : ManualMaxProfileLeafSamplesPerTick;
            profileCaptureKind = automatic ? "Automatic lag incident" : "Manual";
            profileTriggerIncidentUnixMilliseconds = 0;
            lastProfileRootStart = long.MinValue;
            profileAccumulators.Clear();
            phaseAccumulators.Clear();
            modAccumulators.Clear();
            profileTickDurationsMs.Clear();
            capturedSlowProfileTicks.Clear();
            capturedSlowCauses.Clear();
            profileAttributionCache.Clear();
            // Mod-token catalog construction is intentionally deferred to finalization. The profile
            // capture window should do the minimum possible work on the already-stressed game thread.
            modIdentities = new List<ModIdentity>();

            // Off-thread slow-profile collection is useful during an explicit manual investigation,
            // but it adds extra diagnostic work and is not needed for the short automatic
            // main-thread attribution burst. Automatic captures therefore leave it untouched.
            offThreadSettingsCaptured = false;
            offThreadBaseline = new HashSet<string>(StringComparer.Ordinal);
            if (!automatic)
            {
                offThreadSettingsCaptured = true;
                offThreadPrintWasEnabled = FrameProfilerUtil.PrintSlowTicks_Offthreads;
                offThreadThresholdWas = FrameProfilerUtil.PrintSlowTicksThreshold_Offthreads;
                offThreadBaseline = new HashSet<string>(
                    FrameProfilerUtil.offThreadProfiles.ToArray(),
                    StringComparer.Ordinal);
                FrameProfilerUtil.PrintSlowTicks_Offthreads = true;
                FrameProfilerUtil.PrintSlowTicksThreshold_Offthreads =
                    offThreadPrintWasEnabled && offThreadThresholdWas > 0
                        ? Math.Min(offThreadThresholdWas, 50)
                        : 50;
            }

            var profiler = api.World.FrameProfiler;
            profilerWasEnabled = profiler.Enabled;

            // FrameProfiler.Begin() is called by the engine at the start of each
            // server tick, but only when Enabled or PrintSlowTicks was already
            // true at that moment. Web/GUI actions execute as queued main-thread
            // work inside an existing tick. Simply flipping Enabled from false to
            // true here can therefore make the engine reach FrameProfiler.End()
            // with no root entry for the current tick, which is fatal in 1.22.x.
            // Prime the current tick only when profiling was completely inactive
            // at its start. The engine's normal End() will close this root, and
            // subsequent ticks are profiled normally.
            bool profilerTickWasActive = profiler.Enabled || profiler.PrintSlowTicks;
            profiler.Enabled = true;
            if (!profilerTickWasActive)
            {
                profiler.Begin("BlockLogger manual profiler transition");
            }

            profileListenerId = api.Event.RegisterGameTickListener(
                _ =>
                {
                    var activeProfiler = api.World.FrameProfiler;
                    activeProfiler.Enter("blocklogger:profiler-capture");
                    try
                    {
                        CaptureProfilerTick();
                    }
                    finally
                    {
                        activeProfiler.Leave();
                    }
                },
                ex => api.Logger.Warning("[ServerHealth] profiler capture tick failed: {0}: {1}", ex.GetType().Name, ex.Message),
                automatic ? AutomaticProfileSampleIntervalMs : ManualProfileSampleIntervalMs);

            long generation = ++profileGeneration;
            api.Event.RegisterCallback(_ =>
            {
                lock (sync) { if (!profileRunning || profileGeneration != generation) return; }
                FinishManualProfileSafe();
            }, seconds * 1000);
            message = automatic
                ? $"Started {seconds}s lightweight automatic Vintage Story engine profile."
                : $"Started {seconds}s Vintage Story engine profile.";
            return true;
        }
    }

    private int GetRemainingSecondsUnsafe() =>
        Math.Max(0, (int)Math.Ceiling(
            profileDurationSeconds - (DateTime.UtcNow - profileStartedUtc).TotalSeconds));

    private void Sample()
    {
        if (!enabled) return;

        long now = Stopwatch.GetTimestamp();
        double elapsedSeconds = Math.Max(
            0.001,
            (now - lastSampleTimestamp) / (double)Stopwatch.Frequency);
        lastSampleTimestamp = now;

        double expectedSeconds = SampleIntervalMs / 1000d;
        double mainThreadDelayMs = Math.Max(0, (elapsedSeconds - expectedSeconds) * 1000d);

        process.Refresh();
        TimeSpan cpuTime = process.TotalProcessorTime;
        double cpuSeconds = Math.Max(0, (cpuTime - lastCpuTime).TotalSeconds);
        lastCpuTime = cpuTime;
        double cpuPercent = Math.Clamp(
            cpuSeconds / elapsedSeconds / Math.Max(1, Environment.ProcessorCount) * 100d,
            0,
            100);

        if (++samplesSinceChunkRefresh >= 30)
        {
            samplesSinceChunkRefresh = 0;
            try
            {
                // Vintage Story explicitly documents LoadedChunkIndices as not cheap to retrieve,
                // so refresh this counter only every 30 seconds.
                cachedLoadedChunks = api.World.LoadedChunkIndices?.Length ?? cachedLoadedChunks;
            }
            catch
            {
                // Keep the last successful count.
            }
        }

        int loadedEntities = 0;
        try { loadedEntities = api.World.LoadedEntities.Count; } catch { }

        var storage = storageAccessor();
        var limiter = AnimalLimiterSystem.Current;

        double managedHeapMb = GC.GetTotalMemory(false) / 1048576d;
        double privateMemoryMb = process.PrivateMemorySize64 / 1048576d;
        double gcCommittedMb = 0;
        double gcFragmentedMb = 0;
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            gcCommittedMb = gcInfo.TotalCommittedBytes / 1048576d;
            gcFragmentedMb = gcInfo.FragmentedBytes / 1048576d;
        }
        catch { }
        double estimatedNativeMb = Math.Max(0, privateMemoryMb - Math.Max(managedHeapMb, gcCommittedMb));

        var sample = new ServerHealthSample
        {
            UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MainThreadDelayMs = mainThreadDelayMs,
            CpuPercent = cpuPercent,
            WorkingSetMb = process.WorkingSet64 / 1048576d,
            PrivateMemoryMb = privateMemoryMb,
            ManagedHeapMb = managedHeapMb,
            GcCommittedMb = gcCommittedMb,
            GcFragmentedMb = gcFragmentedMb,
            EstimatedNativeMb = estimatedNativeMb,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            Players = api.World.AllOnlinePlayers?.Length ?? 0,
            LoadedEntities = loadedEntities,
            LoadedChunks = cachedLoadedChunks,
            BlockLoggerQueue = storage == null ? 0 : (int)Math.Min(int.MaxValue, storage.PendingEvents),
            BlockLoggerWriterMs = storage?.LastBatchElapsedMs ?? 0,
            BlockLoggerDropped = storage?.DroppedEvents ?? 0,
            AnimalTrackedEntities = limiter?.HealthTrackedEntityCount ?? 0,
            AnimalTrackedRegions = limiter?.HealthTrackedRegionCount ?? 0
        };

        // Startup/world loading routinely stalls the game thread for seconds while assets,
        // spawn chunks and mod systems initialize. Those are not gameplay lag incidents.
        // Also give a manually re-enabled sampler a few seconds to stabilize so the first
        // callback interval is not misclassified as a stall. Warm-up samples are excluded
        // from the learned baseline as well.
        if (sample.UnixMilliseconds < incidentSuppressionUntilUnixMilliseconds) return;

        bool startAutomaticIncidentProfile = false;
        double automaticIncidentDelayMs = 0;

        lock (sync)
        {
            var previous = latestSample;

            int gen0Delta = previous == null ? 0 : Math.Max(0, sample.Gen0Collections - previous.Gen0Collections);
            int gen1Delta = previous == null ? 0 : Math.Max(0, sample.Gen1Collections - previous.Gen1Collections);
            int gen2Delta = previous == null ? 0 : Math.Max(0, sample.Gen2Collections - previous.Gen2Collections);
            int entityDelta = previous == null ? 0 : sample.LoadedEntities - previous.LoadedEntities;
            int chunkDelta = previous == null || previous.LoadedChunks <= 0
                ? 0
                : sample.LoadedChunks - previous.LoadedChunks;
            int playerDelta = previous == null ? 0 : sample.Players - previous.Players;
            double heapDeltaMb = previous == null ? 0 : sample.ManagedHeapMb - previous.ManagedHeapMb;
            double privateDeltaMb = previous == null ? 0 : sample.PrivateMemoryMb - previous.PrivateMemoryMb;
            double nativeDeltaMb = previous == null ? 0 : sample.EstimatedNativeMb - previous.EstimatedNativeMb;
            long droppedDelta = previous == null ? 0 : Math.Max(0, sample.BlockLoggerDropped - previous.BlockLoggerDropped);

            samples.Enqueue(sample);
            latestSample = sample;
            while (samples.Count > MaxSamples) samples.Dequeue();

            if (mainThreadDelayMs >= SlowSpikeThresholdMs)
            {
                slowSpikes.Enqueue(new ServerSlowSpike
                {
                    UnixMilliseconds = sample.UnixMilliseconds,
                    DelayMs = sample.MainThreadDelayMs,
                    CpuPercent = sample.CpuPercent,
                    WorkingSetMb = sample.WorkingSetMb,
                    Players = sample.Players,
                    LoadedEntities = sample.LoadedEntities,
                    LoadedChunks = sample.LoadedChunks,
                    BlockLoggerQueue = sample.BlockLoggerQueue,
                    BlockLoggerWriterMs = sample.BlockLoggerWriterMs,
                    Gen0Delta = gen0Delta,
                    Gen1Delta = gen1Delta,
                    Gen2Delta = gen2Delta
                });

                while (slowSpikes.Count > MaxSlowSpikes) slowSpikes.Dequeue();
            }

            var evidence = new List<string>();
            int severity = 0;

            if (sample.MainThreadDelayMs >= 2000)
            {
                severity = Math.Max(severity, 3);
                evidence.Add($"severe scheduler stall {sample.MainThreadDelayMs:0}ms");
            }
            else if (sample.MainThreadDelayMs >= 500)
            {
                severity = Math.Max(severity, 2);
                evidence.Add($"major scheduler stall {sample.MainThreadDelayMs:0}ms");
            }
            else if (sample.MainThreadDelayMs >= 250)
            {
                severity = Math.Max(severity, 1);
                evidence.Add($"scheduler stall {sample.MainThreadDelayMs:0}ms");
            }
            else if (sample.MainThreadDelayMs >= 150)
            {
                evidence.Add($"brief scheduler delay {sample.MainThreadDelayMs:0}ms");
            }

            if (sample.CpuPercent >= 90)
            {
                severity = Math.Max(severity, sample.MainThreadDelayMs >= 50 ? 2 : 1);
                evidence.Add($"process CPU {sample.CpuPercent:0}%");
            }

            if (gen2Delta > 0)
            {
                severity = Math.Max(severity, sample.MainThreadDelayMs >= 50 ? 2 : 1);
                evidence.Add($"Gen2 GC +{gen2Delta}");
            }
            else if (gen1Delta >= 2 || gen0Delta >= 10)
            {
                severity = Math.Max(severity, 1);
                evidence.Add($"GC activity +{gen0Delta}/+{gen1Delta}/+{gen2Delta}");
            }

            if (sample.BlockLoggerWriterMs >= 100)
            {
                severity = Math.Max(severity, sample.MainThreadDelayMs >= 50 ? 2 : 1);
                evidence.Add($"BlockLogger writer {sample.BlockLoggerWriterMs}ms");
            }

            if (sample.BlockLoggerQueue >= 1000)
            {
                severity = Math.Max(severity, 1);
                evidence.Add($"BlockLogger queue {sample.BlockLoggerQueue:N0}");
            }

            if (droppedDelta > 0)
            {
                severity = Math.Max(severity, 3);
                evidence.Add($"BlockLogger dropped +{droppedDelta:N0}");
            }

            if (Math.Abs(entityDelta) >= 500)
            {
                severity = Math.Max(severity, sample.MainThreadDelayMs >= 50 ? 2 : 1);
                evidence.Add($"loaded entities changed {entityDelta:+#;-#;0}");
            }
            else if (sample.LoadedEntities >= 20_000)
            {
                severity = Math.Max(severity, 1);
                evidence.Add($"high loaded entity count {sample.LoadedEntities:N0}");
            }

            // LoadedChunkIndices is intentionally sampled only once every ~30s.
            // A chunk delta therefore means "count changed since the last chunk
            // sample", not per-second chunk churn.
            if (Math.Abs(chunkDelta) >= 300)
            {
                severity = Math.Max(severity, sample.MainThreadDelayMs >= 50 ? 2 : 1);
                evidence.Add($"loaded chunks changed {chunkDelta:+#;-#;0} at 30s sample");
            }

            if (heapDeltaMb >= 256)
            {
                severity = Math.Max(severity, sample.MainThreadDelayMs >= 50 ? 2 : 1);
                evidence.Add($"managed heap grew {heapDeltaMb:0}MB");
            }

            if (privateDeltaMb >= 256)
            {
                severity = Math.Max(severity, sample.MainThreadDelayMs >= 50 ? 2 : 1);
                if (heapDeltaMb < 64 && nativeDeltaMb >= 128)
                    evidence.Add($"private memory grew {privateDeltaMb:0}MB while managed heap stayed comparatively flat (native estimate +{nativeDeltaMb:0}MB)");
                else
                    evidence.Add($"private memory grew {privateDeltaMb:0}MB");
            }

            if (playerDelta != 0 && Math.Abs(playerDelta) >= 5)
                evidence.Add($"players changed {playerDelta:+#;-#;0}");

            bool correlatedPressure =
                sample.CpuPercent >= 90 ||
                gen2Delta > 0 ||
                sample.BlockLoggerWriterMs >= 100 ||
                sample.BlockLoggerQueue >= 1000 ||
                Math.Abs(entityDelta) >= 500 ||
                Math.Abs(chunkDelta) >= 300;

            // A lone ~100 ms callback delay is common scheduler jitter, not a useful lag
            // incident. Retain clear stalls, or moderately slow samples only when another
            // subsystem was under pressure at the same moment.
            bool recordIncident =
                sample.MainThreadDelayMs >= 250 ||
                droppedDelta > 0 ||
                (sample.MainThreadDelayMs >= 150 && correlatedPressure);

            if (recordIncident)
            {
                diagnosticIncidents.Enqueue(new ServerDiagnosticIncident
                {
                    UnixMilliseconds = sample.UnixMilliseconds,
                    Severity = droppedDelta > 0 || sample.MainThreadDelayMs >= 2000 ? "Critical" :
                        sample.MainThreadDelayMs >= 500 ? "High" :
                        sample.MainThreadDelayMs >= 250 ? "Watch" : "Info",
                    DelayMs = sample.MainThreadDelayMs,
                    CpuPercent = sample.CpuPercent,
                    ManagedHeapMb = sample.ManagedHeapMb,
                    PrivateMemoryMb = sample.PrivateMemoryMb,
                    EstimatedNativeMb = sample.EstimatedNativeMb,
                    LoadedEntities = sample.LoadedEntities,
                    EntityDelta = entityDelta,
                    LoadedChunks = sample.LoadedChunks,
                    ChunkDelta = chunkDelta,
                    BlockLoggerQueue = sample.BlockLoggerQueue,
                    BlockLoggerWriterMs = sample.BlockLoggerWriterMs,
                    BlockLoggerDroppedDelta = droppedDelta,
                    Gen0Delta = gen0Delta,
                    Gen1Delta = gen1Delta,
                    Gen2Delta = gen2Delta,
                    Evidence = evidence.Count == 0
                        ? "Scheduler/health anomaly recorded."
                        : string.Join(" | ", evidence.Take(6))
                });

                CorrelateWorldSaveUnsafe(diagnosticIncidents.Last());
                cachedSmartAnalysisUnixMilliseconds = 0;
                while (diagnosticIncidents.Count > MaxDiagnosticIncidents)
                    diagnosticIncidents.Dequeue();

                // A short automatic burst profile is reserved for meaningful stalls.
                // It is cooldown-limited so the always-on sampler stays lightweight, and
                // Safe Diagnostics Mode can disable it through SetAutomaticIncidentProfilingEnabled().
                bool hasStrongKnownSubsystemCorrelation =
                    droppedDelta > 0 ||
                    sample.BlockLoggerWriterMs >= 100 ||
                    sample.BlockLoggerQueue >= 1000 ||
                    gen2Delta > 0 ||
                    Math.Abs(entityDelta) >= 500 ||
                    Math.Abs(chunkDelta) >= 300;

                if (automaticIncidentProfilingEnabled &&
                    sample.MainThreadDelayMs >= AutomaticIncidentProfileTriggerDelayMs &&
                    !hasStrongKnownSubsystemCorrelation &&
                    !profileRunning &&
                    sample.UnixMilliseconds - lastAutomaticIncidentProfileUnixMilliseconds >= AutomaticIncidentProfileCooldownMilliseconds)
                {
                    lastAutomaticIncidentProfileUnixMilliseconds = sample.UnixMilliseconds;
                    lastAutomaticIncidentTriggerUnixMilliseconds = sample.UnixMilliseconds;
                    lastAutomaticIncidentTriggerDelayMs = sample.MainThreadDelayMs;
                    startAutomaticIncidentProfile = true;
                    automaticIncidentDelayMs = sample.MainThreadDelayMs;
                }
            }
        }

        if (startAutomaticIncidentProfile)
            StartAutomaticIncidentProfile(automaticIncidentDelayMs);
    }

    private void StartAutomaticIncidentProfile(double triggerDelayMs)
    {
        if (!StartProfile(AutomaticIncidentProfileDurationSeconds, automatic: true, out string message))
        {
            api.Logger.Warning(
                "[ServerHealth] automatic incident profile could not start after {0:0}ms stall: {1}",
                triggerDelayMs,
                message);
            return;
        }

        lock (sync)
        {
            profileCaptureKind = "Automatic lag incident";
            profileTriggerIncidentUnixMilliseconds = lastAutomaticIncidentTriggerUnixMilliseconds;
        }

        api.Logger.Notification(
            "[ServerHealth] automatic {0}s mod-attribution profile started after {1:0}ms main-thread stall; cooldown {2}s.",
            AutomaticIncidentProfileDurationSeconds,
            triggerDelayMs,
            AutomaticIncidentProfileCooldownMilliseconds / 1000L);
    }

    private ServerSmartAnalysis GetSmartAnalysisUnsafe(
        List<ServerHealthSample> source,
        List<ServerDiagnosticIncident> incidents)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (cachedSmartAnalysisUnixMilliseconds > 0 &&
            now - cachedSmartAnalysisUnixMilliseconds < 5_000)
            return cachedSmartAnalysis;

        cachedSmartAnalysis = BuildSmartAnalysisUnsafe(source, incidents);
        cachedSmartAnalysisUnixMilliseconds = now;
        return cachedSmartAnalysis;
    }

    private ServerSmartAnalysis BuildSmartAnalysisUnsafe(
        List<ServerHealthSample> source,
        List<ServerDiagnosticIncident> incidents)
    {
        if (!enabled || source.Count == 0)
        {
            return new ServerSmartAnalysis
            {
                Available = false,
                State = enabled ? "Collecting" : "Disabled",
                HealthScore = enabled ? 100 : 0,
                PrimaryFinding = enabled
                    ? "Waiting for enough samples to learn this server's baseline."
                    : "Profiler diagnostics are disabled.",
                PrimaryConfidence = "Low",
                AnalysisConfidence = "Low",
                DataQualityScore = 0,
                RecoveryState = enabled ? "Learning" : "Unavailable"
            };
        }

        if (source.Count < MinimumSmartBaselineSamples)
        {
            return new ServerSmartAnalysis
            {
                Available = true,
                State = "Learning",
                HealthScore = 100,
                PrimaryFinding = $"Collecting a stable gameplay baseline ({source.Count}/{MinimumSmartBaselineSamples} samples).",
                PrimaryConfidence = "Low",
                AnalysisConfidence = "Low",
                DataQualityScore = Math.Clamp((int)Math.Round(source.Count * 60d / MinimumSmartBaselineSamples), 0, 60),
                RecoveryState = "Learning"
            };
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long recentCutoff = now - 5 * 60_000L;
        long trendCutoff = now - 10 * 60_000L;
        long baselineCutoff = now - 2 * 60_000L;

        var recent = source
            .Where(x => x.UnixMilliseconds >= recentCutoff)
            .ToList();
        if (recent.Count == 0)
            recent = source.TakeLast(Math.Min(300, source.Count)).ToList();

        var baselineSource = source
            .Where(x => x.UnixMilliseconds < baselineCutoff)
            .TakeLast(900)
            .ToList();

        if (baselineSource.Count < 30)
            baselineSource = source
                .Take(Math.Max(1, source.Count - Math.Min(15, source.Count)))
                .ToList();
        if (baselineSource.Count < 10)
            baselineSource = source.ToList();

        var baseline = new ServerAdaptiveBaseline
        {
            SampleCount = baselineSource.Count,
            DelayMedianMs = Median(baselineSource.Select(x => x.MainThreadDelayMs)),
            CpuMedianPercent = Median(baselineSource.Select(x => x.CpuPercent)),
            HeapMedianMb = Median(baselineSource.Select(x => x.ManagedHeapMb)),
            EntityMedian = Median(baselineSource.Select(x => (double)x.LoadedEntities)),
            QueueMedian = Median(baselineSource.Select(x => (double)x.BlockLoggerQueue)),
            WriterMedianMs = Median(baselineSource.Select(x => (double)x.BlockLoggerWriterMs))
        };

        baseline.DelayMadMs = MedianAbsoluteDeviation(
            baselineSource.Select(x => x.MainThreadDelayMs),
            baseline.DelayMedianMs);
        baseline.CpuMadPercent = MedianAbsoluteDeviation(
            baselineSource.Select(x => x.CpuPercent),
            baseline.CpuMedianPercent);
        baseline.HeapMadMb = MedianAbsoluteDeviation(
            baselineSource.Select(x => x.ManagedHeapMb),
            baseline.HeapMedianMb);
        baseline.EntityMad = MedianAbsoluteDeviation(
            baselineSource.Select(x => (double)x.LoadedEntities),
            baseline.EntityMedian);
        baseline.QueueMad = MedianAbsoluteDeviation(
            baselineSource.Select(x => (double)x.BlockLoggerQueue),
            baseline.QueueMedian);
        baseline.WriterMadMs = MedianAbsoluteDeviation(
            baselineSource.Select(x => (double)x.BlockLoggerWriterMs),
            baseline.WriterMedianMs);

        baseline.DelayAnomalyMs = Math.Max(
            50,
            baseline.DelayMedianMs + Math.Max(10, 6 * baseline.DelayMadMs));
        baseline.CpuAnomalyPercent = Math.Min(
            98,
            Math.Max(85, baseline.CpuMedianPercent + Math.Max(10, 5 * baseline.CpuMadPercent)));
        baseline.QueueAnomaly = Math.Max(
            500,
            baseline.QueueMedian + Math.Max(250, 6 * baseline.QueueMad));
        baseline.WriterAnomalyMs = Math.Max(
            75,
            baseline.WriterMedianMs + Math.Max(40, 6 * baseline.WriterMadMs));

        var signals = new List<ServerSmartSignal>();
        var recommendations = new List<string>();

        var recentIncidents = incidents
            .Where(x => x.UnixMilliseconds >= recentCutoff)
            .OrderBy(x => x.UnixMilliseconds)
            .ToList();

        var groups = BuildIncidentGroupsUnsafe(recentIncidents);

        int severeGroups = groups.Count(x => x.Severity == "Critical");
        int highGroups = groups.Count(x => x.Severity == "High");

        double recentDelayMedian = Median(recent.Select(x => x.MainThreadDelayMs));
        double recentCpuMedian = Median(recent.Select(x => x.CpuPercent));
        double recentHeapMedian = Median(recent.Select(x => x.ManagedHeapMb));
        double recentEntityMedian = Median(recent.Select(x => (double)x.LoadedEntities));
        double recentQueueMedian = Median(recent.Select(x => (double)x.BlockLoggerQueue));
        double recentWriterMedian = Median(recent.Select(x => (double)x.BlockLoggerWriterMs));

        int delayAnomalyCount = recent.Count(x =>
            x.MainThreadDelayMs >= baseline.DelayAnomalyMs);
        int cpuAnomalyCount = recent.Count(x =>
            x.CpuPercent >= baseline.CpuAnomalyPercent);
        int queueAnomalyCount = recent.Count(x =>
            x.BlockLoggerQueue >= baseline.QueueAnomaly);
        int writerAnomalyCount = recent.Count(x =>
            x.BlockLoggerWriterMs >= baseline.WriterAnomalyMs);

        int gen2Increase = recent.Count < 2
            ? 0
            : Math.Max(0, recent[^1].Gen2Collections - recent[0].Gen2Collections);
        long droppedIncrease = recent.Count < 2
            ? 0
            : Math.Max(0, recent[^1].BlockLoggerDropped - recent[0].BlockLoggerDropped);

        int saveIncidentCount = recentIncidents.Count(x => x.WorldSaveWindowMs > 0);
        if (saveIncidentCount > 0)
        {
            signals.Add(new ServerSmartSignal
            {
                Category = "WorldSave", Severity = "Watch", Confidence = "Medium", Score = 76,
                Title = "World-save windows overlap recorded lag",
                Evidence = $"{saveIncidentCount} lag sample(s) overlap measured save windows. Matching counts alone were not used.",
                NextAction = "Compare event timestamps and repeated save/non-save periods. Observed save windows include main-thread callback scheduling; they do not prove a disk bottleneck or identify a responsible mod."
            });
        }
        int gcIncidentCount = recentIncidents.Count(x => x.Gen2Delta > 0);
        int storageIncidentCount = recentIncidents.Count(x =>
            x.BlockLoggerWriterMs >= 100 ||
            x.BlockLoggerQueue >= 1000 ||
            x.BlockLoggerDroppedDelta > 0);
        int entityIncidentCount = recentIncidents.Count(x =>
            Math.Abs(x.EntityDelta) >= 500);
        int chunkIncidentCount = recentIncidents.Count(x =>
            Math.Abs(x.ChunkDelta) >= 300);
        int cpuIncidentCount = recentIncidents.Count(x =>
            x.CpuPercent >= 90);

        if (delayAnomalyCount >= 3 || severeGroups > 0)
        {
            signals.Add(new ServerSmartSignal
            {
                Category = "MainThread",
                Severity = severeGroups > 0 ? "Critical" : "High",
                Confidence = "High",
                Score = severeGroups > 0 ? 95 : 78,
                Title = "Main-thread scheduling is repeatedly falling behind",
                Evidence =
                    $"{delayAnomalyCount} sample(s) exceeded the adaptive {baseline.DelayAnomalyMs:0}ms delay threshold; " +
                    $"{groups.Count} grouped lag incident(s) were recorded.",
                NextAction =
                    "Run a 10s or 30s engine profile during the slowdown. The lightweight delay sampler shows that work is late, but only the engine profile can attribute tick work to phases/mods."
            });
        }

        if (cpuIncidentCount >= 2 || cpuAnomalyCount >= 15)
        {
            signals.Add(new ServerSmartSignal
            {
                Category = "CPU",
                Severity = cpuIncidentCount >= 4 ? "High" : "Watch",
                Confidence = cpuIncidentCount >= 2 ? "High" : "Medium",
                Score = Math.Min(90, 55 + cpuIncidentCount * 12),
                Title = "CPU saturation is correlated with lag",
                Evidence =
                    $"{cpuIncidentCount} lag-event sample(s) reached >=90% process CPU; " +
                    $"{cpuAnomalyCount} recent sample(s) exceeded the adaptive CPU threshold of {baseline.CpuAnomalyPercent:0}%.",
                NextAction =
                    "Profile while CPU is high. If one mod dominates high-confidence profiler time, investigate that mod; otherwise inspect entity/block-entity/world activity and off-thread reports."
            });
        }

        if (gcIncidentCount >= 2)
        {
            signals.Add(new ServerSmartSignal
            {
                Category = "GC",
                Severity = gcIncidentCount >= 4 ? "High" : "Watch",
                Confidence = gcIncidentCount > 0 ? "High" : "Medium",
                Score = Math.Min(88, 52 + gcIncidentCount * 15 + gen2Increase * 4),
                Title = "Garbage collection is correlated with slow periods",
                Evidence =
                    $"{gcIncidentCount} lag event(s) coincided with Gen2 GC; Gen2 collections increased by {gen2Increase} in the recent window.",
                NextAction =
                    "Look for allocation-heavy mods or systems. Compare managed-heap growth before the event and use a manual profile to identify code active around the slow ticks."
            });
        }

        if (storageIncidentCount >= 2 ||
            queueAnomalyCount >= 10 ||
            writerAnomalyCount >= 5 ||
            droppedIncrease > 0)
        {
            signals.Add(new ServerSmartSignal
            {
                Category = "BlockLoggerIO",
                Severity = droppedIncrease > 0 ? "Critical" :
                    storageIncidentCount >= 4 ? "High" : "Watch",
                Confidence = storageIncidentCount >= 2 || droppedIncrease > 0
                    ? "High"
                    : "Medium",
                Score = droppedIncrease > 0
                    ? 98
                    : Math.Min(90, 52 + storageIncidentCount * 12),
                Title = droppedIncrease > 0
                    ? "BlockLogger backpressure has started dropping audit events"
                    : "BlockLogger storage pressure is correlated with lag",
                Evidence =
                    $"{storageIncidentCount} lag event(s) showed queue/writer pressure; " +
                    $"recent max queue={recent.Max(x => x.BlockLoggerQueue):N0}, " +
                    $"max writer={recent.Max(x => x.BlockLoggerWriterMs):N0}ms, dropped +{droppedIncrease:N0}.",
                NextAction =
                    "Inspect disk/storage latency and BlockLogger queue pressure. If drops occur, reduce logging load or fix storage performance before treating another mod as the primary cause."
            });
        }

        if (entityIncidentCount >= 2 ||
            recentEntityMedian > baseline.EntityMedian + Math.Max(1500, 6 * baseline.EntityMad))
        {
            signals.Add(new ServerSmartSignal
            {
                Category = "Entities",
                Severity = entityIncidentCount >= 4 ? "High" : "Watch",
                Confidence = entityIncidentCount >= 2 ? "High" : "Medium",
                Score = Math.Min(88, 50 + entityIncidentCount * 14),
                Title = "Entity load changed sharply around lag",
                Evidence =
                    $"{entityIncidentCount} lag event(s) had an entity-count jump >=500; " +
                    $"baseline median={baseline.EntityMedian:0}, recent median={recentEntityMedian:0}.",
                NextAction =
                    "Use the Animal Limiter population scan and inspect high-entity regions. If the manual profiler points at an entity-heavy mod, compare its entity population to the lag window."
            });
        }

        if (chunkIncidentCount >= 2)
        {
            signals.Add(new ServerSmartSignal
            {
                Category = "Chunks",
                Severity = chunkIncidentCount >= 4 ? "High" : "Watch",
                Confidence = "Medium",
                Score = Math.Min(80, 48 + chunkIncidentCount * 12),
                Title = "Large loaded-chunk count changes are near lag events",
                Evidence =
                    $"{chunkIncidentCount} lag event(s) coincided with a >=300 loaded-chunk count change at the throttled 30-second chunk sample.",
                NextAction =
                    "Check whether players were exploring/loading new terrain or whether a worldgen/chunk-loading mod is active. Confirm with manual profiler phases before assigning blame."
            });
        }

        var trendSource = source
            .Where(x => x.UnixMilliseconds >= trendCutoff)
            .ToList();

        var trends = new List<ServerHealthTrend>();
        // Trend labels are intentionally conservative: a few dozen seconds of world
        // settling is not enough evidence to call something a sustained Watch condition.
        if (trendSource.Count >= 180)
        {
            AddTrendIfMeaningful(
                trends,
                "Main-thread delay",
                "ms/min",
                SlopePerMinute(trendSource, x => x.MainThreadDelayMs),
                trendSource[^1].MainThreadDelayMs - trendSource[0].MainThreadDelayMs,
                5,
                40,
                "MainThread",
                "A sustained rise in scheduler delay is more meaningful than one isolated spike.");

            AddTrendIfMeaningful(
                trends,
                "CPU",
                "%/min",
                SlopePerMinute(trendSource, x => x.CpuPercent),
                trendSource[^1].CpuPercent - trendSource[0].CpuPercent,
                2,
                15,
                "CPU",
                "A sustained process-CPU rise can show growing load even before hard saturation.");

            AddTrendIfMeaningful(
                trends,
                "Managed heap",
                "MB/min",
                SlopePerMinute(trendSource, x => x.ManagedHeapMb),
                trendSource[^1].ManagedHeapMb - trendSource[0].ManagedHeapMb,
                10,
                100,
                "Memory",
                "A sustained managed-heap rise can indicate allocation pressure or a growing retained object set.");

            AddTrendIfMeaningful(
                trends,
                "Private memory",
                "MB/min",
                SlopePerMinute(trendSource, x => x.PrivateMemoryMb),
                trendSource[^1].PrivateMemoryMb - trendSource[0].PrivateMemoryMb,
                25,
                250,
                "Memory",
                "Private-memory growth includes managed and native allocations; a rise without similar managed-heap growth points away from ordinary GC retention.");

            AddTrendIfMeaningful(
                trends,
                "Estimated native memory",
                "MB/min",
                SlopePerMinute(trendSource, x => x.EstimatedNativeMb),
                trendSource[^1].EstimatedNativeMb - trendSource[0].EstimatedNativeMb,
                25,
                250,
                "Memory",
                "A sustained native estimate rise can indicate SQLite/native buffers, engine chunk retention, image/compression buffers, or another unmanaged owner.");

            AddTrendIfMeaningful(
                trends,
                "Loaded entities",
                "entities/min",
                SlopePerMinute(trendSource, x => x.LoadedEntities),
                trendSource[^1].LoadedEntities - trendSource[0].LoadedEntities,
                100,
                500,
                "Entities",
                "A sustained entity-count rise can turn an otherwise normal entity system into a lag source.");

            AddTrendIfMeaningful(
                trends,
                "BlockLogger queue",
                "events/min",
                SlopePerMinute(trendSource, x => x.BlockLoggerQueue),
                trendSource[^1].BlockLoggerQueue - trendSource[0].BlockLoggerQueue,
                50,
                250,
                "BlockLoggerIO",
                "A queue that keeps growing means the writer is not keeping up with incoming audit work.");
        }

        foreach (var trend in trends.Where(x => x.Severity != "Info"))
        {
            signals.Add(new ServerSmartSignal
            {
                Category = trend.Category,
                Severity = trend.Severity,
                Confidence = trend.Confidence,
                Score = trend.Severity == "High" ? 78 : 58,
                Title = $"{trend.Name} is trending upward",
                Evidence =
                    $"{trend.SlopePerMinute:+0.0;-0.0;0} {trend.Unit}; net change {trend.NetChange:+0.0;-0.0;0}. {trend.Interpretation}",
                NextAction = trend.Category switch
                {
                    "MainThread" => "Capture a manual engine profile while the delay trend is rising; trend evidence shows deterioration but not the responsible code path.",
                    "CPU" => "Compare the CPU rise against player/entity/chunk changes and run a manual profile if scheduler delay rises with it.",
                    "Memory" => "Watch whether the heap falls after Gen2 GC. If it repeatedly climbs to a higher floor, investigate allocation/retention-heavy mods.",
                    "Entities" => "Compare entity counts by type/region and inspect the Animal Limiter population report.",
                    "BlockLoggerIO" => "Inspect writer latency and storage speed; a continuously rising queue is a backlog, not a normal burst.",
                    _ => "Keep collecting data and compare against a manual profile during the next slowdown."
                }
            });
        }

        // Engine profiler results are the only place where we elevate a named
        // mod as a likely hotspot. Even here, keep the language as profiler evidence.
        var topProfileMod = lastModHotspots
            .Where(x =>
                !x.IsCore &&
                x.ModId != "newfiesserveressentialssuite" &&
                x.ModId != "<unattributed>" &&
                x.Confidence == "High")
            .OrderByDescending(x => x.TotalMs)
            .FirstOrDefault();

        bool profileIsRecent =
            lastProfile.CompletedUnixMilliseconds > 0 &&
            now - lastProfile.CompletedUnixMilliseconds <= 30 * 60_000L;

        if (profileIsRecent &&
            topProfileMod != null &&
            (topProfileMod.PercentOfProfileTickTime >= 20 ||
             topProfileMod.MaxSingleMs >= 50))
        {
            signals.Add(new ServerSmartSignal
            {
                Category = "ProfiledMod",
                Severity = topProfileMod.MaxSingleMs >= 100 ? "High" : "Watch",
                Confidence = "High",
                Score = Math.Min(
                    96,
                    65 + (int)Math.Min(25, topProfileMod.PercentOfProfileTickTime / 2)),
                Title = $"Profile hotspot: {topProfileMod.ModId}",
                Evidence =
                    $"{topProfileMod.ModId} accounted for {topProfileMod.PercentOfProfileTickTime:0.0}% of profiled tick time; " +
                    $"max segment {topProfileMod.MaxSingleMs:0.0}ms; {topProfileMod.SlowSegments} segment(s) >=10ms.",
                NextAction =
                    $"Inspect {topProfileMod.ModId}'s top profiler marker and reproduce the slowdown with that system active/inactive if possible. This is high-confidence profiler attribution, not proof of a bug."
            });
        }

        if (groups.Count >= 2 && signals.Count == 0)
        {
            signals.Add(new ServerSmartSignal
            {
                Category = "Unknown",
                Severity = "Watch",
                Confidence = "Low",
                Score = 45,
                Title = "Lag was recorded without a strong correlated subsystem signal",
                Evidence =
                    $"{groups.Count} grouped lag incident(s) were recorded, but CPU/GC/storage/entity/chunk evidence did not dominate.",
                NextAction =
                    "Run a manual 10s/30s engine profile during the slowdown and inspect Tick Phases, Hotspots, Mods, Slow Ticks, and Off-thread views."
            });
        }

        signals = signals
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => SeverityRank(x.Severity))
            .Take(12)
            .ToList();

        foreach (var signal in signals.Take(5))
        {
            if (!string.IsNullOrWhiteSpace(signal.NextAction) &&
                !recommendations.Contains(signal.NextAction))
                recommendations.Add(signal.NextAction);
        }

        var immediate = source
            .Where(x => x.UnixMilliseconds >= now - 60_000L)
            .ToList();
        if (immediate.Count == 0) immediate = recent.TakeLast(Math.Min(60, recent.Count)).ToList();

        double immediateDelayMedian = Median(immediate.Select(x => x.MainThreadDelayMs));
        double immediateCpuMedian = Median(immediate.Select(x => x.CpuPercent));
        bool recoveredNow = groups.Count > 0 &&
            immediateDelayMedian < Math.Max(75, baseline.DelayAnomalyMs * 0.65) &&
            immediateCpuMedian < Math.Min(85, baseline.CpuAnomalyPercent) &&
            !recentIncidents.Any(x => x.UnixMilliseconds >= now - 60_000L && x.DelayMs >= 250);

        int dataQualityScore = Math.Clamp(
            35 +
            Math.Min(25, 10 + baseline.SampleCount / 12) +
            Math.Min(25, 10 + recent.Count / 12) +
            (profileIsRecent ? 15 : 0),
            0,
            100);
        string analysisConfidence = dataQualityScore >= 85 ? "High" : dataQualityScore >= 65 ? "Medium" : "Low";

        int healthScore = 100;
        healthScore -= Math.Min(35, severeGroups * 18);
        healthScore -= Math.Min(24, highGroups * 10);
        healthScore -= Math.Min(20, delayAnomalyCount / 2);
        if (cpuIncidentCount > 0) healthScore -= Math.Min(15, cpuIncidentCount * 5);
        if (gcIncidentCount > 0) healthScore -= Math.Min(12, gcIncidentCount * 4);
        if (storageIncidentCount > 0) healthScore -= Math.Min(18, storageIncidentCount * 6);
        if (droppedIncrease > 0) healthScore -= 20;
        healthScore -= Math.Min(15, trends.Count(x => x.Severity == "High") * 8);
        if (recoveredNow) healthScore += 8;
        healthScore = Math.Clamp(healthScore, 0, 100);

        string state =
            healthScore < 55 ? "Critical" :
            healthScore < 75 ? "Investigate" :
            healthScore < 90 ? "Watch" :
            "Healthy";

        var primary = signals.FirstOrDefault();

        return new ServerSmartAnalysis
        {
            Available = true,
            State = state,
            HealthScore = healthScore,
            PrimaryFinding = primary?.Title ??
                "No strong lag pattern is currently standing out.",
            PrimaryConfidence = primary?.Confidence ?? "Low",
            AnalysisConfidence = analysisConfidence,
            DataQualityScore = dataQualityScore,
            RecoveryState = recoveredNow ? "Recovered in the last minute" : groups.Count > 0 ? "Active/recent incidents" : "Stable",
            Baseline = baseline,
            Signals = signals,
            Trends = trends
                .OrderByDescending(x => SeverityRank(x.Severity))
                .ThenByDescending(x => Math.Abs(x.SlopePerMinute))
                .Take(8)
                .ToList(),
            IncidentGroups = groups
                .OrderByDescending(x => x.EndUnixMilliseconds)
                .Take(20)
                .ToList(),
            Recommendations = recommendations.Take(6).ToList(),
            RecentDelayMedianMs = recentDelayMedian,
            RecentCpuMedianPercent = recentCpuMedian,
            RecentHeapMedianMb = recentHeapMedian,
            RecentEntityMedian = recentEntityMedian,
            RecentQueueMedian = recentQueueMedian,
            RecentWriterMedianMs = recentWriterMedian
        };
    }

    private IncidentModAttribution ResolveIncidentModAttributionUnsafe(long incidentStart, long incidentEnd)
    {
        if (lastProfile.CompletedUnixMilliseconds <= 0 || lastModHotspots.Count == 0)
            return IncidentModAttribution.None;

        long profileStart = lastProfile.CompletedUnixMilliseconds - Math.Max(1, lastProfile.DurationSeconds) * 1000L;
        bool overlapsIncidentWindow =
            profileStart <= incidentEnd + 15_000L &&
            lastProfile.CompletedUnixMilliseconds >= incidentStart - 5_000L;

        if (!overlapsIncidentWindow)
            return IncidentModAttribution.None;

        // Automatic profiles are started after a specific severe stall. Bind the
        // attribution back to that exact incident instead of merely accepting a
        // nearby profile, which avoids blaming an unrelated mod from later work.
        if (string.Equals(lastProfile.CaptureKind, "Automatic lag incident", StringComparison.OrdinalIgnoreCase))
        {
            long trigger = lastProfile.TriggerIncidentUnixMilliseconds;
            if (trigger <= 0 || trigger < incidentStart - 1_000L || trigger > incidentEnd + 1_000L)
                return IncidentModAttribution.None;
        }

        var candidates = lastModHotspots
            .Where(x =>
                !x.IsCore &&
                !string.IsNullOrWhiteSpace(x.ModId) &&
                x.ModId != "<unattributed>" &&
                x.Confidence != "Low")
            .OrderByDescending(x => x.TotalMs)
            .ToList();

        if (candidates.Count == 0)
            return IncidentModAttribution.None;

        var top = candidates[0];
        var second = candidates.Count > 1 ? candidates[1] : null;
        bool dominant = second == null || second.TotalMs <= 0 || top.TotalMs >= second.TotalMs * 1.35;
        bool high =
            top.Confidence == "High" &&
            dominant &&
            (top.PercentOfProfileTickTime >= 25 || top.MaxSingleMs >= 75 || top.SlowSegments >= 2);
        bool medium =
            top.Confidence != "Low" &&
            (top.PercentOfProfileTickTime >= 10 || top.MaxSingleMs >= 30 || top.SlowSegments >= 1);

        if (!high && !medium)
            return IncidentModAttribution.None;

        string confidence = high ? "High" : "Medium";
        string evidence =
            $"{top.ModId} held {top.PercentOfProfileTickTime:0.0}% of profiled tick time; " +
            $"max segment {top.MaxSingleMs:0.0}ms; {top.SlowSegments} segment(s) >=10ms; " +
            $"top marker {ShortCode(top.TopMarker, 110)}.";

        return new IncidentModAttribution(
            top.ModId,
            string.IsNullOrWhiteSpace(top.ModName) ? top.ModId : top.ModName,
            confidence,
            evidence,
            lastProfile.CaptureKind,
            lastProfile.CompletedUnixMilliseconds,
            true);
    }

    private List<ServerLagIncidentGroup> BuildIncidentGroupsUnsafe(
        List<ServerDiagnosticIncident> source)
    {
        var result = new List<ServerLagIncidentGroup>();
        if (source.Count == 0) return result;

        var ordered = source
            .OrderBy(x => x.UnixMilliseconds)
            .ToList();

        var current = new List<ServerDiagnosticIncident>();

        void Flush()
        {
            if (current.Count == 0) return;

            long start = current[0].UnixMilliseconds;
            long end = current[^1].UnixMilliseconds;
            double worstDelay = current.Max(x => x.DelayMs);
            double maxCpu = current.Max(x => x.CpuPercent);
            int maxQueue = current.Max(x => x.BlockLoggerQueue);
            long maxWriter = current.Max(x => x.BlockLoggerWriterMs);
            long drops = current.Sum(x => x.BlockLoggerDroppedDelta);
            int gen2 = current.Sum(x => x.Gen2Delta);
            int maxEntityDelta = current
                .OrderByDescending(x => Math.Abs(x.EntityDelta))
                .Select(x => x.EntityDelta)
                .FirstOrDefault();
            int maxChunkDelta = current
                .OrderByDescending(x => Math.Abs(x.ChunkDelta))
                .Select(x => x.ChunkDelta)
                .FirstOrDefault();

            int storageScore =
                (maxWriter >= 100 ? 2 : 0) +
                (maxQueue >= 1000 ? 2 : 0) +
                (drops > 0 ? 4 : 0);
            int gcScore = gen2 > 0 ? 3 + Math.Min(3, gen2) : 0;
            int cpuScore = maxCpu >= 95 ? 4 : maxCpu >= 90 ? 3 : 0;
            int entityScore = Math.Abs(maxEntityDelta) >= 1000 ? 4 :
                Math.Abs(maxEntityDelta) >= 500 ? 3 : 0;
            int chunkScore = Math.Abs(maxChunkDelta) >= 600 ? 4 :
                Math.Abs(maxChunkDelta) >= 300 ? 3 : 0;

            var scores = new List<(string Category, int Score)>
            {
                ("Storage / BlockLogger", storageScore),
                ("Garbage collection", gcScore),
                ("CPU saturation", cpuScore),
                ("Entity load", entityScore),
                ("Chunk/world activity", chunkScore)
            }
            .OrderByDescending(x => x.Score)
            .ToList();

            string category;
            string confidence;
            string action;
            if (scores[0].Score <= 0)
            {
                category = "Main-thread / unknown";
                confidence = "Low";
                action = "Run a manual engine profile during a repeat of this incident.";
            }
            else if (scores.Count > 1 &&
                     scores[1].Score > 0 &&
                     scores[0].Score - scores[1].Score <= 1)
            {
                category = $"Mixed: {scores[0].Category} + {scores[1].Category}";
                confidence = "Medium";
                action = "Inspect both correlated signals, then use a manual profile to separate correlation from actual tick ownership.";
            }
            else
            {
                category = scores[0].Category;
                confidence = scores[0].Score >= 5 ? "High" : "Medium";
                action = category switch
                {
                    "Storage / BlockLogger" => "Inspect disk/write latency and BlockLogger queue growth.",
                    "Garbage collection" => "Inspect managed-heap growth and allocation-heavy systems around the event.",
                    "CPU saturation" => "Run a manual engine profile while CPU is saturated.",
                    "Entity load" => "Inspect entity population/type/region and compare with the Animal Limiter report.",
                    "Chunk/world activity" => "Check exploration/worldgen/chunk loading and profile tick phases.",
                    _ => "Run a manual engine profile during a repeat of this incident."
                };
            }

            double saveWindowMs = current.Max(x => x.WorldSaveWindowMs);
            if (saveWindowMs > 0)
            {
                category = category == "Main-thread / unknown" ? "World save (correlated)" : "World save + " + category + " (correlated)";
                confidence = "Medium";
                action = $"A measured world-save window ({saveWindowMs:0}ms) overlapped this sampling interval. Compare repeated save/non-save periods and disk latency; the window includes callback scheduling and does not prove disk I/O or a specific mod caused the stall.";
            }

            var modAttribution = ResolveIncidentModAttributionUnsafe(start, end);
            if (modAttribution.IsAttributed &&
                modAttribution.Confidence == "High" &&
                (category == "Main-thread / unknown" || category == "CPU saturation"))
            {
                category = $"Mod: {modAttribution.ModId}";
                confidence = modAttribution.Confidence;
                action = $"Inspect {modAttribution.ModId} around the profiled hotspot. Attribution is profiler evidence, not automatic proof of a mod defect.";
            }

            string severity =
                drops > 0 || worstDelay >= 2000 ? "Critical" :
                worstDelay >= 500 ? "High" :
                worstDelay >= 250 || current.Count >= 2 ? "Watch" :
                "Info";

            result.Add(new ServerLagIncidentGroup
            {
                StartUnixMilliseconds = start,
                EndUnixMilliseconds = end,
                DurationSeconds = Math.Max(1, (end - start) / 1000d + 1),
                SampleCount = current.Count,
                Severity = severity,
                PrimaryCategory = category,
                Confidence = confidence,
                WorstDelayMs = worstDelay,
                MaxCpuPercent = maxCpu,
                Gen2Collections = gen2,
                MaxBlockLoggerQueue = maxQueue,
                MaxBlockLoggerWriterMs = maxWriter,
                BlockLoggerDropped = drops,
                MaxEntityDelta = maxEntityDelta,
                MaxChunkDelta = maxChunkDelta,
                Evidence = string.Join(
                    " | ",
                    current
                        .Select(x => x.Evidence)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct()
                        .Take(4)),
                NextAction = action,
                LikelyModId = modAttribution.ModId,
                LikelyModName = modAttribution.ModName,
                ModConfidence = modAttribution.Confidence,
                ModEvidence = modAttribution.Evidence,
                ModAttributionSource = modAttribution.Source,
                ModProfileCompletedUnixMilliseconds = modAttribution.ProfileCompletedUnixMilliseconds
            });

            current.Clear();
        }

        foreach (var incident in ordered)
        {
            if (current.Count > 0 &&
                incident.UnixMilliseconds - current[^1].UnixMilliseconds > 5_000)
                Flush();

            current.Add(incident);
        }

        Flush();
        return result;
    }

    private static void AddTrendIfMeaningful(
        List<ServerHealthTrend> output,
        string name,
        string unit,
        double slopePerMinute,
        double netChange,
        double watchSlope,
        double watchNetChange,
        string category,
        string interpretation)
    {
        bool rising =
            slopePerMinute >= watchSlope &&
            netChange >= watchNetChange;

        bool high =
            slopePerMinute >= watchSlope * 3 &&
            netChange >= watchNetChange * 2;

        if (!rising) return;

        output.Add(new ServerHealthTrend
        {
            Name = name,
            Category = category,
            Unit = unit,
            SlopePerMinute = slopePerMinute,
            NetChange = netChange,
            Severity = high ? "High" : "Watch",
            Confidence = high ? "High" : "Medium",
            Interpretation = interpretation
        });
    }

    private static double SlopePerMinute(
        List<ServerHealthSample> source,
        System.Func<ServerHealthSample, double> selector)
    {
        if (source.Count < 2) return 0;

        double firstMs = source[0].UnixMilliseconds;
        double meanX = source.Average(x =>
            (x.UnixMilliseconds - firstMs) / 60_000d);
        double meanY = source.Average(selector);

        double numerator = 0;
        double denominator = 0;
        foreach (var row in source)
        {
            double x = (row.UnixMilliseconds - firstMs) / 60_000d;
            double y = selector(row);
            numerator += (x - meanX) * (y - meanY);
            denominator += (x - meanX) * (x - meanX);
        }

        return denominator <= 0 ? 0 : numerator / denominator;
    }

    private static double Median(IEnumerable<double> source)
    {
        var ordered = source
            .Where(double.IsFinite)
            .OrderBy(x => x)
            .ToList();

        if (ordered.Count == 0) return 0;
        int middle = ordered.Count / 2;
        return (ordered.Count & 1) == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2d;
    }

    private static double MedianAbsoluteDeviation(
        IEnumerable<double> source,
        double median)
    {
        return Median(source.Select(x => Math.Abs(x - median)));
    }

    private static int SeverityRank(string severity) =>
        severity switch
        {
            "Critical" => 4,
            "High" => 3,
            "Watch" => 2,
            "Info" => 1,
            _ => 0
        };

    private ServerDiagnosticSummary BuildDiagnosticSummaryUnsafe(
        List<ServerHealthSample> source)
    {
        if (!enabled || source.Count == 0)
        {
            return new ServerDiagnosticSummary
            {
                State = enabled ? "Collecting" : "Disabled",
                WindowMinutes = 5,
                Summary = enabled
                    ? "Waiting for enough health samples."
                    : "Profiler diagnostics are disabled."
            };
        }

        long cutoff = DateTimeOffset.UtcNow
            .AddMinutes(-5)
            .ToUnixTimeMilliseconds();

        var recent = source
            .Where(x => x.UnixMilliseconds >= cutoff)
            .ToList();

        if (recent.Count == 0)
            recent = source.TakeLast(Math.Min(300, source.Count)).ToList();

        int stallCount = recent.Count(x => x.MainThreadDelayMs >= 150);
        int severeStallCount = recent.Count(x => x.MainThreadDelayMs >= 500);
        int cpuPressureCount = recent.Count(x => x.CpuPercent >= 90);
        int writerPressureCount = recent.Count(x => x.BlockLoggerWriterMs >= 100);
        int queuePressureCount = recent.Count(x => x.BlockLoggerQueue >= 1000);
        double worstDelay = recent.Max(x => x.MainThreadDelayMs);
        double avgCpu = recent.Average(x => x.CpuPercent);
        double maxCpu = recent.Max(x => x.CpuPercent);
        int maxQueue = recent.Max(x => x.BlockLoggerQueue);
        long maxWriter = recent.Max(x => x.BlockLoggerWriterMs);
        int maxEntities = recent.Max(x => x.LoadedEntities);
        int maxChunks = recent.Max(x => x.LoadedChunks);
        long droppedIncrease = Math.Max(
            0,
            recent[^1].BlockLoggerDropped - recent[0].BlockLoggerDropped);
        int gen2Increase = Math.Max(
            0,
            recent[^1].Gen2Collections - recent[0].Gen2Collections);

        var signals = new List<string>();

        if (severeStallCount > 0)
            signals.Add($"{severeStallCount} scheduler stall(s) >=500ms");
        else if (stallCount >= 2)
            signals.Add($"{stallCount} scheduler delay sample(s) >=150ms");

        if (cpuPressureCount >= 5)
            signals.Add($"CPU >=90% for {cpuPressureCount} sample(s), peak {maxCpu:0}%");

        // Gen2 collection by itself is normal. Only surface it in the compact lag
        // summary when repeated scheduler delays were present in the same window.
        if (gen2Increase > 0 && stallCount >= 2)
            signals.Add($"Gen2 GC increased by {gen2Increase} during delayed samples");

        if (writerPressureCount >= 3)
            signals.Add($"BlockLogger writer >=100ms for {writerPressureCount} sample(s), max {maxWriter}ms");

        if (queuePressureCount >= 5)
            signals.Add($"BlockLogger queue >=1,000 for {queuePressureCount} sample(s), max {maxQueue:N0}");

        if (droppedIncrease > 0)
            signals.Add($"BlockLogger dropped {droppedIncrease:N0} event(s)");

        if (maxEntities >= 20_000)
            signals.Add($"loaded entities peaked at {maxEntities:N0}");

        string state =
            droppedIncrease > 0 ||
            severeStallCount > 0 ||
            stallCount >= 4 ||
            cpuPressureCount >= 15 ||
            writerPressureCount >= 5 ||
            queuePressureCount >= 10
                ? "Investigate"
                : stallCount >= 2 ||
                  cpuPressureCount >= 5 ||
                  writerPressureCount >= 3 ||
                  queuePressureCount >= 5
                    ? "Watch"
                    : "Stable";

        string summary = signals.Count == 0
            ? "No strong lag-pressure signals were detected in the last five minutes."
            : string.Join(" | ", signals.Take(6));

        return new ServerDiagnosticSummary
        {
            State = state,
            WindowMinutes = 5,
            Summary = summary,
            StallCount = stallCount,
            SevereStallCount = severeStallCount,
            WorstDelayMs = worstDelay,
            AverageCpuPercent = avgCpu,
            MaxCpuPercent = maxCpu,
            Gen2Collections = gen2Increase,
            MaxBlockLoggerQueue = maxQueue,
            MaxBlockLoggerWriterMs = maxWriter,
            BlockLoggerDroppedIncrease = droppedIncrease,
            MaxLoadedEntities = maxEntities,
            MaxLoadedChunks = maxChunks
        };
    }

    private void CaptureProfilerTick()
    {
        lock (sync)
        {
            if (!profileRunning) return;

            var root = api.World.FrameProfiler.PrevRootEntry;
            if (root == null || root.Start == lastProfileRootStart) return;

            lastProfileRootStart = root.Start;
            double tickMs = TicksToMs(root.ElapsedTicks);
            if (tickMs < 0 || tickMs >= 60_000) return;

            profileTickDurationsMs.Add(tickMs);

            // Root-level marks/ranges are the closest thing the engine exposes to
            // a "what parts made up this tick" breakdown.
            CaptureRootPhases(root);

            // Leaf segments are used for hotspot and mod attribution rankings.
            // Inclusive parent ranges are deliberately not aggregated here.
            profileLeafBuffer.Clear();
            CaptureLeafWork(root, "", 0, profileLeafBuffer);

            foreach (var leaf in profileLeafBuffer)
                AddProfileValue(leaf.Path, leaf.ElapsedMs, leaf.Calls);

            // Preserve the worst profiled ticks with their leading causes.
            if (tickMs >= 50)
            {
                var leading = TopProfileLeaves(profileLeafBuffer, 1).FirstOrDefault();
                string marker = leading.Path ?? "<no leaf markers>";
                if (!capturedSlowCauses.ContainsKey(marker) && capturedSlowCauses.Count >= 256) marker = "<other leading markers>";
                if (!capturedSlowCauses.TryGetValue(marker, out var cause))
                    capturedSlowCauses[marker] = cause = new ServerProfileSlowCause { Marker = marker };
                cause.Count++;
                cause.WorstTickMs = Math.Max(cause.WorstTickMs, tickMs);
                var causes = TopProfileLeaves(profileLeafBuffer, 4)
                    .Select(x => $"{ShortCode(x.Path, 72)} ({x.ElapsedMs:0.0}ms)")
                    .ToList();

                capturedSlowProfileTicks.Add(new ServerProfileSlowTick
                {
                    UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    TickMs = tickMs,
                    TopCauseSummary = causes.Count == 0
                        ? "No leaf profiler markers were exposed for this tick."
                        : string.Join(" | ", causes)
                });

                if (capturedSlowProfileTicks.Count > 60)
                {
                    int smallest = 0;
                    for (int i = 1; i < capturedSlowProfileTicks.Count; i++)
                    {
                        if (capturedSlowProfileTicks[i].TickMs <
                            capturedSlowProfileTicks[smallest].TickMs)
                            smallest = i;
                    }
                    capturedSlowProfileTicks.RemoveAt(smallest);
                }
            }
        }
    }

    private static List<ProfileLeafSample> TopProfileLeaves(List<ProfileLeafSample> source, int count)
    {
        var top = new List<ProfileLeafSample>(Math.Max(0, count));
        if (count <= 0) return top;

        foreach (var item in source)
        {
            int insert = top.Count;
            for (int i = 0; i < top.Count; i++)
            {
                if (item.ElapsedMs > top[i].ElapsedMs)
                {
                    insert = i;
                    break;
                }
            }

            if (insert >= count) continue;
            top.Insert(insert, item);
            if (top.Count > count) top.RemoveAt(top.Count - 1);
        }
        return top;
    }

    private void CaptureRootPhases(ProfileEntryRange root)
    {
        if (root.Marks != null)
        {
            foreach (var pair in root.Marks)
            {
                AddPhaseValue(pair.Key, pair.Value.ElapsedTicks, pair.Value.CallCount);
            }
        }

        if (root.ChildRanges == null) return;

        foreach (var child in root.ChildRanges.Values)
        {
            if (child == null) continue;
            AddPhaseValue(child.Code ?? "unnamed", child.ElapsedTicks, child.CallCount);
        }
    }

    private void CaptureLeafWork(
        ProfileEntryRange entry,
        string currentPath,
        int depth,
        List<ProfileLeafSample> output)
    {
        if (entry == null || depth > 6 || entry.ElapsedTicks <= 0 ||
            output.Count >= profileLeafLimit) return;

        // FrameProfilerUtil.Leave() resets LastMark on all parents when a child
        // range exits. Therefore parent Mark() segments and child Enter()/Leave()
        // ranges are separate timing pieces. We can safely partition a range into:
        // explicit marks + recursively expanded child ranges + residual unscoped time.
        long accountedTicks = 0;
        string prefix = string.IsNullOrWhiteSpace(currentPath)
            ? ""
            : currentPath + " / ";

        if (entry.Marks != null)
        {
            foreach (var pair in entry.Marks)
            {
                if (pair.Value == null || pair.Value.ElapsedTicks <= 0) continue;

                if (output.Count >= profileLeafLimit) break;
                accountedTicks += pair.Value.ElapsedTicks;
                output.Add(new ProfileLeafSample(
                    prefix + pair.Key,
                    TicksToMs(pair.Value.ElapsedTicks),
                    Math.Max(1, pair.Value.CallCount)));
            }
        }

        if (entry.ChildRanges != null)
        {
            foreach (var child in entry.ChildRanges.Values)
            {
                if (output.Count >= profileLeafLimit) break;
                if (child == null || child.ElapsedTicks <= 0) continue;

                accountedTicks += child.ElapsedTicks;
                string childPath = string.IsNullOrWhiteSpace(currentPath)
                    ? (child.Code ?? "unnamed")
                    : currentPath + " / " + (child.Code ?? "unnamed");

                CaptureLeafWork(child, childPath, depth + 1, output);
            }
        }

        // Preserve any profiler time not represented by an explicit Mark() or
        // child range. This lets the coverage figures stay honest instead of
        // silently dropping uninstrumented work.
        long residualTicks = Math.Max(0, entry.ElapsedTicks - accountedTicks);
        if (residualTicks > 0 && output.Count < profileLeafLimit)
        {
            string residualPath = string.IsNullOrWhiteSpace(currentPath)
                ? "<root / unscoped>"
                : currentPath + " / <self / unscoped>";

            output.Add(new ProfileLeafSample(
                residualPath,
                TicksToMs(residualTicks),
                1));
        }
    }

    private void AddProfileValue(string code, double ms, int calls)
    {
        if (string.IsNullOrWhiteSpace(code) || ms <= 0) return;

        if (!profileAccumulators.TryGetValue(code, out var acc))
        {
            if (profileAccumulators.Count >= MaxProfileAccumulatorKeys)
                code = OverflowProfileKey;

            if (!profileAccumulators.TryGetValue(code, out acc))
            {
                acc = new ProfileAccumulator();
                profileAccumulators[code] = acc;
            }
        }

        acc.TotalMs += ms;
        acc.MaxSingleMs = Math.Max(acc.MaxSingleMs, ms);
        acc.Calls += Math.Max(1, calls);
    }

    private void AddPhaseValue(string code, long elapsedTicks, int calls)
    {
        if (string.IsNullOrWhiteSpace(code) || elapsedTicks <= 0) return;

        double ms = TicksToMs(elapsedTicks);
        if (!phaseAccumulators.TryGetValue(code, out var acc))
        {
            if (phaseAccumulators.Count >= MaxPhaseAccumulatorKeys)
                code = OverflowPhaseKey;

            if (!phaseAccumulators.TryGetValue(code, out acc))
            {
                acc = new ProfileAccumulator();
                phaseAccumulators[code] = acc;
            }
        }

        acc.TotalMs += ms;
        acc.MaxSingleMs = Math.Max(acc.MaxSingleMs, ms);
        acc.Calls += Math.Max(1, calls);
    }

    private void RebuildModAccumulatorsFromProfilePaths()
    {
        modAccumulators.Clear();
        foreach (var pair in profileAccumulators)
        {
            var attribution = ResolveModAttribution(pair.Key);
            var source = pair.Value;
            string key = attribution.IsAttributed ? attribution.ModId : "<unattributed>";
            if (!modAccumulators.TryGetValue(key, out var acc))
            {
                acc = new ModProfileAccumulator
                {
                    ModId = key,
                    ModName = attribution.IsAttributed ? attribution.ModName : "Unattributed / engine-generic"
                };
                modAccumulators[key] = acc;
            }

            acc.TotalMs += source.TotalMs;
            acc.MaxSingleMs = Math.Max(acc.MaxSingleMs, source.MaxSingleMs);
            acc.Calls += Math.Max(1, source.Calls);
            if (source.MaxSingleMs >= 10) acc.SlowSegments++;
            if (attribution.Confidence == "High") acc.HighConfidenceMs += source.TotalMs;
            else if (attribution.Confidence == "Medium") acc.MediumConfidenceMs += source.TotalMs;
            else acc.LowConfidenceMs += source.TotalMs;

            if (source.MaxSingleMs > acc.TopMarkerMs)
            {
                acc.TopMarkerMs = source.MaxSingleMs;
                acc.TopMarker = pair.Key;
            }
        }
    }

    private List<ModIdentity> BuildModIdentityCatalog()
    {
        var result = new List<ModIdentity>();

        foreach (var mod in api.ModLoader.Mods)
        {
            if (mod?.Info == null) continue;

            string modId = mod.Info.ModID ?? "";
            if (string.IsNullOrWhiteSpace(modId)) continue;

            var identity = new ModIdentity
            {
                ModId = modId,
                Name = string.IsNullOrWhiteSpace(mod.Info.Name)
                    ? modId
                    : mod.Info.Name,
                IsCore = mod.Info.CoreMod
            };

            identity.Tokens.Add(new ModToken(modId + ":", 120, "High"));
            identity.Tokens.Add(new ModToken(
                modId,
                85,
                modId.Length >= 6 ? "High" : "Medium"));

            string normalizedName = NormalizeToken(mod.Info.Name);
            if (normalizedName.Length >= 5)
                identity.Tokens.Add(new ModToken(normalizedName, 45, "Medium"));

            if (!string.IsNullOrWhiteSpace(mod.SourcePath))
            {
                string sourceName = System.IO.Path.GetFileNameWithoutExtension(mod.SourcePath);
                if (!string.IsNullOrWhiteSpace(sourceName) && sourceName.Length >= 5)
                    identity.Tokens.Add(new ModToken(sourceName, 55, "Medium"));
            }

            foreach (var system in mod.Systems)
            {
                if (system == null) continue;

                var type = system.GetType();
                string? fullName = type.FullName;
                string? ns = type.Namespace;
                string? assemblyName = type.Assembly.GetName().Name;

                if (!string.IsNullOrWhiteSpace(fullName))
                    identity.Tokens.Add(new ModToken(fullName, 115, "High"));
                if (!string.IsNullOrWhiteSpace(ns) && ns.Length >= 5)
                    identity.Tokens.Add(new ModToken(ns, 105, "High"));
                if (!string.IsNullOrWhiteSpace(type.Name) && type.Name.Length >= 5)
                    identity.Tokens.Add(new ModToken(type.Name, 95, "High"));
                if (!string.IsNullOrWhiteSpace(assemblyName) && assemblyName.Length >= 5)
                    identity.Tokens.Add(new ModToken(assemblyName, 100, "High"));
            }

            // Some entity/AI handlers live outside their ModSystem namespace. Include
            // concrete types from the mod's own assemblies, never engine assemblies.
            foreach (var assembly in mod.Systems.Where(x => x != null).Select(x => x.GetType().Assembly).Distinct())
            {
                string name = assembly.GetName().Name ?? "";
                if (name.StartsWith("Vintagestory", StringComparison.OrdinalIgnoreCase)) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).Cast<Type>().ToArray(); }
                catch { continue; }
                foreach (var type in types.Take(256))
                {
                    if (type.IsNested || type.Name.Contains('<') || string.IsNullOrWhiteSpace(type.FullName)) continue;
                    identity.Tokens.Add(new ModToken(type.FullName!, 115, "High"));
                    if (type.Name.Length >= 8) identity.Tokens.Add(new ModToken(type.Name, 90, "Medium"));
                }
            }
            var unique = identity.Tokens.GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderByDescending(t => t.Score).First()).ToList();
            identity.Tokens.Clear();
            identity.Tokens.AddRange(unique);
            result.Add(identity);
        }

        return result;
    }

    private ModAttribution ResolveModAttribution(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ModAttribution.Unattributed;
        if (profileAttributionCache.TryGetValue(path, out ModAttribution? cached) && cached != null) return cached;
        ModAttribution resolved = ResolveModAttributionCore(path);
        if (profileAttributionCache.Count < 8192) profileAttributionCache[path] = resolved;
        return resolved;
    }

    private ModAttribution ResolveModAttributionCore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ModAttribution.Unattributed;

        int bestScore = int.MinValue;
        ModIdentity? best = null;
        string bestConfidence = "Low";
        bool tied = false;
        string? normalizedPath = null;

        foreach (var mod in modIdentities)
        {
            foreach (var token in mod.Tokens)
            {
                if (!ContainsProfilerToken(path, token, ref normalizedPath)) continue;

                int score = token.Score + Math.Min(30, token.Value.Length);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = mod;
                    bestConfidence = token.Confidence;
                    tied = false;
                }
                else if (score == bestScore &&
                         best != null &&
                         !string.Equals(best.ModId, mod.ModId, StringComparison.OrdinalIgnoreCase))
                {
                    tied = true;
                }
            }
        }

        if (best == null)
        {
            if (path.Contains("game:", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Vintagestory.", StringComparison.OrdinalIgnoreCase))
            {
                return new ModAttribution(
                    "game",
                    "Vintage Story / game",
                    "High",
                    true,
                    true);
            }

            return ModAttribution.Unattributed;
        }

        if (tied)
            return new ModAttribution(
                "<ambiguous>",
                "Ambiguous mod attribution",
                "Low",
                false,
                false);

        return new ModAttribution(
            best.ModId,
            best.Name,
            bestConfidence,
            true,
            best.IsCore);
    }

    private static bool ContainsProfilerToken(string haystack, ModToken candidate, ref string? normalizedPath)
    {
        string token = candidate.Value;
        if (string.IsNullOrWhiteSpace(token) || token.Length < 3) return false;

        // Domain/full namespace/type tokens are safe as substring matches.
        if (token.Contains(':') || token.Contains('.') || token.Contains('/'))
            return haystack.Contains(token, StringComparison.OrdinalIgnoreCase);

        int index = haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            bool leftOk = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
            int rightIndex = index + token.Length;
            bool rightOk = rightIndex >= haystack.Length ||
                           !char.IsLetterOrDigit(haystack[rightIndex]);

            if (leftOk && rightOk) return true;
            index = haystack.IndexOf(
                token,
                index + 1,
                StringComparison.OrdinalIgnoreCase);
        }

        // A normalized mod name/source filename can appear embedded inside a
        // profiler marker. Only allow that for longer tokens.
        if (token.Length < 8) return false;
        // A capture can contain thousands of paths and hundreds of tokens per mod.
        // Normalize each marker at most once, and each catalog token once at creation.
        normalizedPath ??= NormalizeToken(haystack);
        return normalizedPath.Contains(candidate.NormalizedValue, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        Span<char> buffer = value.Length <= 512 ? stackalloc char[value.Length] : new char[value.Length];
        int count = 0;
        foreach (char c in value)
            if (char.IsLetterOrDigit(c)) buffer[count++] = char.ToLowerInvariant(c);
        return new string(buffer[..count]);
    }

    private static string ShortCode(string? value, int maxLength)
    {
        string text = value ?? "";
        if (text.Length <= maxLength) return text;
        if (maxLength <= 3) return text.Substring(0, Math.Min(text.Length, maxLength));
        return "..." + text.Substring(text.Length - (maxLength - 3));
    }

    private void CollectOffThreadReports()
    {
        var reports = new List<ServerProfilerOffThreadReport>();

        foreach (var raw in FrameProfilerUtil.offThreadProfiles.ToArray())
        {
            if (string.IsNullOrWhiteSpace(raw) || offThreadBaseline.Contains(raw))
                continue;

            double durationMs = ExtractOffThreadDurationMs(raw);
            var attribution = ResolveModAttribution(raw);
            string compact = string.Join(
                " | ",
                raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(x => x.Trim())
                   .Where(x => x.Length > 0)
                   .Take(5));

            if (compact.Length > 700)
                compact = compact.Substring(0, 697) + "...";

            reports.Add(new ServerProfilerOffThreadReport
            {
                DurationMs = durationMs,
                Summary = compact,
                ModId = attribution.IsAttributed ? attribution.ModId : "",
                Confidence = attribution.Confidence
            });
        }

        lastOffThreadReports = reports
            .OrderByDescending(x => x.DurationMs)
            .ThenBy(x => x.ModId, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
    }

    private static double ExtractOffThreadDurationMs(string raw)
    {
        const string needle = "A tick took ";
        int start = raw.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return 0;

        start += needle.Length;
        int end = raw.IndexOf(" ms", start, StringComparison.OrdinalIgnoreCase);
        if (end <= start) return 0;

        string value = raw.Substring(start, end - start).Trim();
        if (double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double invariant))
            return invariant;

        return double.TryParse(value, out double current) ? current : 0;
    }

    private void RestoreOffThreadProfilerSettings()
    {
        if (!offThreadSettingsCaptured) return;

        FrameProfilerUtil.PrintSlowTicks_Offthreads = offThreadPrintWasEnabled;
        FrameProfilerUtil.PrintSlowTicksThreshold_Offthreads = offThreadThresholdWas;
        offThreadBaseline.Clear();
        offThreadSettingsCaptured = false;
    }

    private void ReleaseManualProfileWorkingSetUnsafe()
    {
        // The published profile result lists are independent DTOs. Once a capture
        // finishes or is cancelled, retaining raw profiler-path dictionaries and
        // the temporary mod/off-thread catalogs only wastes memory until next run.
        profileAccumulators.Clear();
        phaseAccumulators.Clear();
        modAccumulators.Clear();
        profileTickDurationsMs.Clear();
        capturedSlowProfileTicks.Clear();
        capturedSlowCauses.Clear();
        profileLeafBuffer.Clear();
        if (profileLeafBuffer.Capacity > ManualMaxProfileLeafSamplesPerTick)
            profileLeafBuffer.Capacity = ManualMaxProfileLeafSamplesPerTick;
        modIdentities = new List<ModIdentity>();
        profileAttributionCache.Clear();
        offThreadBaseline.Clear();
    }

    private void FinishManualProfileSafe()
    {
        try
        {
            FinishManualProfile();
        }
        catch (Exception ex)
        {
            api.Logger.Error(
                "[ServerHealth] engine profile finalization failed: {0}: {1}",
                ex.GetType().Name,
                ex.Message);

            // A profiler failure must never be allowed to take down the server.
            // Best-effort cleanup also restores the global profiler/off-thread
            // settings so a failed capture cannot leave expensive diagnostics on.
            lock (sync)
            {
                try
                {
                    StopManualProfileUnsafe();
                }
                catch (Exception cleanupEx)
                {
                    api.Logger.Warning(
                        "[ServerHealth] profiler cleanup after failure also failed: {0}: {1}",
                        cleanupEx.GetType().Name,
                        cleanupEx.Message);
                }
            }
        }
    }

    private void FinishManualProfile()
    {
        lock (sync)
        {
            if (!profileRunning) return;

            CaptureProfilerTick();

            if (profileListenerId >= 0)
            {
                api.Event.UnregisterGameTickListener(profileListenerId);
                profileListenerId = -1;
            }

            api.World.FrameProfiler.Enabled = profilerWasEnabled;
            if (!profileAutomatic)
                CollectOffThreadReports();
            else
                lastOffThreadReports = new List<ServerProfilerOffThreadReport>();
            RestoreOffThreadProfilerSettings();
            profileRunning = false;

            var orderedTicks = profileTickDurationsMs
                .Where(x => x >= 0 && x < 60_000)
                .OrderBy(x => x)
                .ToList();

            double totalTickMs = orderedTicks.Sum();

            // Attribution and its mod-token catalog are deliberately deferred until capture
            // finalization so neither catalog construction nor token matching runs inside the
            // lagging capture window.
            modIdentities = BuildModIdentityCatalog();
            RebuildModAccumulatorsFromProfilePaths();

            int attributedSegments = modAccumulators
                .Where(x => x.Key != "<unattributed>")
                .Sum(x => x.Value.Calls > int.MaxValue ? int.MaxValue : (int)x.Value.Calls);
            int unattributedSegments = modAccumulators.TryGetValue("<unattributed>", out var unattributed)
                ? (unattributed.Calls > int.MaxValue ? int.MaxValue : (int)unattributed.Calls)
                : 0;

            double attributedMs = modAccumulators
                .Where(x => x.Key != "<unattributed>")
                .Sum(x => x.Value.TotalMs);
            double unattributedMs = unattributed?.TotalMs ?? 0;

            lastProfile = new ServerProfileSummary
            {
                CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DurationSeconds = profileDurationSeconds,
                TickCount = orderedTicks.Count,
                AverageTickMs = orderedTicks.Count == 0 ? 0 : orderedTicks.Average(),
                P95TickMs = Percentile(orderedTicks, 0.95),
                P99TickMs = Percentile(orderedTicks, 0.99),
                MaxTickMs = orderedTicks.Count == 0 ? 0 : orderedTicks[^1],
                AttributedSegments = attributedSegments,
                UnattributedSegments = unattributedSegments,
                AttributedMs = attributedMs,
                UnattributedMs = unattributedMs,
                CaptureKind = profileCaptureKind,
                TriggerIncidentUnixMilliseconds = profileTriggerIncidentUnixMilliseconds
            };

            lastHotspots = profileAccumulators
                .Select(pair =>
                {
                    var attribution = ResolveModAttribution(pair.Key);
                    return new ServerProfilerHotspot
                    {
                        Code = pair.Key,
                        TotalMs = pair.Value.TotalMs,
                        MaxSingleMs = pair.Value.MaxSingleMs,
                        Calls = pair.Value.Calls,
                        PercentOfProfileTickTime = totalTickMs <= 0
                            ? 0
                            : Math.Clamp(pair.Value.TotalMs / totalTickMs * 100d, 0, 999),
                        ModId = attribution.IsAttributed
                            ? attribution.ModId
                            : "",
                        ModName = attribution.IsAttributed
                            ? attribution.ModName
                            : "",
                        AttributionConfidence = attribution.Confidence,
                        IsCore = attribution.IsCore
                    };
                })
                .OrderByDescending(x => x.TotalMs)
                .Take(200)
                .ToList();

            lastModHotspots = modAccumulators
                .Select(pair =>
                {
                    var acc = pair.Value;
                    string confidence =
                        acc.TotalMs <= 0 ? "Low" :
                        acc.HighConfidenceMs / acc.TotalMs >= 0.70 ? "High" :
                        (acc.HighConfidenceMs + acc.MediumConfidenceMs) / acc.TotalMs >= 0.70 ? "Medium" :
                        "Low";

                    var identity = modIdentities.FirstOrDefault(x =>
                        x.ModId.Equals(acc.ModId, StringComparison.OrdinalIgnoreCase));

                    return new ServerProfilerModHotspot
                    {
                        ModId = acc.ModId,
                        ModName = acc.ModName,
                        TotalMs = acc.TotalMs,
                        MaxSingleMs = acc.MaxSingleMs,
                        Calls = acc.Calls,
                        SlowSegments = acc.SlowSegments,
                        PercentOfProfileTickTime = totalTickMs <= 0
                            ? 0
                            : Math.Clamp(acc.TotalMs / totalTickMs * 100d, 0, 999),
                        Confidence = confidence,
                        TopMarker = acc.TopMarker,
                        IsCore = identity?.IsCore ?? acc.ModId == "game"
                    };
                })
                .OrderByDescending(x => x.TotalMs)
                .Take(100)
                .ToList();

            lastPhases = phaseAccumulators
                .Select(pair =>
                {
                    var attribution = ResolveModAttribution(pair.Key);
                    return new ServerProfilerPhase
                    {
                        Code = pair.Key,
                        TotalMs = pair.Value.TotalMs,
                        AveragePerTickMs = orderedTicks.Count == 0
                            ? 0
                            : pair.Value.TotalMs / orderedTicks.Count,
                        MaxSingleMs = pair.Value.MaxSingleMs,
                        Calls = pair.Value.Calls,
                        PercentOfProfileTickTime = totalTickMs <= 0
                            ? 0
                            : Math.Clamp(pair.Value.TotalMs / totalTickMs * 100d, 0, 999),
                        ModId = attribution.IsAttributed ? attribution.ModId : "",
                        AttributionConfidence = attribution.Confidence
                    };
                })
                .OrderByDescending(x => x.TotalMs)
                .Take(120)
                .ToList();

            lastSlowCauses = capturedSlowCauses.Values.Select(c => {
                var owner = ResolveModAttribution(c.Marker);
                return new ServerProfileSlowCause { Marker = c.Marker, Count = c.Count, WorstTickMs = c.WorstTickMs,
                    ModId = owner.IsAttributed ? owner.ModId : "", Confidence = owner.Confidence };
            }).OrderByDescending(c => c.Count).ThenByDescending(c => c.WorstTickMs).ToList();
            lastProfileSlowTicks = capturedSlowProfileTicks
                .OrderByDescending(x => x.TickMs)
                .Take(60)
                .ToList();

            cachedSmartAnalysisUnixMilliseconds = 0;

            api.Logger.Notification(
                "[ServerHealth] {0}s engine profile complete. ticks={1}, avg={2:0.00}ms, p95={3:0.00}ms, p99={4:0.00}ms, max={5:0.00}ms, mods={6}, attributed={7}, unattributed={8}",
                profileDurationSeconds,
                lastProfile.TickCount,
                lastProfile.AverageTickMs,
                lastProfile.P95TickMs,
                lastProfile.P99TickMs,
                lastProfile.MaxTickMs,
                modIdentities.Count,
                lastProfile.AttributedSegments,
                lastProfile.UnattributedSegments);

            profileAutomatic = false;
            profileLeafLimit = ManualMaxProfileLeafSamplesPerTick;
            ReleaseManualProfileWorkingSetUnsafe();
        }
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        int index = Math.Clamp(
            (int)Math.Ceiling(p * sorted.Count) - 1,
            0,
            sorted.Count - 1);
        return sorted[index];
    }

    private static double TicksToMs(long ticks) =>
        ticks / (double)Stopwatch.Frequency * 1000d;

    private static List<ServerHealthSample> Downsample(
        List<ServerHealthSample> source,
        int maxPoints)
    {
        if (source.Count <= maxPoints) return source;
        int step = Math.Max(1, (int)Math.Ceiling(source.Count / (double)maxPoints));
        var result = new List<ServerHealthSample>(maxPoints + 1);

        for (int i = 0; i < source.Count; i += step)
            result.Add(source[i]);

        if (result.Count == 0 || result[^1].UnixMilliseconds != source[^1].UnixMilliseconds)
            result.Add(source[^1]);

        return result;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (!started) return;
            started = false;
            api.Event.GameWorldSave -= OnWorldSave;
            worldSaveWindows.Clear();

            enabled = false;
            StopSamplingUnsafe();
            StopManualProfileUnsafe();
            process.Dispose();
        }
    }

    private sealed class ProfileAccumulator
    {
        public double TotalMs;
        public double MaxSingleMs;
        public long Calls;
    }

    private sealed class ModProfileAccumulator
    {
        public string ModId = "";
        public string ModName = "";
        public double TotalMs;
        public double MaxSingleMs;
        public long Calls;
        public int SlowSegments;
        public double HighConfidenceMs;
        public double MediumConfidenceMs;
        public double LowConfidenceMs;
        public string TopMarker = "";
        public double TopMarkerMs;
    }

    private sealed class ModIdentity
    {
        public string ModId = "";
        public string Name = "";
        public bool IsCore;
        public List<ModToken> Tokens { get; } = new();
    }

    private sealed record ModToken(string Value, int Score, string Confidence)
    {
        public string NormalizedValue { get; } = NormalizeToken(Value);
    }
    private readonly record struct ProfileLeafSample(string Path, double ElapsedMs, int Calls);

    private sealed record ModAttribution(
        string ModId,
        string ModName,
        string Confidence,
        bool IsAttributed,
        bool IsCore)
    {
        public static readonly ModAttribution Unattributed =
            new("", "", "Low", false, false);
    }

    private sealed record IncidentModAttribution(
        string ModId,
        string ModName,
        string Confidence,
        string Evidence,
        string Source,
        long ProfileCompletedUnixMilliseconds,
        bool IsAttributed)
    {
        public static readonly IncidentModAttribution None =
            new("", "", "Low", "", "", 0, false);
    }
}

public sealed class ServerHealthLiveState
{
    public bool Enabled { get; set; }
    public ServerHealthSample Current { get; set; } = new();
    public bool ProfileRunning { get; set; }
    public int ProfileDurationSeconds { get; set; }
    public int ProfileRemainingSeconds { get; set; }
}

public sealed class ServerHealthSnapshot
{
    public bool Enabled { get; set; }
    public ServerHealthSample Current { get; set; } = new();
    public List<ServerHealthSample> Samples { get; set; } = new();
    public List<ServerSlowSpike> SlowSpikes { get; set; } = new();
    public List<ServerDiagnosticIncident> DiagnosticIncidents { get; set; } = new();
    public ServerDiagnosticSummary DiagnosticSummary { get; set; } = new();
    public ServerSmartAnalysis SmartAnalysis { get; set; } = new();
    public List<ServerProfilerHotspot> Hotspots { get; set; } = new();
    public List<ServerProfilerModHotspot> ModHotspots { get; set; } = new();
    public List<ServerProfilerPhase> Phases { get; set; } = new();
    public List<ServerProfileSlowTick> ProfileSlowTicks { get; set; } = new();
    public List<ServerProfileSlowCause> ProfileSlowCauses { get; set; } = new();
    public List<ServerProfilerOffThreadReport> OffThreadReports { get; set; } = new();
    public int LoadedModCount { get; set; }
    public bool ProfileRunning { get; set; }
    public int ProfileDurationSeconds { get; set; }
    public int ProfileRemainingSeconds { get; set; }
    public bool AutomaticIncidentProfilingConfigured { get; set; }
    public bool AutomaticIncidentProfilingEnabled { get; set; }
    public int AutomaticIncidentProfileDurationSeconds { get; set; }
    public int AutomaticIncidentProfileCooldownSeconds { get; set; }
    public long LastAutomaticIncidentProfileUnixMilliseconds { get; set; }
    public long LastAutomaticIncidentTriggerUnixMilliseconds { get; set; }
    public double LastAutomaticIncidentTriggerDelayMs { get; set; }
    public ServerProfileSummary LastProfile { get; set; } = new();
}

public sealed class ServerHealthSample
{
    public long UnixMilliseconds { get; set; }
    public double MainThreadDelayMs { get; set; }
    public double CpuPercent { get; set; }
    public double WorkingSetMb { get; set; }
    public double PrivateMemoryMb { get; set; }
    public double ManagedHeapMb { get; set; }
    public double GcCommittedMb { get; set; }
    public double GcFragmentedMb { get; set; }
    public double EstimatedNativeMb { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public int Players { get; set; }
    public int LoadedEntities { get; set; }
    public int LoadedChunks { get; set; }
    public int BlockLoggerQueue { get; set; }
    public long BlockLoggerWriterMs { get; set; }
    public long BlockLoggerDropped { get; set; }
    public int AnimalTrackedEntities { get; set; }
    public int AnimalTrackedRegions { get; set; }
}

public sealed class ServerSlowSpike
{
    public long UnixMilliseconds { get; set; }
    public double DelayMs { get; set; }
    public double CpuPercent { get; set; }
    public double WorkingSetMb { get; set; }
    public int Players { get; set; }
    public int LoadedEntities { get; set; }
    public int LoadedChunks { get; set; }
    public int BlockLoggerQueue { get; set; }
    public long BlockLoggerWriterMs { get; set; }
    public int Gen0Delta { get; set; }
    public int Gen1Delta { get; set; }
    public int Gen2Delta { get; set; }
}

public sealed class ServerDiagnosticIncident
{
    public double WorldSaveWindowMs { get; set; }
    public long WorldSaveStartedUnixMilliseconds { get; set; }
    public long UnixMilliseconds { get; set; }
    public string Severity { get; set; } = "Watch";
    public double DelayMs { get; set; }
    public double CpuPercent { get; set; }
    public double ManagedHeapMb { get; set; }
    public double PrivateMemoryMb { get; set; }
    public double EstimatedNativeMb { get; set; }
    public int LoadedEntities { get; set; }
    public int EntityDelta { get; set; }
    public int LoadedChunks { get; set; }
    public int ChunkDelta { get; set; }
    public int BlockLoggerQueue { get; set; }
    public long BlockLoggerWriterMs { get; set; }
    public long BlockLoggerDroppedDelta { get; set; }
    public int Gen0Delta { get; set; }
    public int Gen1Delta { get; set; }
    public int Gen2Delta { get; set; }
    public string Evidence { get; set; } = "";
}

public sealed class ServerDiagnosticSummary
{
    public string State { get; set; } = "Disabled";
    public int WindowMinutes { get; set; } = 5;
    public string Summary { get; set; } = "";
    public int StallCount { get; set; }
    public int SevereStallCount { get; set; }
    public double WorstDelayMs { get; set; }
    public double AverageCpuPercent { get; set; }
    public double MaxCpuPercent { get; set; }
    public int Gen2Collections { get; set; }
    public int MaxBlockLoggerQueue { get; set; }
    public long MaxBlockLoggerWriterMs { get; set; }
    public long BlockLoggerDroppedIncrease { get; set; }
    public int MaxLoadedEntities { get; set; }
    public int MaxLoadedChunks { get; set; }
}

public sealed class ServerSmartAnalysis
{
    public bool Available { get; set; }
    public string State { get; set; } = "Disabled";
    public int HealthScore { get; set; }
    public string PrimaryFinding { get; set; } = "";
    public string PrimaryConfidence { get; set; } = "Low";
    public string AnalysisConfidence { get; set; } = "Low";
    public int DataQualityScore { get; set; }
    public string RecoveryState { get; set; } = "Unknown";
    public ServerAdaptiveBaseline Baseline { get; set; } = new();
    public List<ServerSmartSignal> Signals { get; set; } = new();
    public List<ServerHealthTrend> Trends { get; set; } = new();
    public List<ServerLagIncidentGroup> IncidentGroups { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public double RecentDelayMedianMs { get; set; }
    public double RecentCpuMedianPercent { get; set; }
    public double RecentHeapMedianMb { get; set; }
    public double RecentEntityMedian { get; set; }
    public double RecentQueueMedian { get; set; }
    public double RecentWriterMedianMs { get; set; }
}

public sealed class ServerAdaptiveBaseline
{
    public int SampleCount { get; set; }
    public double DelayMedianMs { get; set; }
    public double DelayMadMs { get; set; }
    public double DelayAnomalyMs { get; set; }
    public double CpuMedianPercent { get; set; }
    public double CpuMadPercent { get; set; }
    public double CpuAnomalyPercent { get; set; }
    public double HeapMedianMb { get; set; }
    public double HeapMadMb { get; set; }
    public double EntityMedian { get; set; }
    public double EntityMad { get; set; }
    public double QueueMedian { get; set; }
    public double QueueMad { get; set; }
    public double QueueAnomaly { get; set; }
    public double WriterMedianMs { get; set; }
    public double WriterMadMs { get; set; }
    public double WriterAnomalyMs { get; set; }
}

public sealed class ServerSmartSignal
{
    public string Category { get; set; } = "";
    public string Severity { get; set; } = "Watch";
    public string Confidence { get; set; } = "Low";
    public int Score { get; set; }
    public string Title { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string NextAction { get; set; } = "";
}

public sealed class ServerHealthTrend
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Unit { get; set; } = "";
    public double SlopePerMinute { get; set; }
    public double NetChange { get; set; }
    public string Severity { get; set; } = "Info";
    public string Confidence { get; set; } = "Low";
    public string Interpretation { get; set; } = "";
}

public sealed class ServerLagIncidentGroup
{
    public long StartUnixMilliseconds { get; set; }
    public long EndUnixMilliseconds { get; set; }
    public double DurationSeconds { get; set; }
    public int SampleCount { get; set; }
    public string Severity { get; set; } = "Watch";
    public string PrimaryCategory { get; set; } = "";
    public string Confidence { get; set; } = "Low";
    public double WorstDelayMs { get; set; }
    public double MaxCpuPercent { get; set; }
    public int Gen2Collections { get; set; }
    public int MaxBlockLoggerQueue { get; set; }
    public long MaxBlockLoggerWriterMs { get; set; }
    public long BlockLoggerDropped { get; set; }
    public int MaxEntityDelta { get; set; }
    public int MaxChunkDelta { get; set; }
    public string Evidence { get; set; } = "";
    public string NextAction { get; set; } = "";
    public string LikelyModId { get; set; } = "";
    public string LikelyModName { get; set; } = "";
    public string ModConfidence { get; set; } = "Low";
    public string ModEvidence { get; set; } = "";
    public string ModAttributionSource { get; set; } = "";
    public long ModProfileCompletedUnixMilliseconds { get; set; }
}

public sealed class ServerProfilerHotspot
{
    public string Code { get; set; } = "";
    public double TotalMs { get; set; }
    public double MaxSingleMs { get; set; }
    public long Calls { get; set; }
    public double PercentOfProfileTickTime { get; set; }
    public string ModId { get; set; } = "";
    public string ModName { get; set; } = "";
    public string AttributionConfidence { get; set; } = "Low";
    public bool IsCore { get; set; }
}

public sealed class ServerProfilerModHotspot
{
    public string ModId { get; set; } = "";
    public string ModName { get; set; } = "";
    public double TotalMs { get; set; }
    public double MaxSingleMs { get; set; }
    public long Calls { get; set; }
    public int SlowSegments { get; set; }
    public double PercentOfProfileTickTime { get; set; }
    public string Confidence { get; set; } = "Low";
    public string TopMarker { get; set; } = "";
    public bool IsCore { get; set; }
}


public sealed class ServerModPerformanceDetails
{
    public string ModId { get; set; } = "";
    public bool ProfileAvailable { get; set; }
    public long ProfileCompletedUnixMilliseconds { get; set; }
    public int ProfileDurationSeconds { get; set; }
    public int ProfileTickCount { get; set; }
    public double ProfileAverageTickMs { get; set; }
    public double ProfileP95TickMs { get; set; }
    public double ProfileP99TickMs { get; set; }
    public double ProfileMaxTickMs { get; set; }
    public ServerProfilerModHotspot? Summary { get; set; }
    public List<ServerProfilerHotspot> Hotspots { get; set; } = new();
    public List<ServerProfilerPhase> Phases { get; set; } = new();
    public List<ServerProfilerOffThreadReport> OffThreadReports { get; set; } = new();
    public List<ServerProfileSlowTick> SlowTicks { get; set; } = new();
}

public sealed class ServerProfilerPhase
{
    public string Code { get; set; } = "";
    public double TotalMs { get; set; }
    public double AveragePerTickMs { get; set; }
    public double MaxSingleMs { get; set; }
    public long Calls { get; set; }
    public double PercentOfProfileTickTime { get; set; }
    public string ModId { get; set; } = "";
    public string AttributionConfidence { get; set; } = "Low";
}

public sealed class ServerProfileSlowCause
{
    public string Marker { get; set; } = "";
    public int Count { get; set; }
    public double WorstTickMs { get; set; }
    public string ModId { get; set; } = "";
    public string Confidence { get; set; } = "Low";
}

public sealed class ServerProfileSlowTick
{
    public long UnixMilliseconds { get; set; }
    public double TickMs { get; set; }
    public string TopCauseSummary { get; set; } = "";
}

public sealed class ServerProfilerOffThreadReport
{
    public double DurationMs { get; set; }
    public string Summary { get; set; } = "";
    public string ModId { get; set; } = "";
    public string Confidence { get; set; } = "Low";
}

public sealed class ServerProfileSummary
{
    public long CompletedUnixMilliseconds { get; set; }
    public int DurationSeconds { get; set; }
    public int TickCount { get; set; }
    public double AverageTickMs { get; set; }
    public double P95TickMs { get; set; }
    public double P99TickMs { get; set; }
    public double MaxTickMs { get; set; }
    public int AttributedSegments { get; set; }
    public int UnattributedSegments { get; set; }
    public double AttributedMs { get; set; }
    public double UnattributedMs { get; set; }
    public string CaptureKind { get; set; } = "Manual";
    public long TriggerIncidentUnixMilliseconds { get; set; }
}

public sealed class ServerTickContributors
{
    public bool Running { get; set; }
    public long CaptureUnixMilliseconds { get; set; }
    public int SampledTicks { get; set; }
    public int RemainingSeconds { get; set; }
    public List<ServerProfilerHotspot> Rows { get; set; } = new();
}
