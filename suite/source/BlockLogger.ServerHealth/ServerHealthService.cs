using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using BlockLogger.AnimalLimiter;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.ServerHealth;

public sealed class ServerHealthService : IDisposable
{
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

		public List<ModToken> Tokens { get; } = new List<ModToken>();
	}

	private sealed record ModToken(string Value, int Score, string Confidence);

	private readonly record struct ProfileLeafSample(string Path, double ElapsedMs, int Calls);

	private sealed record ModAttribution(string ModId, string ModName, string Confidence, bool IsAttributed, bool IsCore)
	{
		public static readonly ModAttribution Unattributed = new ModAttribution("", "", "Low", IsAttributed: false, IsCore: false);
	}

	private sealed record IncidentModAttribution(string ModId, string ModName, string Confidence, string Evidence, string Source, long ProfileCompletedUnixMilliseconds, bool IsAttributed)
	{
		public static readonly IncidentModAttribution None = new IncidentModAttribution("", "", "Low", "", "", 0L, IsAttributed: false);
	}

	private const int SampleIntervalMs = 1000;

	private const int MaxSamples = 1800;

	private const int MaxSlowSpikes = 50;

	private const int MaxDiagnosticIncidents = 100;

	private const double SlowSpikeThresholdMs = 150.0;

	private const int MaxProfileAccumulatorKeys = 4096;

	private const int MaxPhaseAccumulatorKeys = 1024;

	private const int ManualMaxProfileLeafSamplesPerTick = 4096;

	private const int AutomaticMaxProfileLeafSamplesPerTick = 512;

	private const string OverflowProfileKey = "<other profiler paths>";

	private const string OverflowPhaseKey = "<other tick phases>";

	private const int AutomaticIncidentProfileDurationSeconds = 3;

	private const long AutomaticIncidentProfileCooldownMilliseconds = 300000L;

	private const double AutomaticIncidentProfileTriggerDelayMs = 750.0;

	private const int AutomaticProfileSampleIntervalMs = 100;

	private const int ManualProfileSampleIntervalMs = 50;

	private readonly ICoreServerAPI api;

	private readonly Func<LogStorageService?> storageAccessor;

	private readonly Process process;

	private readonly object sync = new object();

	private readonly Queue<ServerHealthSample> samples = new Queue<ServerHealthSample>();

	private readonly Queue<ServerSlowSpike> slowSpikes = new Queue<ServerSlowSpike>();

	private readonly Queue<ServerDiagnosticIncident> diagnosticIncidents = new Queue<ServerDiagnosticIncident>();

	private ServerHealthSample? latestSample;

	private long sampleListenerId = -1L;

	private long lastSampleTimestamp;

	private TimeSpan lastCpuTime;

	private int cachedLoadedChunks;

	private int samplesSinceChunkRefresh = 30;

	private bool started;

	private bool enabled;

	private bool firstSamplingEnable = true;

	private long incidentSuppressionUntilUnixMilliseconds;

	private const int StartupWarmupMilliseconds = 45000;

	private const int ReenableWarmupMilliseconds = 5000;

	private const int MinimumSmartBaselineSamples = 60;

	private bool profileRunning;

	private bool profilerWasEnabled;

	private long profileListenerId = -1L;

	private long lastProfileRootStart = long.MinValue;

	private DateTime profileStartedUtc;

	private int profileDurationSeconds;

	private readonly Dictionary<string, ProfileAccumulator> profileAccumulators = new Dictionary<string, ProfileAccumulator>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, ProfileAccumulator> phaseAccumulators = new Dictionary<string, ProfileAccumulator>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, ModProfileAccumulator> modAccumulators = new Dictionary<string, ModProfileAccumulator>(StringComparer.OrdinalIgnoreCase);

	private readonly List<double> profileTickDurationsMs = new List<double>();

	private readonly List<ServerProfileSlowTick> capturedSlowProfileTicks = new List<ServerProfileSlowTick>();

	private readonly List<ProfileLeafSample> profileLeafBuffer = new List<ProfileLeafSample>(256);

	private List<ModIdentity> modIdentities = new List<ModIdentity>();

	private bool offThreadSettingsCaptured;

	private bool offThreadPrintWasEnabled;

	private int offThreadThresholdWas;

	private HashSet<string> offThreadBaseline = new HashSet<string>(StringComparer.Ordinal);

	private List<ServerProfilerHotspot> lastHotspots = new List<ServerProfilerHotspot>();

	private List<ServerProfilerModHotspot> lastModHotspots = new List<ServerProfilerModHotspot>();

	private List<ServerProfilerPhase> lastPhases = new List<ServerProfilerPhase>();

	private List<ServerProfileSlowTick> lastProfileSlowTicks = new List<ServerProfileSlowTick>();

	private List<ServerProfilerOffThreadReport> lastOffThreadReports = new List<ServerProfilerOffThreadReport>();

	private ServerProfileSummary lastProfile = new ServerProfileSummary();

	private bool automaticIncidentProfilingEnabled = true;

	private long lastAutomaticIncidentProfileUnixMilliseconds;

	private long lastAutomaticIncidentTriggerUnixMilliseconds;

	private double lastAutomaticIncidentTriggerDelayMs;

	private string profileCaptureKind = "Manual";

	private bool profileAutomatic;

	private int profileLeafLimit = 4096;

	private readonly Dictionary<string, ModAttribution> profileAttributionCache = new Dictionary<string, ModAttribution>(StringComparer.OrdinalIgnoreCase);

	private long profileTriggerIncidentUnixMilliseconds;

	private ServerSmartAnalysis cachedSmartAnalysis = new ServerSmartAnalysis();

	private long cachedSmartAnalysisUnixMilliseconds;

	public bool Enabled
	{
		get
		{
			lock (sync)
			{
				return enabled;
			}
		}
	}

	public bool AutomaticIncidentProfilingEnabled
	{
		get
		{
			lock (sync)
			{
				return automaticIncidentProfilingEnabled;
			}
		}
	}

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

	public ServerHealthService(ICoreServerAPI api, Func<LogStorageService?> storageAccessor)
	{
		this.api = api;
		this.storageAccessor = storageAccessor;
		process = Process.GetCurrentProcess();
	}

	public void Start(bool startEnabled)
	{
		lock (sync)
		{
			if (!started)
			{
				started = true;
				enabled = false;
				if (startEnabled)
				{
					EnableSamplingUnsafe();
				}
				else
				{
					api.Logger.Notification("[ServerHealth] profiler/diagnostics are disabled. Enable them from the Profiler GUI when needed.");
				}
			}
		}
	}

	public void SetAutomaticIncidentProfilingEnabled(bool value)
	{
		lock (sync)
		{
			automaticIncidentProfilingEnabled = value;
		}
	}

	public ServerHealthLiveState GetLiveState()
	{
		lock (sync)
		{
			return new ServerHealthLiveState
			{
				Enabled = enabled,
				Current = (latestSample ?? new ServerHealthSample()),
				ProfileRunning = profileRunning,
				ProfileDurationSeconds = profileDurationSeconds,
				ProfileRemainingSeconds = (profileRunning ? GetRemainingSecondsUnsafe() : 0)
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
			cachedSmartAnalysisUnixMilliseconds = 0L;
			return count;
		}
	}

	public int ClearProfileSlowTicks()
	{
		lock (sync)
		{
			int count = lastProfileSlowTicks.Count;
			lastProfileSlowTicks.Clear();
			cachedSmartAnalysis = new ServerSmartAnalysis();
			cachedSmartAnalysisUnixMilliseconds = 0L;
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
			cachedSmartAnalysisUnixMilliseconds = 0L;
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
			}
			else if (enabled != value)
			{
				if (value)
				{
					EnableSamplingUnsafe();
					return;
				}
				enabled = false;
				StopSamplingUnsafe();
				StopManualProfileUnsafe();
				samples.Clear();
				latestSample = null;
				slowSpikes.Clear();
				diagnosticIncidents.Clear();
				cachedSmartAnalysis = new ServerSmartAnalysis();
				cachedSmartAnalysisUnixMilliseconds = 0L;
				cachedLoadedChunks = 0;
				samplesSinceChunkRefresh = 30;
				api.Logger.Notification("[ServerHealth] profiler/diagnostics disabled.");
			}
		}
	}

	private void EnableSamplingUnsafe()
	{
		if (enabled && sampleListenerId >= 0)
		{
			return;
		}
		enabled = true;
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		incidentSuppressionUntilUnixMilliseconds = num + (firstSamplingEnable ? 45000 : 5000);
		firstSamplingEnable = false;
		lastSampleTimestamp = Stopwatch.GetTimestamp();
		process.Refresh();
		lastCpuTime = process.TotalProcessorTime;
		samplesSinceChunkRefresh = 30;
		if (sampleListenerId < 0)
		{
			sampleListenerId = api.Event.RegisterGameTickListener((float _) =>
			{
				Sample();
			}, (Exception ex) =>
			{
				api.Logger.Warning("[ServerHealth] sample failed: {0}: {1}", ex.GetType().Name, ex.Message);
			}, 1000);
		}
		api.Logger.Notification("[ServerHealth] profiler/diagnostics enabled. 1s samples, 30 minute in-memory history.");
	}

	private void StopSamplingUnsafe()
	{
		if (sampleListenerId >= 0)
		{
			api.Event.UnregisterGameTickListener(sampleListenerId);
			sampleListenerId = -1L;
		}
	}

	private void StopManualProfileUnsafe()
	{
		if (profileListenerId >= 0)
		{
			api.Event.UnregisterGameTickListener(profileListenerId);
			profileListenerId = -1L;
		}
		if (profileRunning)
		{
			api.World.FrameProfiler.Enabled = profilerWasEnabled;
		}
		RestoreOffThreadProfilerSettings();
		profileRunning = false;
		profileDurationSeconds = 0;
		lastProfileRootStart = long.MinValue;
		ReleaseManualProfileWorkingSetUnsafe();
	}

	public ServerHealthSnapshot GetSnapshot(int maxGraphPoints = 360, int windowSeconds = 1800)
	{
		lock (sync)
		{
			int num = Math.Clamp(windowSeconds, 60, 1800);
			long cutoff = DateTimeOffset.UtcNow.AddSeconds(-num).ToUnixTimeMilliseconds();
			List<ServerHealthSample> list = samples.ToList();
			List<ServerHealthSample> list2 = list.Where((ServerHealthSample x) => x.UnixMilliseconds >= cutoff).ToList();
			if (list2.Count == 0 && list.Count > 0)
			{
				list2.Add(list[list.Count - 1]);
			}
			List<ServerHealthSample> list3 = Downsample(list2, Math.Clamp(maxGraphPoints, 60, 1800));
			ServerHealthSample serverHealthSample;
			if (list.Count != 0)
			{
				serverHealthSample = list[list.Count - 1];
			}
			else
			{
				serverHealthSample = new ServerHealthSample();
			}
			ServerHealthSample current = serverHealthSample;
			return new ServerHealthSnapshot
			{
				Enabled = enabled,
				Current = current,
				Samples = list3,
				SlowSpikes = slowSpikes.Where((ServerSlowSpike x) => x.UnixMilliseconds >= cutoff).Reverse().Take(20)
					.ToList(),
				DiagnosticIncidents = diagnosticIncidents.Where((ServerDiagnosticIncident x) => x.UnixMilliseconds >= cutoff).Reverse().Take(60)
					.ToList(),
				DiagnosticSummary = BuildDiagnosticSummaryUnsafe(list),
				SmartAnalysis = GetSmartAnalysisUnsafe(list, diagnosticIncidents.ToList()),
				Hotspots = lastHotspots.Take(30).ToList(),
				ModHotspots = lastModHotspots.Take(30).ToList(),
				Phases = lastPhases.Take(30).ToList(),
				ProfileSlowTicks = lastProfileSlowTicks.Take(30).ToList(),
				OffThreadReports = lastOffThreadReports.Take(30).ToList(),
				LoadedModCount = ((modIdentities.Count > 0) ? modIdentities.Count : api.ModLoader.Mods.Count()),
				ProfileRunning = profileRunning,
				ProfileDurationSeconds = profileDurationSeconds,
				ProfileRemainingSeconds = (profileRunning ? Math.Max(0, (int)Math.Ceiling((double)profileDurationSeconds - (DateTime.UtcNow - profileStartedUtc).TotalSeconds)) : 0),
				AutomaticIncidentProfilingEnabled = automaticIncidentProfilingEnabled,
				AutomaticIncidentProfileDurationSeconds = 3,
				AutomaticIncidentProfileCooldownSeconds = 300,
				LastAutomaticIncidentProfileUnixMilliseconds = lastAutomaticIncidentProfileUnixMilliseconds,
				LastAutomaticIncidentTriggerUnixMilliseconds = lastAutomaticIncidentTriggerUnixMilliseconds,
				LastAutomaticIncidentTriggerDelayMs = lastAutomaticIncidentTriggerDelayMs,
				LastProfile = lastProfile
			};
		}
	}

	public ServerModPerformanceDetails GetModPerformanceDetails(string modId)
	{
		string requested = (modId ?? string.Empty).Trim();
		lock (sync)
		{
			ServerProfilerModHotspot summary = lastModHotspots.FirstOrDefault((ServerProfilerModHotspot x) => x.ModId.Equals(requested, StringComparison.OrdinalIgnoreCase));
			return new ServerModPerformanceDetails
			{
				ModId = requested,
				ProfileAvailable = (lastProfile.CompletedUnixMilliseconds > 0),
				ProfileCompletedUnixMilliseconds = lastProfile.CompletedUnixMilliseconds,
				ProfileDurationSeconds = lastProfile.DurationSeconds,
				ProfileTickCount = lastProfile.TickCount,
				ProfileAverageTickMs = lastProfile.AverageTickMs,
				ProfileP95TickMs = lastProfile.P95TickMs,
				ProfileP99TickMs = lastProfile.P99TickMs,
				ProfileMaxTickMs = lastProfile.MaxTickMs,
				Summary = summary,
				Hotspots = (from x in lastHotspots
					where x.ModId.Equals(requested, StringComparison.OrdinalIgnoreCase)
					orderby x.TotalMs descending
					select x).Take(30).ToList(),
				Phases = (from x in lastPhases
					where x.ModId.Equals(requested, StringComparison.OrdinalIgnoreCase)
					orderby x.TotalMs descending
					select x).Take(20).ToList(),
				OffThreadReports = (from x in lastOffThreadReports
					where x.ModId.Equals(requested, StringComparison.OrdinalIgnoreCase)
					orderby x.DurationMs descending
					select x).Take(20).ToList(),
				SlowTicks = (from x in lastProfileSlowTicks
					where !string.IsNullOrWhiteSpace(x.TopCauseSummary) && x.TopCauseSummary.Contains(requested, StringComparison.OrdinalIgnoreCase)
					orderby x.TickMs descending
					select x).Take(20).ToList()
			};
		}
	}

	public bool StartManualProfile(int requestedSeconds, out string message)
	{
		return StartProfile(requestedSeconds, automatic: false, out message);
	}

	private bool StartProfile(int requestedSeconds, bool automatic, out string message)
	{
		int num = (automatic ? Math.Clamp(requestedSeconds, 2, 5) : Math.Clamp(requestedSeconds, 5, 30));
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
			profileDurationSeconds = num;
			profileStartedUtc = DateTime.UtcNow;
			profileAutomatic = automatic;
			profileLeafLimit = (automatic ? 512 : 4096);
			profileCaptureKind = (automatic ? "Automatic lag incident" : "Manual");
			profileTriggerIncidentUnixMilliseconds = 0L;
			lastProfileRootStart = long.MinValue;
			profileAccumulators.Clear();
			phaseAccumulators.Clear();
			modAccumulators.Clear();
			profileTickDurationsMs.Clear();
			capturedSlowProfileTicks.Clear();
			profileAttributionCache.Clear();
			modIdentities = new List<ModIdentity>();
			offThreadSettingsCaptured = false;
			offThreadBaseline = new HashSet<string>(StringComparer.Ordinal);
			if (!automatic)
			{
				offThreadSettingsCaptured = true;
				offThreadPrintWasEnabled = FrameProfilerUtil.PrintSlowTicks_Offthreads;
				offThreadThresholdWas = FrameProfilerUtil.PrintSlowTicksThreshold_Offthreads;
				offThreadBaseline = new HashSet<string>(FrameProfilerUtil.offThreadProfiles.ToArray(), StringComparer.Ordinal);
				FrameProfilerUtil.PrintSlowTicks_Offthreads = true;
				FrameProfilerUtil.PrintSlowTicksThreshold_Offthreads = ((offThreadPrintWasEnabled && offThreadThresholdWas > 0) ? Math.Min(offThreadThresholdWas, 50) : 50);
			}
			FrameProfilerUtil frameProfiler = api.World.FrameProfiler;
			profilerWasEnabled = frameProfiler.Enabled;
			bool flag = frameProfiler.Enabled || frameProfiler.PrintSlowTicks;
			frameProfiler.Enabled = true;
			if (!flag)
			{
				frameProfiler.Begin("BlockLogger manual profiler transition");
			}
			profileListenerId = api.Event.RegisterGameTickListener((float _) =>
			{
				FrameProfilerUtil frameProfiler2 = api.World.FrameProfiler;
				frameProfiler2.Enter("blocklogger:profiler-capture");
				try
				{
					CaptureProfilerTick();
				}
				finally
				{
					frameProfiler2.Leave();
				}
			}, (Exception ex) =>
			{
				api.Logger.Warning("[ServerHealth] profiler capture tick failed: {0}: {1}", ex.GetType().Name, ex.Message);
			}, automatic ? 100 : 50);
			api.Event.RegisterCallback((float _) =>
			{
				FinishManualProfileSafe();
			}, num * 1000);
			message = (automatic ? $"Started {num}s lightweight automatic Vintage Story engine profile." : $"Started {num}s Vintage Story engine profile.");
			return true;
		}
	}

	private int GetRemainingSecondsUnsafe()
	{
		return Math.Max(0, (int)Math.Ceiling((double)profileDurationSeconds - (DateTime.UtcNow - profileStartedUtc).TotalSeconds));
	}

	private void Sample()
	{
		if (!enabled)
		{
			return;
		}
		long timestamp = Stopwatch.GetTimestamp();
		double num = Math.Max(0.001, (double)(timestamp - lastSampleTimestamp) / (double)Stopwatch.Frequency);
		lastSampleTimestamp = timestamp;
		double num2 = 1.0;
		double num3 = Math.Max(0.0, (num - num2) * 1000.0);
		process.Refresh();
		TimeSpan totalProcessorTime = process.TotalProcessorTime;
		double num4 = Math.Max(0.0, (totalProcessorTime - lastCpuTime).TotalSeconds);
		lastCpuTime = totalProcessorTime;
		double cpuPercent = Math.Clamp(num4 / num / (double)Math.Max(1, Environment.ProcessorCount) * 100.0, 0.0, 100.0);
		if (++samplesSinceChunkRefresh >= 30)
		{
			samplesSinceChunkRefresh = 0;
			try
			{
				cachedLoadedChunks = api.World.LoadedChunkIndices?.Length ?? cachedLoadedChunks;
			}
			catch
			{
			}
		}
		int loadedEntities = 0;
		try
		{
			loadedEntities = api.World.LoadedEntities.Count;
		}
		catch
		{
		}
		LogStorageService logStorageService = storageAccessor();
		AnimalLimiterSystem current = AnimalLimiterSystem.Current;
		double num5 = (double)GC.GetTotalMemory(forceFullCollection: false) / 1048576.0;
		double num6 = (double)process.PrivateMemorySize64 / 1048576.0;
		double num7 = 0.0;
		double gcFragmentedMb = 0.0;
		try
		{
			GCMemoryInfo gCMemoryInfo = GC.GetGCMemoryInfo();
			num7 = (double)gCMemoryInfo.TotalCommittedBytes / 1048576.0;
			gcFragmentedMb = (double)gCMemoryInfo.FragmentedBytes / 1048576.0;
		}
		catch
		{
		}
		double estimatedNativeMb = Math.Max(0.0, num6 - Math.Max(num5, num7));
		ServerHealthSample serverHealthSample = new ServerHealthSample
		{
			UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			MainThreadDelayMs = num3,
			CpuPercent = cpuPercent,
			WorkingSetMb = (double)process.WorkingSet64 / 1048576.0,
			PrivateMemoryMb = num6,
			ManagedHeapMb = num5,
			GcCommittedMb = num7,
			GcFragmentedMb = gcFragmentedMb,
			EstimatedNativeMb = estimatedNativeMb,
			Gen0Collections = GC.CollectionCount(0),
			Gen1Collections = GC.CollectionCount(1),
			Gen2Collections = GC.CollectionCount(2),
			Players = (api.World.AllOnlinePlayers?.Length ?? 0),
			LoadedEntities = loadedEntities,
			LoadedChunks = cachedLoadedChunks,
			BlockLoggerQueue = (int)((logStorageService != null) ? Math.Min(2147483647L, logStorageService.PendingEvents) : 0),
			BlockLoggerWriterMs = (logStorageService?.LastBatchElapsedMs ?? 0),
			BlockLoggerDropped = (logStorageService?.DroppedEvents ?? 0),
			AnimalTrackedEntities = (current?.HealthTrackedEntityCount ?? 0),
			AnimalTrackedRegions = (current?.HealthTrackedRegionCount ?? 0)
		};
		if (serverHealthSample.UnixMilliseconds < incidentSuppressionUntilUnixMilliseconds)
		{
			return;
		}
		bool flag = false;
		double triggerDelayMs = 0.0;
		lock (sync)
		{
			ServerHealthSample serverHealthSample2 = latestSample;
			int num8 = ((serverHealthSample2 != null) ? Math.Max(0, serverHealthSample.Gen0Collections - serverHealthSample2.Gen0Collections) : 0);
			int num9 = ((serverHealthSample2 != null) ? Math.Max(0, serverHealthSample.Gen1Collections - serverHealthSample2.Gen1Collections) : 0);
			int num10 = ((serverHealthSample2 != null) ? Math.Max(0, serverHealthSample.Gen2Collections - serverHealthSample2.Gen2Collections) : 0);
			int num11 = ((serverHealthSample2 != null) ? (serverHealthSample.LoadedEntities - serverHealthSample2.LoadedEntities) : 0);
			int num12 = ((serverHealthSample2 != null && serverHealthSample2.LoadedChunks > 0) ? (serverHealthSample.LoadedChunks - serverHealthSample2.LoadedChunks) : 0);
			int num13 = ((serverHealthSample2 != null) ? (serverHealthSample.Players - serverHealthSample2.Players) : 0);
			double num14 = ((serverHealthSample2 == null) ? 0.0 : (serverHealthSample.ManagedHeapMb - serverHealthSample2.ManagedHeapMb));
			double num15 = ((serverHealthSample2 == null) ? 0.0 : (serverHealthSample.PrivateMemoryMb - serverHealthSample2.PrivateMemoryMb));
			double num16 = ((serverHealthSample2 == null) ? 0.0 : (serverHealthSample.EstimatedNativeMb - serverHealthSample2.EstimatedNativeMb));
			long num17 = ((serverHealthSample2 == null) ? 0 : Math.Max(0L, serverHealthSample.BlockLoggerDropped - serverHealthSample2.BlockLoggerDropped));
			samples.Enqueue(serverHealthSample);
			latestSample = serverHealthSample;
			while (samples.Count > 1800)
			{
				samples.Dequeue();
			}
			if (num3 >= 150.0)
			{
				slowSpikes.Enqueue(new ServerSlowSpike
				{
					UnixMilliseconds = serverHealthSample.UnixMilliseconds,
					DelayMs = serverHealthSample.MainThreadDelayMs,
					CpuPercent = serverHealthSample.CpuPercent,
					WorkingSetMb = serverHealthSample.WorkingSetMb,
					Players = serverHealthSample.Players,
					LoadedEntities = serverHealthSample.LoadedEntities,
					LoadedChunks = serverHealthSample.LoadedChunks,
					BlockLoggerQueue = serverHealthSample.BlockLoggerQueue,
					BlockLoggerWriterMs = serverHealthSample.BlockLoggerWriterMs,
					Gen0Delta = num8,
					Gen1Delta = num9,
					Gen2Delta = num10
				});
				while (slowSpikes.Count > 50)
				{
					slowSpikes.Dequeue();
				}
			}
			List<string> list = new List<string>();
			int val = 0;
			if (serverHealthSample.MainThreadDelayMs >= 2000.0)
			{
				val = Math.Max(val, 3);
				list.Add($"severe scheduler stall {serverHealthSample.MainThreadDelayMs:0}ms");
			}
			else if (serverHealthSample.MainThreadDelayMs >= 500.0)
			{
				val = Math.Max(val, 2);
				list.Add($"major scheduler stall {serverHealthSample.MainThreadDelayMs:0}ms");
			}
			else if (serverHealthSample.MainThreadDelayMs >= 250.0)
			{
				val = Math.Max(val, 1);
				list.Add($"scheduler stall {serverHealthSample.MainThreadDelayMs:0}ms");
			}
			else if (serverHealthSample.MainThreadDelayMs >= 150.0)
			{
				list.Add($"brief scheduler delay {serverHealthSample.MainThreadDelayMs:0}ms");
			}
			if (serverHealthSample.CpuPercent >= 90.0)
			{
				val = Math.Max(val, (!(serverHealthSample.MainThreadDelayMs >= 50.0)) ? 1 : 2);
				list.Add($"process CPU {serverHealthSample.CpuPercent:0}%");
			}
			if (num10 > 0)
			{
				val = Math.Max(val, (!(serverHealthSample.MainThreadDelayMs >= 50.0)) ? 1 : 2);
				list.Add($"Gen2 GC +{num10}");
			}
			else if (num9 >= 2 || num8 >= 10)
			{
				val = Math.Max(val, 1);
				list.Add($"GC activity +{num8}/+{num9}/+{num10}");
			}
			if (serverHealthSample.BlockLoggerWriterMs >= 100)
			{
				val = Math.Max(val, (!(serverHealthSample.MainThreadDelayMs >= 50.0)) ? 1 : 2);
				list.Add($"BlockLogger writer {serverHealthSample.BlockLoggerWriterMs}ms");
			}
			if (serverHealthSample.BlockLoggerQueue >= 1000)
			{
				val = Math.Max(val, 1);
				list.Add($"BlockLogger queue {serverHealthSample.BlockLoggerQueue:N0}");
			}
			if (num17 > 0)
			{
				val = Math.Max(val, 3);
				list.Add($"BlockLogger dropped +{num17:N0}");
			}
			if (Math.Abs(num11) >= 500)
			{
				val = Math.Max(val, (!(serverHealthSample.MainThreadDelayMs >= 50.0)) ? 1 : 2);
				list.Add($"loaded entities changed {num11:+#;-#;0}");
			}
			else if (serverHealthSample.LoadedEntities >= 20000)
			{
				val = Math.Max(val, 1);
				list.Add($"high loaded entity count {serverHealthSample.LoadedEntities:N0}");
			}
			if (Math.Abs(num12) >= 300)
			{
				val = Math.Max(val, (!(serverHealthSample.MainThreadDelayMs >= 50.0)) ? 1 : 2);
				list.Add($"loaded chunks changed {num12:+#;-#;0} at 30s sample");
			}
			if (num14 >= 256.0)
			{
				val = Math.Max(val, (!(serverHealthSample.MainThreadDelayMs >= 50.0)) ? 1 : 2);
				list.Add($"managed heap grew {num14:0}MB");
			}
			if (num15 >= 256.0)
			{
				val = Math.Max(val, (!(serverHealthSample.MainThreadDelayMs >= 50.0)) ? 1 : 2);
				if (num14 < 64.0 && num16 >= 128.0)
				{
					list.Add($"private memory grew {num15:0}MB while managed heap stayed comparatively flat (native estimate +{num16:0}MB)");
				}
				else
				{
					list.Add($"private memory grew {num15:0}MB");
				}
			}
			if (num13 != 0 && Math.Abs(num13) >= 5)
			{
				list.Add($"players changed {num13:+#;-#;0}");
			}
			bool flag2 = serverHealthSample.CpuPercent >= 90.0 || num10 > 0 || serverHealthSample.BlockLoggerWriterMs >= 100 || serverHealthSample.BlockLoggerQueue >= 1000 || Math.Abs(num11) >= 500 || Math.Abs(num12) >= 300;
			if (serverHealthSample.MainThreadDelayMs >= 250.0 || num17 > 0 || ((serverHealthSample.MainThreadDelayMs >= 150.0) & flag2))
			{
				diagnosticIncidents.Enqueue(new ServerDiagnosticIncident
				{
					UnixMilliseconds = serverHealthSample.UnixMilliseconds,
					Severity = ((num17 > 0 || serverHealthSample.MainThreadDelayMs >= 2000.0) ? "Critical" : ((serverHealthSample.MainThreadDelayMs >= 500.0) ? "High" : ((serverHealthSample.MainThreadDelayMs >= 250.0) ? "Watch" : "Info"))),
					DelayMs = serverHealthSample.MainThreadDelayMs,
					CpuPercent = serverHealthSample.CpuPercent,
					ManagedHeapMb = serverHealthSample.ManagedHeapMb,
					PrivateMemoryMb = serverHealthSample.PrivateMemoryMb,
					EstimatedNativeMb = serverHealthSample.EstimatedNativeMb,
					LoadedEntities = serverHealthSample.LoadedEntities,
					EntityDelta = num11,
					LoadedChunks = serverHealthSample.LoadedChunks,
					ChunkDelta = num12,
					BlockLoggerQueue = serverHealthSample.BlockLoggerQueue,
					BlockLoggerWriterMs = serverHealthSample.BlockLoggerWriterMs,
					BlockLoggerDroppedDelta = num17,
					Gen0Delta = num8,
					Gen1Delta = num9,
					Gen2Delta = num10,
					Evidence = ((list.Count == 0) ? "Scheduler/health anomaly recorded." : string.Join(" | ", list.Take(6)))
				});
				while (diagnosticIncidents.Count > 100)
				{
					diagnosticIncidents.Dequeue();
				}
				bool flag3 = num17 > 0 || serverHealthSample.BlockLoggerWriterMs >= 100 || serverHealthSample.BlockLoggerQueue >= 1000 || num10 > 0 || Math.Abs(num11) >= 500 || Math.Abs(num12) >= 300;
				if (automaticIncidentProfilingEnabled && serverHealthSample.MainThreadDelayMs >= 750.0 && !flag3 && !profileRunning && serverHealthSample.UnixMilliseconds - lastAutomaticIncidentProfileUnixMilliseconds >= 300000)
				{
					lastAutomaticIncidentProfileUnixMilliseconds = serverHealthSample.UnixMilliseconds;
					lastAutomaticIncidentTriggerUnixMilliseconds = serverHealthSample.UnixMilliseconds;
					lastAutomaticIncidentTriggerDelayMs = serverHealthSample.MainThreadDelayMs;
					flag = true;
					triggerDelayMs = serverHealthSample.MainThreadDelayMs;
				}
			}
		}
		if (flag)
		{
			StartAutomaticIncidentProfile(triggerDelayMs);
		}
	}

	private void StartAutomaticIncidentProfile(double triggerDelayMs)
	{
		if (!StartProfile(3, automatic: true, out string message))
		{
			api.Logger.Warning("[ServerHealth] automatic incident profile could not start after {0:0}ms stall: {1}", triggerDelayMs, message);
			return;
		}
		lock (sync)
		{
			profileCaptureKind = "Automatic lag incident";
			profileTriggerIncidentUnixMilliseconds = lastAutomaticIncidentTriggerUnixMilliseconds;
		}
		api.Logger.Notification("[ServerHealth] automatic {0}s mod-attribution profile started after {1:0}ms main-thread stall; cooldown {2}s.", 3, triggerDelayMs, 300L);
	}

	private ServerSmartAnalysis GetSmartAnalysisUnsafe(List<ServerHealthSample> source, List<ServerDiagnosticIncident> incidents)
	{
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (cachedSmartAnalysisUnixMilliseconds > 0 && num - cachedSmartAnalysisUnixMilliseconds < 5000)
		{
			return cachedSmartAnalysis;
		}
		cachedSmartAnalysis = BuildSmartAnalysisUnsafe(source, incidents);
		cachedSmartAnalysisUnixMilliseconds = num;
		return cachedSmartAnalysis;
	}

	private ServerSmartAnalysis BuildSmartAnalysisUnsafe(List<ServerHealthSample> source, List<ServerDiagnosticIncident> incidents)
	{
		if (!enabled || source.Count == 0)
		{
			return new ServerSmartAnalysis
			{
				Available = false,
				State = (enabled ? "Collecting" : "Disabled"),
				HealthScore = (enabled ? 100 : 0),
				PrimaryFinding = (enabled ? "Waiting for enough samples to learn this server's baseline." : "Profiler diagnostics are disabled."),
				PrimaryConfidence = "Low",
				AnalysisConfidence = "Low",
				DataQualityScore = 0,
				RecoveryState = (enabled ? "Learning" : "Unavailable")
			};
		}
		if (source.Count < 60)
		{
			return new ServerSmartAnalysis
			{
				Available = true,
				State = "Learning",
				HealthScore = 100,
				PrimaryFinding = $"Collecting a stable gameplay baseline ({source.Count}/{60} samples).",
				PrimaryConfidence = "Low",
				AnalysisConfidence = "Low",
				DataQualityScore = Math.Clamp((int)Math.Round((double)source.Count * 60.0 / 60.0), 0, 60),
				RecoveryState = "Learning"
			};
		}
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		long recentCutoff = now - 300000;
		long trendCutoff = now - 600000;
		long baselineCutoff = now - 120000;
		List<ServerHealthSample> list = source.Where((ServerHealthSample x) => x.UnixMilliseconds >= recentCutoff).ToList();
		if (list.Count == 0)
		{
			list = source.TakeLast(Math.Min(300, source.Count)).ToList();
		}
		List<ServerHealthSample> list2 = source.Where((ServerHealthSample x) => x.UnixMilliseconds < baselineCutoff).TakeLast(900).ToList();
		if (list2.Count < 30)
		{
			list2 = source.Take(Math.Max(1, source.Count - Math.Min(15, source.Count))).ToList();
		}
		if (list2.Count < 10)
		{
			list2 = source.ToList();
		}
		ServerAdaptiveBaseline baseline = new ServerAdaptiveBaseline
		{
			SampleCount = list2.Count,
			DelayMedianMs = Median(list2.Select((ServerHealthSample x) => x.MainThreadDelayMs)),
			CpuMedianPercent = Median(list2.Select((ServerHealthSample x) => x.CpuPercent)),
			HeapMedianMb = Median(list2.Select((ServerHealthSample x) => x.ManagedHeapMb)),
			EntityMedian = Median(((IEnumerable<ServerHealthSample>)list2).Select((System.Func<ServerHealthSample, double>)((ServerHealthSample x) => x.LoadedEntities))),
			QueueMedian = Median(((IEnumerable<ServerHealthSample>)list2).Select((System.Func<ServerHealthSample, double>)((ServerHealthSample x) => x.BlockLoggerQueue))),
			WriterMedianMs = Median(((IEnumerable<ServerHealthSample>)list2).Select((System.Func<ServerHealthSample, double>)((ServerHealthSample x) => x.BlockLoggerWriterMs)))
		};
		baseline.DelayMadMs = MedianAbsoluteDeviation(list2.Select((ServerHealthSample x) => x.MainThreadDelayMs), baseline.DelayMedianMs);
		baseline.CpuMadPercent = MedianAbsoluteDeviation(list2.Select((ServerHealthSample x) => x.CpuPercent), baseline.CpuMedianPercent);
		baseline.HeapMadMb = MedianAbsoluteDeviation(list2.Select((ServerHealthSample x) => x.ManagedHeapMb), baseline.HeapMedianMb);
		baseline.EntityMad = MedianAbsoluteDeviation(((IEnumerable<ServerHealthSample>)list2).Select((System.Func<ServerHealthSample, double>)((ServerHealthSample x) => x.LoadedEntities)), baseline.EntityMedian);
		baseline.QueueMad = MedianAbsoluteDeviation(((IEnumerable<ServerHealthSample>)list2).Select((System.Func<ServerHealthSample, double>)((ServerHealthSample x) => x.BlockLoggerQueue)), baseline.QueueMedian);
		baseline.WriterMadMs = MedianAbsoluteDeviation(((IEnumerable<ServerHealthSample>)list2).Select((System.Func<ServerHealthSample, double>)((ServerHealthSample x) => x.BlockLoggerWriterMs)), baseline.WriterMedianMs);
		baseline.DelayAnomalyMs = Math.Max(50.0, baseline.DelayMedianMs + Math.Max(10.0, 6.0 * baseline.DelayMadMs));
		baseline.CpuAnomalyPercent = Math.Min(98.0, Math.Max(85.0, baseline.CpuMedianPercent + Math.Max(10.0, 5.0 * baseline.CpuMadPercent)));
		baseline.QueueAnomaly = Math.Max(500.0, baseline.QueueMedian + Math.Max(250.0, 6.0 * baseline.QueueMad));
		baseline.WriterAnomalyMs = Math.Max(75.0, baseline.WriterMedianMs + Math.Max(40.0, 6.0 * baseline.WriterMadMs));
		List<ServerSmartSignal> list3 = new List<ServerSmartSignal>();
		List<string> list4 = new List<string>();
		List<ServerDiagnosticIncident> source2 = (from x in incidents
			where x.UnixMilliseconds >= recentCutoff
			orderby x.UnixMilliseconds
			select x).ToList();
		List<ServerLagIncidentGroup> list5 = BuildIncidentGroupsUnsafe(source2);
		int num = list5.Count((ServerLagIncidentGroup x) => x.Severity == "Critical");
		int num2 = list5.Count((ServerLagIncidentGroup x) => x.Severity == "High");
		double recentDelayMedianMs = Median(list.Select((ServerHealthSample x) => x.MainThreadDelayMs));
		double recentCpuMedianPercent = Median(list.Select((ServerHealthSample x) => x.CpuPercent));
		double recentHeapMedianMb = Median(list.Select((ServerHealthSample x) => x.ManagedHeapMb));
		double num3 = Median(((IEnumerable<ServerHealthSample>)list).Select((System.Func<ServerHealthSample, double>)((ServerHealthSample x) => x.LoadedEntities)));
		double recentQueueMedian = Median(((IEnumerable<ServerHealthSample>)list).Select((System.Func<ServerHealthSample, double>)((ServerHealthSample x) => x.BlockLoggerQueue)));
		double recentWriterMedianMs = Median(((IEnumerable<ServerHealthSample>)list).Select((System.Func<ServerHealthSample, double>)((ServerHealthSample x) => x.BlockLoggerWriterMs)));
		int num4 = list.Count((ServerHealthSample x) => x.MainThreadDelayMs >= baseline.DelayAnomalyMs);
		int num5 = list.Count((ServerHealthSample x) => x.CpuPercent >= baseline.CpuAnomalyPercent);
		int num6 = list.Count((ServerHealthSample x) => (double)x.BlockLoggerQueue >= baseline.QueueAnomaly);
		int num7 = list.Count((ServerHealthSample x) => (double)x.BlockLoggerWriterMs >= baseline.WriterAnomalyMs);
		int num8;
		if (list.Count >= 2)
		{
			List<ServerHealthSample> list6 = list;
			num8 = Math.Max(0, list6[list6.Count - 1].Gen2Collections - list[0].Gen2Collections);
		}
		else
		{
			num8 = 0;
		}
		int num9 = num8;
		long num10;
		if (list.Count >= 2)
		{
			long val = 0L;
			List<ServerHealthSample> list7 = list;
			num10 = Math.Max(val, list7[list7.Count - 1].BlockLoggerDropped - list[0].BlockLoggerDropped);
		}
		else
		{
			num10 = 0L;
		}
		long num11 = num10;
		int num12 = source2.Count((ServerDiagnosticIncident x) => x.Gen2Delta > 0);
		int num13 = source2.Count((ServerDiagnosticIncident x) => x.BlockLoggerWriterMs >= 100 || x.BlockLoggerQueue >= 1000 || x.BlockLoggerDroppedDelta > 0);
		int num14 = source2.Count((ServerDiagnosticIncident x) => Math.Abs(x.EntityDelta) >= 500);
		int num15 = source2.Count((ServerDiagnosticIncident x) => Math.Abs(x.ChunkDelta) >= 300);
		int num16 = source2.Count((ServerDiagnosticIncident x) => x.CpuPercent >= 90.0);
		if (num4 >= 3 || num > 0)
		{
			list3.Add(new ServerSmartSignal
			{
				Category = "MainThread",
				Severity = ((num > 0) ? "Critical" : "High"),
				Confidence = "High",
				Score = ((num > 0) ? 95 : 78),
				Title = "Main-thread scheduling is repeatedly falling behind",
				Evidence = $"{num4} sample(s) exceeded the adaptive {baseline.DelayAnomalyMs:0}ms delay threshold; {list5.Count} grouped lag incident(s) were recorded.",
				NextAction = "Run a 10s or 30s engine profile during the slowdown. The lightweight delay sampler shows that work is late, but only the engine profile can attribute tick work to phases/mods."
			});
		}
		if (num16 >= 2 || num5 >= 15)
		{
			list3.Add(new ServerSmartSignal
			{
				Category = "CPU",
				Severity = ((num16 >= 4) ? "High" : "Watch"),
				Confidence = ((num16 >= 2) ? "High" : "Medium"),
				Score = Math.Min(90, 55 + num16 * 12),
				Title = "CPU saturation is correlated with lag",
				Evidence = $"{num16} lag-event sample(s) reached >=90% process CPU; {num5} recent sample(s) exceeded the adaptive CPU threshold of {baseline.CpuAnomalyPercent:0}%.",
				NextAction = "Profile while CPU is high. If one mod dominates high-confidence profiler time, investigate that mod; otherwise inspect entity/block-entity/world activity and off-thread reports."
			});
		}
		if (num12 >= 2)
		{
			list3.Add(new ServerSmartSignal
			{
				Category = "GC",
				Severity = ((num12 >= 4) ? "High" : "Watch"),
				Confidence = ((num12 > 0) ? "High" : "Medium"),
				Score = Math.Min(88, 52 + num12 * 15 + num9 * 4),
				Title = "Garbage collection is correlated with slow periods",
				Evidence = $"{num12} lag event(s) coincided with Gen2 GC; Gen2 collections increased by {num9} in the recent window.",
				NextAction = "Look for allocation-heavy mods or systems. Compare managed-heap growth before the event and use a manual profile to identify code active around the slow ticks."
			});
		}
		if (num13 >= 2 || num6 >= 10 || num7 >= 5 || num11 > 0)
		{
			list3.Add(new ServerSmartSignal
			{
				Category = "BlockLoggerIO",
				Severity = ((num11 > 0) ? "Critical" : ((num13 >= 4) ? "High" : "Watch")),
				Confidence = ((num13 >= 2 || num11 > 0) ? "High" : "Medium"),
				Score = ((num11 > 0) ? 98 : Math.Min(90, 52 + num13 * 12)),
				Title = ((num11 > 0) ? "BlockLogger backpressure has started dropping audit events" : "BlockLogger storage pressure is correlated with lag"),
				Evidence = $"{num13} lag event(s) showed queue/writer pressure; recent max queue={list.Max((ServerHealthSample x) => x.BlockLoggerQueue):N0}, max writer={list.Max((ServerHealthSample x) => x.BlockLoggerWriterMs):N0}ms, dropped +{num11:N0}.",
				NextAction = "Inspect disk/storage latency and BlockLogger queue pressure. If drops occur, reduce logging load or fix storage performance before treating another mod as the primary cause."
			});
		}
		if (num14 >= 2 || num3 > baseline.EntityMedian + Math.Max(1500.0, 6.0 * baseline.EntityMad))
		{
			list3.Add(new ServerSmartSignal
			{
				Category = "Entities",
				Severity = ((num14 >= 4) ? "High" : "Watch"),
				Confidence = ((num14 >= 2) ? "High" : "Medium"),
				Score = Math.Min(88, 50 + num14 * 14),
				Title = "Entity load changed sharply around lag",
				Evidence = $"{num14} lag event(s) had an entity-count jump >=500; baseline median={baseline.EntityMedian:0}, recent median={num3:0}.",
				NextAction = "Use the Animal Limiter population scan and inspect high-entity regions. If the manual profiler points at an entity-heavy mod, compare its entity population to the lag window."
			});
		}
		if (num15 >= 2)
		{
			list3.Add(new ServerSmartSignal
			{
				Category = "Chunks",
				Severity = ((num15 >= 4) ? "High" : "Watch"),
				Confidence = "Medium",
				Score = Math.Min(80, 48 + num15 * 12),
				Title = "Large loaded-chunk count changes are near lag events",
				Evidence = $"{num15} lag event(s) coincided with a >=300 loaded-chunk count change at the throttled 30-second chunk sample.",
				NextAction = "Check whether players were exploring/loading new terrain or whether a worldgen/chunk-loading mod is active. Confirm with manual profiler phases before assigning blame."
			});
		}
		List<ServerHealthSample> list8 = source.Where((ServerHealthSample x) => x.UnixMilliseconds >= trendCutoff).ToList();
		List<ServerHealthTrend> list9 = new List<ServerHealthTrend>();
		if (list8.Count >= 180)
		{
			AddTrendIfMeaningful(list9, "Main-thread delay", "ms/min", SlopePerMinute(list8, (ServerHealthSample x) => x.MainThreadDelayMs), list8[list8.Count - 1].MainThreadDelayMs - list8[0].MainThreadDelayMs, 5.0, 40.0, "MainThread", "A sustained rise in scheduler delay is more meaningful than one isolated spike.");
			AddTrendIfMeaningful(list9, "CPU", "%/min", SlopePerMinute(list8, (ServerHealthSample x) => x.CpuPercent), list8[list8.Count - 1].CpuPercent - list8[0].CpuPercent, 2.0, 15.0, "CPU", "A sustained process-CPU rise can show growing load even before hard saturation.");
			AddTrendIfMeaningful(list9, "Managed heap", "MB/min", SlopePerMinute(list8, (ServerHealthSample x) => x.ManagedHeapMb), list8[list8.Count - 1].ManagedHeapMb - list8[0].ManagedHeapMb, 10.0, 100.0, "Memory", "A sustained managed-heap rise can indicate allocation pressure or a growing retained object set.");
			AddTrendIfMeaningful(list9, "Private memory", "MB/min", SlopePerMinute(list8, (ServerHealthSample x) => x.PrivateMemoryMb), list8[list8.Count - 1].PrivateMemoryMb - list8[0].PrivateMemoryMb, 25.0, 250.0, "Memory", "Private-memory growth includes managed and native allocations; a rise without similar managed-heap growth points away from ordinary GC retention.");
			AddTrendIfMeaningful(list9, "Estimated native memory", "MB/min", SlopePerMinute(list8, (ServerHealthSample x) => x.EstimatedNativeMb), list8[list8.Count - 1].EstimatedNativeMb - list8[0].EstimatedNativeMb, 25.0, 250.0, "Memory", "A sustained native estimate rise can indicate SQLite/native buffers, engine chunk retention, image/compression buffers, or another unmanaged owner.");
			AddTrendIfMeaningful(list9, "Loaded entities", "entities/min", SlopePerMinute(list8, (ServerHealthSample x) => x.LoadedEntities), list8[list8.Count - 1].LoadedEntities - list8[0].LoadedEntities, 100.0, 500.0, "Entities", "A sustained entity-count rise can turn an otherwise normal entity system into a lag source.");
			AddTrendIfMeaningful(list9, "BlockLogger queue", "events/min", SlopePerMinute(list8, (ServerHealthSample x) => x.BlockLoggerQueue), list8[list8.Count - 1].BlockLoggerQueue - list8[0].BlockLoggerQueue, 50.0, 250.0, "BlockLoggerIO", "A queue that keeps growing means the writer is not keeping up with incoming audit work.");
		}
		foreach (ServerHealthTrend item in list9.Where((ServerHealthTrend x) => x.Severity != "Info"))
		{
			List<ServerSmartSignal> list10 = list3;
			ServerSmartSignal serverSmartSignal = new ServerSmartSignal();
			serverSmartSignal.Category = item.Category;
			serverSmartSignal.Severity = item.Severity;
			serverSmartSignal.Confidence = item.Confidence;
			serverSmartSignal.Score = ((item.Severity == "High") ? 78 : 58);
			serverSmartSignal.Title = item.Name + " is trending upward";
			serverSmartSignal.Evidence = $"{item.SlopePerMinute:+0.0;-0.0;0} {item.Unit}; net change {item.NetChange:+0.0;-0.0;0}. {item.Interpretation}";
			ServerSmartSignal serverSmartSignal2 = serverSmartSignal;
			string category = item.Category;
			if (1 == 0)
			{
			}
			string nextAction = category switch
			{
				"MainThread" => "Capture a manual engine profile while the delay trend is rising; trend evidence shows deterioration but not the responsible code path.", 
				"CPU" => "Compare the CPU rise against player/entity/chunk changes and run a manual profile if scheduler delay rises with it.", 
				"Memory" => "Watch whether the heap falls after Gen2 GC. If it repeatedly climbs to a higher floor, investigate allocation/retention-heavy mods.", 
				"Entities" => "Compare entity counts by type/region and inspect the Animal Limiter population report.", 
				"BlockLoggerIO" => "Inspect writer latency and storage speed; a continuously rising queue is a backlog, not a normal burst.", 
				_ => "Keep collecting data and compare against a manual profile during the next slowdown.", 
			};
			if (1 == 0)
			{
			}
			serverSmartSignal2.NextAction = nextAction;
			list10.Add(serverSmartSignal);
		}
		ServerProfilerModHotspot serverProfilerModHotspot = (from x in lastModHotspots
			where !x.IsCore && x.ModId != "newfiesserveressentialssuite" && x.ModId != "<unattributed>" && x.Confidence == "High"
			orderby x.TotalMs descending
			select x).FirstOrDefault();
		bool flag = lastProfile.CompletedUnixMilliseconds > 0 && now - lastProfile.CompletedUnixMilliseconds <= 1800000;
		if (flag && serverProfilerModHotspot != null && (serverProfilerModHotspot.PercentOfProfileTickTime >= 20.0 || serverProfilerModHotspot.MaxSingleMs >= 50.0))
		{
			list3.Add(new ServerSmartSignal
			{
				Category = "ProfiledMod",
				Severity = ((serverProfilerModHotspot.MaxSingleMs >= 100.0) ? "High" : "Watch"),
				Confidence = "High",
				Score = Math.Min(96, 65 + (int)Math.Min(25.0, serverProfilerModHotspot.PercentOfProfileTickTime / 2.0)),
				Title = "Profile hotspot: " + serverProfilerModHotspot.ModId,
				Evidence = $"{serverProfilerModHotspot.ModId} accounted for {serverProfilerModHotspot.PercentOfProfileTickTime:0.0}% of profiled tick time; max segment {serverProfilerModHotspot.MaxSingleMs:0.0}ms; {serverProfilerModHotspot.SlowSegments} segment(s) >=10ms.",
				NextAction = "Inspect " + serverProfilerModHotspot.ModId + "'s top profiler marker and reproduce the slowdown with that system active/inactive if possible. This is high-confidence profiler attribution, not proof of a bug."
			});
		}
		if (list5.Count >= 2 && list3.Count == 0)
		{
			list3.Add(new ServerSmartSignal
			{
				Category = "Unknown",
				Severity = "Watch",
				Confidence = "Low",
				Score = 45,
				Title = "Lag was recorded without a strong correlated subsystem signal",
				Evidence = $"{list5.Count} grouped lag incident(s) were recorded, but CPU/GC/storage/entity/chunk evidence did not dominate.",
				NextAction = "Run a manual 10s/30s engine profile during the slowdown and inspect Tick Phases, Hotspots, Mods, Slow Ticks, and Off-thread views."
			});
		}
		list3 = (from x in list3
			orderby x.Score descending, SeverityRank(x.Severity) descending
			select x).Take(12).ToList();
		foreach (ServerSmartSignal item2 in list3.Take(5))
		{
			if (!string.IsNullOrWhiteSpace(item2.NextAction) && !list4.Contains(item2.NextAction))
			{
				list4.Add(item2.NextAction);
			}
		}
		List<ServerHealthSample> list11 = source.Where((ServerHealthSample x) => x.UnixMilliseconds >= now - 60000).ToList();
		if (list11.Count == 0)
		{
			list11 = list.TakeLast(Math.Min(60, list.Count)).ToList();
		}
		double num17 = Median(list11.Select((ServerHealthSample x) => x.MainThreadDelayMs));
		double num18 = Median(list11.Select((ServerHealthSample x) => x.CpuPercent));
		bool flag2 = list5.Count > 0 && num17 < Math.Max(75.0, baseline.DelayAnomalyMs * 0.65) && num18 < Math.Min(85.0, baseline.CpuAnomalyPercent) && !source2.Any((ServerDiagnosticIncident x) => x.UnixMilliseconds >= now - 60000 && x.DelayMs >= 250.0);
		int num19 = Math.Clamp(35 + Math.Min(25, 10 + baseline.SampleCount / 12) + Math.Min(25, 10 + list.Count / 12) + (flag ? 15 : 0), 0, 100);
		string analysisConfidence;
		if (num19 >= 85)
		{
			analysisConfidence = "High";
		}
		else
		{
			analysisConfidence = ((num19 >= 65) ? "Medium" : "Low");
		}
		int num20 = 100;
		num20 -= Math.Min(35, num * 18);
		num20 -= Math.Min(24, num2 * 10);
		num20 -= Math.Min(20, num4 / 2);
		if (num16 > 0)
		{
			num20 -= Math.Min(15, num16 * 5);
		}
		if (num12 > 0)
		{
			num20 -= Math.Min(12, num12 * 4);
		}
		if (num13 > 0)
		{
			num20 -= Math.Min(18, num13 * 6);
		}
		if (num11 > 0)
		{
			num20 -= 20;
		}
		num20 -= Math.Min(15, list9.Count((ServerHealthTrend x) => x.Severity == "High") * 8);
		if (flag2)
		{
			num20 += 8;
		}
		num20 = Math.Clamp(num20, 0, 100);
		string state;
		if (num20 < 55)
		{
			state = "Critical";
		}
		else if (num20 < 75)
		{
			state = "Investigate";
		}
		else
		{
			state = ((num20 < 90) ? "Watch" : "Healthy");
		}
		ServerSmartSignal serverSmartSignal3 = list3.FirstOrDefault();
		return new ServerSmartAnalysis
		{
			Available = true,
			State = state,
			HealthScore = num20,
			PrimaryFinding = (serverSmartSignal3?.Title ?? "No strong lag pattern is currently standing out."),
			PrimaryConfidence = (serverSmartSignal3?.Confidence ?? "Low"),
			AnalysisConfidence = analysisConfidence,
			DataQualityScore = num19,
			RecoveryState = (flag2 ? "Recovered in the last minute" : ((list5.Count > 0) ? "Active/recent incidents" : "Stable")),
			Baseline = baseline,
			Signals = list3,
			Trends = (from x in list9
				orderby SeverityRank(x.Severity) descending, Math.Abs(x.SlopePerMinute) descending
				select x).Take(8).ToList(),
			IncidentGroups = list5.OrderByDescending((ServerLagIncidentGroup x) => x.EndUnixMilliseconds).Take(20).ToList(),
			Recommendations = list4.Take(6).ToList(),
			RecentDelayMedianMs = recentDelayMedianMs,
			RecentCpuMedianPercent = recentCpuMedianPercent,
			RecentHeapMedianMb = recentHeapMedianMb,
			RecentEntityMedian = num3,
			RecentQueueMedian = recentQueueMedian,
			RecentWriterMedianMs = recentWriterMedianMs
		};
	}

	private IncidentModAttribution ResolveIncidentModAttributionUnsafe(long incidentStart, long incidentEnd)
	{
		if (lastProfile.CompletedUnixMilliseconds <= 0 || lastModHotspots.Count == 0)
		{
			return IncidentModAttribution.None;
		}
		long num = lastProfile.CompletedUnixMilliseconds - (long)Math.Max(1, lastProfile.DurationSeconds) * 1000L;
		if (num > incidentEnd + 15000 || lastProfile.CompletedUnixMilliseconds < incidentStart - 5000)
		{
			return IncidentModAttribution.None;
		}
		if (string.Equals(lastProfile.CaptureKind, "Automatic lag incident", StringComparison.OrdinalIgnoreCase))
		{
			long triggerIncidentUnixMilliseconds = lastProfile.TriggerIncidentUnixMilliseconds;
			if (triggerIncidentUnixMilliseconds <= 0 || triggerIncidentUnixMilliseconds < incidentStart - 1000 || triggerIncidentUnixMilliseconds > incidentEnd + 1000)
			{
				return IncidentModAttribution.None;
			}
		}
		List<ServerProfilerModHotspot> list = (from x in lastModHotspots
			where !x.IsCore && !string.IsNullOrWhiteSpace(x.ModId) && x.ModId != "<unattributed>" && x.Confidence != "Low"
			orderby x.TotalMs descending
			select x).ToList();
		if (list.Count == 0)
		{
			return IncidentModAttribution.None;
		}
		ServerProfilerModHotspot serverProfilerModHotspot = list[0];
		ServerProfilerModHotspot serverProfilerModHotspot2 = ((list.Count > 1) ? list[1] : null);
		bool flag = serverProfilerModHotspot2 == null || serverProfilerModHotspot2.TotalMs <= 0.0 || serverProfilerModHotspot.TotalMs >= serverProfilerModHotspot2.TotalMs * 1.35;
		bool flag2 = ((serverProfilerModHotspot.Confidence == "High") & flag) && (serverProfilerModHotspot.PercentOfProfileTickTime >= 25.0 || serverProfilerModHotspot.MaxSingleMs >= 75.0 || serverProfilerModHotspot.SlowSegments >= 2);
		bool flag3 = serverProfilerModHotspot.Confidence != "Low" && (serverProfilerModHotspot.PercentOfProfileTickTime >= 10.0 || serverProfilerModHotspot.MaxSingleMs >= 30.0 || serverProfilerModHotspot.SlowSegments >= 1);
		if (!flag2 && !flag3)
		{
			return IncidentModAttribution.None;
		}
		string confidence = (flag2 ? "High" : "Medium");
		string evidence = $"{serverProfilerModHotspot.ModId} held {serverProfilerModHotspot.PercentOfProfileTickTime:0.0}% of profiled tick time; max segment {serverProfilerModHotspot.MaxSingleMs:0.0}ms; {serverProfilerModHotspot.SlowSegments} segment(s) >=10ms; top marker {ShortCode(serverProfilerModHotspot.TopMarker, 110)}.";
		return new IncidentModAttribution(serverProfilerModHotspot.ModId, string.IsNullOrWhiteSpace(serverProfilerModHotspot.ModName) ? serverProfilerModHotspot.ModId : serverProfilerModHotspot.ModName, confidence, evidence, lastProfile.CaptureKind, lastProfile.CompletedUnixMilliseconds, IsAttributed: true);
	}

	private List<ServerLagIncidentGroup> BuildIncidentGroupsUnsafe(List<ServerDiagnosticIncident> source)
	{
		List<ServerLagIncidentGroup> result = new List<ServerLagIncidentGroup>();
		if (source.Count == 0)
		{
			return result;
		}
		List<ServerDiagnosticIncident> list = source.OrderBy((ServerDiagnosticIncident x) => x.UnixMilliseconds).ToList();
		List<ServerDiagnosticIncident> current = new List<ServerDiagnosticIncident>();
		foreach (ServerDiagnosticIncident item6 in list)
		{
			if (current.Count > 0)
			{
				long unixMilliseconds = item6.UnixMilliseconds;
				List<ServerDiagnosticIncident> list2 = current;
				if (unixMilliseconds - list2[list2.Count - 1].UnixMilliseconds > 5000)
				{
					Flush();
				}
			}
			current.Add(item6);
		}
		Flush();
		return result;
		void Flush()
		{
			if (current.Count != 0)
			{
				long unixMilliseconds2 = current[0].UnixMilliseconds;
				List<ServerDiagnosticIncident> list3 = current;
				long unixMilliseconds3 = list3[list3.Count - 1].UnixMilliseconds;
				double num = current.Max((ServerDiagnosticIncident x) => x.DelayMs);
				double num2 = current.Max((ServerDiagnosticIncident x) => x.CpuPercent);
				int num3 = current.Max((ServerDiagnosticIncident x) => x.BlockLoggerQueue);
				long num4 = current.Max((ServerDiagnosticIncident x) => x.BlockLoggerWriterMs);
				long num5 = current.Sum((ServerDiagnosticIncident x) => x.BlockLoggerDroppedDelta);
				int num6 = current.Sum((ServerDiagnosticIncident x) => x.Gen2Delta);
				int num7 = (from x in current
					orderby Math.Abs(x.EntityDelta) descending
					select x.EntityDelta).FirstOrDefault();
				int num8 = (from x in current
					orderby Math.Abs(x.ChunkDelta) descending
					select x.ChunkDelta).FirstOrDefault();
				int item = ((num4 >= 100) ? 2 : 0) + ((num3 >= 1000) ? 2 : 0) + ((num5 > 0) ? 4 : 0);
				int item2 = ((num6 > 0) ? (3 + Math.Min(3, num6)) : 0);
				int item3 = ((num2 >= 95.0) ? 4 : ((num2 >= 90.0) ? 3 : 0));
				int item4 = ((Math.Abs(num7) >= 1000) ? 4 : ((Math.Abs(num7) >= 500) ? 3 : 0));
				int item5 = ((Math.Abs(num8) >= 600) ? 4 : ((Math.Abs(num8) >= 300) ? 3 : 0));
				List<(string, int)> list4 = new List<(string, int)>
				{
					("Storage / BlockLogger", item),
					("Garbage collection", item2),
					("CPU saturation", item3),
					("Entity load", item4),
					("Chunk/world activity", item5)
				}.OrderByDescending(((string Category, int Score) x) => x.Score).ToList();
				string text;
				string confidence;
				string nextAction;
				if (list4[0].Item2 <= 0)
				{
					text = "Main-thread / unknown";
					confidence = "Low";
					nextAction = "Run a manual engine profile during a repeat of this incident.";
				}
				else if (list4.Count > 1 && list4[1].Item2 > 0 && list4[0].Item2 - list4[1].Item2 <= 1)
				{
					text = "Mixed: " + list4[0].Item1 + " + " + list4[1].Item1;
					confidence = "Medium";
					nextAction = "Inspect both correlated signals, then use a manual profile to separate correlation from actual tick ownership.";
				}
				else
				{
					text = list4[0].Item1;
					confidence = ((list4[0].Item2 >= 5) ? "High" : "Medium");
					if (1 == 0)
					{
					}
					string text2 = text switch
					{
						"Storage / BlockLogger" => "Inspect disk/write latency and BlockLogger queue growth.", 
						"Garbage collection" => "Inspect managed-heap growth and allocation-heavy systems around the event.", 
						"CPU saturation" => "Run a manual engine profile while CPU is saturated.", 
						"Entity load" => "Inspect entity population/type/region and compare with the Animal Limiter report.", 
						"Chunk/world activity" => "Check exploration/worldgen/chunk loading and profile tick phases.", 
						_ => "Run a manual engine profile during a repeat of this incident.", 
					};
					if (1 == 0)
					{
					}
					nextAction = text2;
				}
				IncidentModAttribution incidentModAttribution = ResolveIncidentModAttributionUnsafe(unixMilliseconds2, unixMilliseconds3);
				if (incidentModAttribution.IsAttributed && incidentModAttribution.Confidence == "High" && (text == "Main-thread / unknown" || text == "CPU saturation"))
				{
					text = "Mod: " + incidentModAttribution.ModId;
					confidence = incidentModAttribution.Confidence;
					nextAction = "Inspect " + incidentModAttribution.ModId + " around the profiled hotspot. Attribution is profiler evidence, not automatic proof of a mod defect.";
				}
				string severity;
				if (num5 > 0 || num >= 2000.0)
				{
					severity = "Critical";
				}
				else if (num >= 500.0)
				{
					severity = "High";
				}
				else
				{
					severity = ((num >= 250.0 || current.Count >= 2) ? "Watch" : "Info");
				}
				result.Add(new ServerLagIncidentGroup
				{
					StartUnixMilliseconds = unixMilliseconds2,
					EndUnixMilliseconds = unixMilliseconds3,
					DurationSeconds = Math.Max(1.0, (double)(unixMilliseconds3 - unixMilliseconds2) / 1000.0 + 1.0),
					SampleCount = current.Count,
					Severity = severity,
					PrimaryCategory = text,
					Confidence = confidence,
					WorstDelayMs = num,
					MaxCpuPercent = num2,
					Gen2Collections = num6,
					MaxBlockLoggerQueue = num3,
					MaxBlockLoggerWriterMs = num4,
					BlockLoggerDropped = num5,
					MaxEntityDelta = num7,
					MaxChunkDelta = num8,
					Evidence = string.Join(" | ", (from x in current
						select x.Evidence into x
						where !string.IsNullOrWhiteSpace(x)
						select x).Distinct().Take(4)),
					NextAction = nextAction,
					LikelyModId = incidentModAttribution.ModId,
					LikelyModName = incidentModAttribution.ModName,
					ModConfidence = incidentModAttribution.Confidence,
					ModEvidence = incidentModAttribution.Evidence,
					ModAttributionSource = incidentModAttribution.Source,
					ModProfileCompletedUnixMilliseconds = incidentModAttribution.ProfileCompletedUnixMilliseconds
				});
				current.Clear();
			}
		}
	}

	private static void AddTrendIfMeaningful(List<ServerHealthTrend> output, string name, string unit, double slopePerMinute, double netChange, double watchSlope, double watchNetChange, string category, string interpretation)
	{
		bool flag = slopePerMinute >= watchSlope && netChange >= watchNetChange;
		bool flag2 = slopePerMinute >= watchSlope * 3.0 && netChange >= watchNetChange * 2.0;
		if (flag)
		{
			output.Add(new ServerHealthTrend
			{
				Name = name,
				Category = category,
				Unit = unit,
				SlopePerMinute = slopePerMinute,
				NetChange = netChange,
				Severity = (flag2 ? "High" : "Watch"),
				Confidence = (flag2 ? "High" : "Medium"),
				Interpretation = interpretation
			});
		}
	}

	private static double SlopePerMinute(List<ServerHealthSample> source, System.Func<ServerHealthSample, double> selector)
	{
		if (source.Count < 2)
		{
			return 0.0;
		}
		double firstMs = source[0].UnixMilliseconds;
		double num = source.Average((ServerHealthSample x) => ((double)x.UnixMilliseconds - firstMs) / 60000.0);
		double num2 = source.Average(selector);
		double num3 = 0.0;
		double num4 = 0.0;
		foreach (ServerHealthSample item in source)
		{
			double num5 = ((double)item.UnixMilliseconds - firstMs) / 60000.0;
			double num6 = selector(item);
			num3 += (num5 - num) * (num6 - num2);
			num4 += (num5 - num) * (num5 - num);
		}
		return (num4 <= 0.0) ? 0.0 : (num3 / num4);
	}

	private static double Median(IEnumerable<double> source)
	{
		List<double> list = (from x in source.Where(double.IsFinite)
			orderby x
			select x).ToList();
		if (list.Count == 0)
		{
			return 0.0;
		}
		int num = list.Count / 2;
		return ((list.Count & 1) == 1) ? list[num] : ((list[num - 1] + list[num]) / 2.0);
	}

	private static double MedianAbsoluteDeviation(IEnumerable<double> source, double median)
	{
		return Median(source.Select((double x) => Math.Abs(x - median)));
	}

	private static int SeverityRank(string severity)
	{
		if (1 == 0)
		{
		}
		int result = severity switch
		{
			"Critical" => 4, 
			"High" => 3, 
			"Watch" => 2, 
			"Info" => 1, 
			_ => 0, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private ServerDiagnosticSummary BuildDiagnosticSummaryUnsafe(List<ServerHealthSample> source)
	{
		if (!enabled || source.Count == 0)
		{
			return new ServerDiagnosticSummary
			{
				State = (enabled ? "Collecting" : "Disabled"),
				WindowMinutes = 5,
				Summary = (enabled ? "Waiting for enough health samples." : "Profiler diagnostics are disabled.")
			};
		}
		long cutoff = DateTimeOffset.UtcNow.AddMinutes(-5.0).ToUnixTimeMilliseconds();
		List<ServerHealthSample> list = source.Where((ServerHealthSample x) => x.UnixMilliseconds >= cutoff).ToList();
		if (list.Count == 0)
		{
			list = source.TakeLast(Math.Min(300, source.Count)).ToList();
		}
		int num = list.Count((ServerHealthSample x) => x.MainThreadDelayMs >= 150.0);
		int num2 = list.Count((ServerHealthSample x) => x.MainThreadDelayMs >= 500.0);
		int num3 = list.Count((ServerHealthSample x) => x.CpuPercent >= 90.0);
		int num4 = list.Count((ServerHealthSample x) => x.BlockLoggerWriterMs >= 100);
		int num5 = list.Count((ServerHealthSample x) => x.BlockLoggerQueue >= 1000);
		double worstDelayMs = list.Max((ServerHealthSample x) => x.MainThreadDelayMs);
		double averageCpuPercent = list.Average((ServerHealthSample x) => x.CpuPercent);
		double num6 = list.Max((ServerHealthSample x) => x.CpuPercent);
		int num7 = list.Max((ServerHealthSample x) => x.BlockLoggerQueue);
		long num8 = list.Max((ServerHealthSample x) => x.BlockLoggerWriterMs);
		int num9 = list.Max((ServerHealthSample x) => x.LoadedEntities);
		int maxLoadedChunks = list.Max((ServerHealthSample x) => x.LoadedChunks);
		long val = 0L;
		List<ServerHealthSample> list2 = list;
		long num10 = Math.Max(val, list2[list2.Count - 1].BlockLoggerDropped - list[0].BlockLoggerDropped);
		List<ServerHealthSample> list3 = list;
		int num11 = Math.Max(0, list3[list3.Count - 1].Gen2Collections - list[0].Gen2Collections);
		List<string> list4 = new List<string>();
		if (num2 > 0)
		{
			list4.Add($"{num2} scheduler stall(s) >=500ms");
		}
		else if (num >= 2)
		{
			list4.Add($"{num} scheduler delay sample(s) >=150ms");
		}
		if (num3 >= 5)
		{
			list4.Add($"CPU >=90% for {num3} sample(s), peak {num6:0}%");
		}
		if (num11 > 0 && num >= 2)
		{
			list4.Add($"Gen2 GC increased by {num11} during delayed samples");
		}
		if (num4 >= 3)
		{
			list4.Add($"BlockLogger writer >=100ms for {num4} sample(s), max {num8}ms");
		}
		if (num5 >= 5)
		{
			list4.Add($"BlockLogger queue >=1,000 for {num5} sample(s), max {num7:N0}");
		}
		if (num10 > 0)
		{
			list4.Add($"BlockLogger dropped {num10:N0} event(s)");
		}
		if (num9 >= 20000)
		{
			list4.Add($"loaded entities peaked at {num9:N0}");
		}
		string state;
		if (num10 > 0 || num2 > 0 || num >= 4 || num3 >= 15 || num4 >= 5 || num5 >= 10)
		{
			state = "Investigate";
		}
		else
		{
			state = ((num >= 2 || num3 >= 5 || num4 >= 3 || num5 >= 5) ? "Watch" : "Stable");
		}
		string summary = ((list4.Count == 0) ? "No strong lag-pressure signals were detected in the last five minutes." : string.Join(" | ", list4.Take(6)));
		return new ServerDiagnosticSummary
		{
			State = state,
			WindowMinutes = 5,
			Summary = summary,
			StallCount = num,
			SevereStallCount = num2,
			WorstDelayMs = worstDelayMs,
			AverageCpuPercent = averageCpuPercent,
			MaxCpuPercent = num6,
			Gen2Collections = num11,
			MaxBlockLoggerQueue = num7,
			MaxBlockLoggerWriterMs = num8,
			BlockLoggerDroppedIncrease = num10,
			MaxLoadedEntities = num9,
			MaxLoadedChunks = maxLoadedChunks
		};
	}

	private void CaptureProfilerTick()
	{
		lock (sync)
		{
			if (!profileRunning)
			{
				return;
			}
			ProfileEntryRange prevRootEntry = api.World.FrameProfiler.PrevRootEntry;
			if (prevRootEntry == null || prevRootEntry.Start == lastProfileRootStart)
			{
				return;
			}
			lastProfileRootStart = prevRootEntry.Start;
			double num = TicksToMs(prevRootEntry.ElapsedTicks);
			if (num < 0.0 || num >= 60000.0)
			{
				return;
			}
			profileTickDurationsMs.Add(num);
			CaptureRootPhases(prevRootEntry);
			profileLeafBuffer.Clear();
			CaptureLeafWork(prevRootEntry, "", 0, profileLeafBuffer);
			foreach (ProfileLeafSample item in profileLeafBuffer)
			{
				AddProfileValue(item.Path, item.ElapsedMs, item.Calls);
			}
			if (!(num >= 50.0))
			{
				return;
			}
			List<string> list = (from x in TopProfileLeaves(profileLeafBuffer, 4)
				select $"{ShortCode(x.Path, 72)} ({x.ElapsedMs:0.0}ms)").ToList();
			capturedSlowProfileTicks.Add(new ServerProfileSlowTick
			{
				UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				TickMs = num,
				TopCauseSummary = ((list.Count == 0) ? "No leaf profiler markers were exposed for this tick." : string.Join(" | ", list))
			});
			if (capturedSlowProfileTicks.Count <= 60)
			{
				return;
			}
			int index = 0;
			for (int num2 = 1; num2 < capturedSlowProfileTicks.Count; num2++)
			{
				if (capturedSlowProfileTicks[num2].TickMs < capturedSlowProfileTicks[index].TickMs)
				{
					index = num2;
				}
			}
			capturedSlowProfileTicks.RemoveAt(index);
		}
	}

	private static List<ProfileLeafSample> TopProfileLeaves(List<ProfileLeafSample> source, int count)
	{
		List<ProfileLeafSample> list = new List<ProfileLeafSample>(Math.Max(0, count));
		if (count <= 0)
		{
			return list;
		}
		foreach (ProfileLeafSample item in source)
		{
			int num = list.Count;
			for (int i = 0; i < list.Count; i++)
			{
				if (item.ElapsedMs > list[i].ElapsedMs)
				{
					num = i;
					break;
				}
			}
			if (num < count)
			{
				list.Insert(num, item);
				if (list.Count > count)
				{
					list.RemoveAt(list.Count - 1);
				}
			}
		}
		return list;
	}

	private void CaptureRootPhases(ProfileEntryRange root)
	{
		if (root.Marks != null)
		{
			foreach (KeyValuePair<string, ProfileEntry> mark in root.Marks)
			{
				AddPhaseValue(mark.Key, mark.Value.ElapsedTicks, mark.Value.CallCount);
			}
		}
		if (root.ChildRanges == null)
		{
			return;
		}
		foreach (ProfileEntryRange value in root.ChildRanges.Values)
		{
			if (value != null)
			{
				AddPhaseValue(value.Code ?? "unnamed", value.ElapsedTicks, value.CallCount);
			}
		}
	}

	private void CaptureLeafWork(ProfileEntryRange entry, string currentPath, int depth, List<ProfileLeafSample> output)
	{
		if (entry == null || depth > 6 || entry.ElapsedTicks <= 0 || output.Count >= profileLeafLimit)
		{
			return;
		}
		long num = 0L;
		string text = (string.IsNullOrWhiteSpace(currentPath) ? "" : (currentPath + " / "));
		if (entry.Marks != null)
		{
			foreach (KeyValuePair<string, ProfileEntry> mark in entry.Marks)
			{
				if (mark.Value != null && mark.Value.ElapsedTicks > 0)
				{
					if (output.Count >= profileLeafLimit)
					{
						break;
					}
					num += mark.Value.ElapsedTicks;
					output.Add(new ProfileLeafSample(text + mark.Key, TicksToMs(mark.Value.ElapsedTicks), Math.Max(1, mark.Value.CallCount)));
				}
			}
		}
		if (entry.ChildRanges != null)
		{
			foreach (ProfileEntryRange value in entry.ChildRanges.Values)
			{
				if (output.Count >= profileLeafLimit)
				{
					break;
				}
				if (value != null && value.ElapsedTicks > 0)
				{
					num += value.ElapsedTicks;
					string currentPath2 = (string.IsNullOrWhiteSpace(currentPath) ? (value.Code ?? "unnamed") : (currentPath + " / " + (value.Code ?? "unnamed")));
					CaptureLeafWork(value, currentPath2, depth + 1, output);
				}
			}
		}
		long num2 = Math.Max(0L, entry.ElapsedTicks - num);
		if (num2 > 0 && output.Count < profileLeafLimit)
		{
			string path = (string.IsNullOrWhiteSpace(currentPath) ? "<root / unscoped>" : (currentPath + " / <self / unscoped>"));
			output.Add(new ProfileLeafSample(path, TicksToMs(num2), 1));
		}
	}

	private void AddProfileValue(string code, double ms, int calls)
	{
		if (string.IsNullOrWhiteSpace(code) || ms <= 0.0)
		{
			return;
		}
		if (!profileAccumulators.TryGetValue(code, out ProfileAccumulator value))
		{
			if (profileAccumulators.Count >= 4096)
			{
				code = "<other profiler paths>";
			}
			if (!profileAccumulators.TryGetValue(code, out value))
			{
				value = new ProfileAccumulator();
				profileAccumulators[code] = value;
			}
		}
		value.TotalMs += ms;
		value.MaxSingleMs = Math.Max(value.MaxSingleMs, ms);
		value.Calls += Math.Max(1, calls);
	}

	private void AddPhaseValue(string code, long elapsedTicks, int calls)
	{
		if (string.IsNullOrWhiteSpace(code) || elapsedTicks <= 0)
		{
			return;
		}
		double num = TicksToMs(elapsedTicks);
		if (!phaseAccumulators.TryGetValue(code, out ProfileAccumulator value))
		{
			if (phaseAccumulators.Count >= 1024)
			{
				code = "<other tick phases>";
			}
			if (!phaseAccumulators.TryGetValue(code, out value))
			{
				value = new ProfileAccumulator();
				phaseAccumulators[code] = value;
			}
		}
		value.TotalMs += num;
		value.MaxSingleMs = Math.Max(value.MaxSingleMs, num);
		value.Calls += Math.Max(1, calls);
	}

	private void RebuildModAccumulatorsFromProfilePaths()
	{
		modAccumulators.Clear();
		foreach (KeyValuePair<string, ProfileAccumulator> profileAccumulator in profileAccumulators)
		{
			ModAttribution modAttribution = ResolveModAttribution(profileAccumulator.Key);
			ProfileAccumulator value = profileAccumulator.Value;
			string text = (modAttribution.IsAttributed ? modAttribution.ModId : "<unattributed>");
			if (!modAccumulators.TryGetValue(text, out ModProfileAccumulator value2))
			{
				value2 = new ModProfileAccumulator
				{
					ModId = text,
					ModName = (modAttribution.IsAttributed ? modAttribution.ModName : "Unattributed / engine-generic")
				};
				modAccumulators[text] = value2;
			}
			value2.TotalMs += value.TotalMs;
			value2.MaxSingleMs = Math.Max(value2.MaxSingleMs, value.MaxSingleMs);
			value2.Calls += Math.Max(1L, value.Calls);
			if (value.MaxSingleMs >= 10.0)
			{
				value2.SlowSegments++;
			}
			if (modAttribution.Confidence == "High")
			{
				value2.HighConfidenceMs += value.TotalMs;
			}
			else if (modAttribution.Confidence == "Medium")
			{
				value2.MediumConfidenceMs += value.TotalMs;
			}
			else
			{
				value2.LowConfidenceMs += value.TotalMs;
			}
			if (value.MaxSingleMs > value2.TopMarkerMs)
			{
				value2.TopMarkerMs = value.MaxSingleMs;
				value2.TopMarker = profileAccumulator.Key;
			}
		}
	}

	private List<ModIdentity> BuildModIdentityCatalog()
	{
		List<ModIdentity> list = new List<ModIdentity>();
		foreach (Mod mod in api.ModLoader.Mods)
		{
			if (mod?.Info == null)
			{
				continue;
			}
			string text = mod.Info.ModID ?? "";
			if (string.IsNullOrWhiteSpace(text))
			{
				continue;
			}
			ModIdentity modIdentity = new ModIdentity
			{
				ModId = text,
				Name = (string.IsNullOrWhiteSpace(mod.Info.Name) ? text : mod.Info.Name),
				IsCore = mod.Info.CoreMod
			};
			modIdentity.Tokens.Add(new ModToken(text + ":", 120, "High"));
			modIdentity.Tokens.Add(new ModToken(text, 85, (text.Length >= 6) ? "High" : "Medium"));
			string text2 = NormalizeToken(mod.Info.Name);
			if (text2.Length >= 5)
			{
				modIdentity.Tokens.Add(new ModToken(text2, 45, "Medium"));
			}
			if (!string.IsNullOrWhiteSpace(mod.SourcePath))
			{
				string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(mod.SourcePath);
				if (!string.IsNullOrWhiteSpace(fileNameWithoutExtension) && fileNameWithoutExtension.Length >= 5)
				{
					modIdentity.Tokens.Add(new ModToken(fileNameWithoutExtension, 55, "Medium"));
				}
			}
			foreach (ModSystem system in mod.Systems)
			{
				if (system != null)
				{
					Type type = system.GetType();
					string fullName = type.FullName;
					string text3 = type.Namespace;
					string name = type.Assembly.GetName().Name;
					if (!string.IsNullOrWhiteSpace(fullName))
					{
						modIdentity.Tokens.Add(new ModToken(fullName, 115, "High"));
					}
					if (!string.IsNullOrWhiteSpace(text3) && text3.Length >= 5)
					{
						modIdentity.Tokens.Add(new ModToken(text3, 105, "High"));
					}
					if (!string.IsNullOrWhiteSpace(type.Name) && type.Name.Length >= 5)
					{
						modIdentity.Tokens.Add(new ModToken(type.Name, 95, "High"));
					}
					if (!string.IsNullOrWhiteSpace(name) && name.Length >= 5)
					{
						modIdentity.Tokens.Add(new ModToken(name, 100, "High"));
					}
				}
			}
			list.Add(modIdentity);
		}
		return list;
	}

	private ModAttribution ResolveModAttribution(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return ModAttribution.Unattributed;
		}
		if (profileAttributionCache.TryGetValue(path, out ModAttribution value) && value != null)
		{
			return value;
		}
		ModAttribution modAttribution = ResolveModAttributionCore(path);
		if (profileAttributionCache.Count < 8192)
		{
			profileAttributionCache[path] = modAttribution;
		}
		return modAttribution;
	}

	private ModAttribution ResolveModAttributionCore(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return ModAttribution.Unattributed;
		}
		int num = int.MinValue;
		ModIdentity modIdentity = null;
		string confidence = "Low";
		bool flag = false;
		foreach (ModIdentity modIdentity2 in modIdentities)
		{
			foreach (ModToken token in modIdentity2.Tokens)
			{
				if (ContainsProfilerToken(path, token.Value))
				{
					int num2 = token.Score + Math.Min(30, token.Value.Length);
					if (num2 > num)
					{
						num = num2;
						modIdentity = modIdentity2;
						confidence = token.Confidence;
						flag = false;
					}
					else if (num2 == num && modIdentity != null && !string.Equals(modIdentity.ModId, modIdentity2.ModId, StringComparison.OrdinalIgnoreCase))
					{
						flag = true;
					}
				}
			}
		}
		if (modIdentity == null)
		{
			if (path.Contains("game:", StringComparison.OrdinalIgnoreCase) || path.Contains("Vintagestory.", StringComparison.OrdinalIgnoreCase))
			{
				return new ModAttribution("game", "Vintage Story / game", "High", IsAttributed: true, IsCore: true);
			}
			return ModAttribution.Unattributed;
		}
		if (flag)
		{
			return new ModAttribution("<ambiguous>", "Ambiguous mod attribution", "Low", IsAttributed: false, IsCore: false);
		}
		return new ModAttribution(modIdentity.ModId, modIdentity.Name, confidence, IsAttributed: true, modIdentity.IsCore);
	}

	private static bool ContainsProfilerToken(string haystack, string token)
	{
		if (string.IsNullOrWhiteSpace(token) || token.Length < 3)
		{
			return false;
		}
		if (token.Contains(':') || token.Contains('.') || token.Contains('/'))
		{
			return haystack.Contains(token, StringComparison.OrdinalIgnoreCase);
		}
		for (int num = haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase); num >= 0; num = haystack.IndexOf(token, num + 1, StringComparison.OrdinalIgnoreCase))
		{
			bool flag = num == 0 || !char.IsLetterOrDigit(haystack[num - 1]);
			int num2 = num + token.Length;
			bool flag2 = num2 >= haystack.Length || !char.IsLetterOrDigit(haystack[num2]);
			if (flag & flag2)
			{
				return true;
			}
		}
		return token.Length >= 8 && NormalizeToken(haystack).Contains(NormalizeToken(token), StringComparison.OrdinalIgnoreCase);
	}

	private static string NormalizeToken(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "";
		}
		return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
	}

	private static string ShortCode(string? value, int maxLength)
	{
		string text = value ?? "";
		if (text.Length <= maxLength)
		{
			return text;
		}
		if (maxLength <= 3)
		{
			return text.Substring(0, Math.Min(text.Length, maxLength));
		}
		return "..." + text.Substring(text.Length - (maxLength - 3));
	}

	private void CollectOffThreadReports()
	{
		List<ServerProfilerOffThreadReport> list = new List<ServerProfilerOffThreadReport>();
		string[] array = FrameProfilerUtil.offThreadProfiles.ToArray();
		foreach (string text in array)
		{
			if (!string.IsNullOrWhiteSpace(text) && !offThreadBaseline.Contains(text))
			{
				double durationMs = ExtractOffThreadDurationMs(text);
				ModAttribution modAttribution = ResolveModAttribution(text);
				string text2 = string.Join(" | ", (from x in text.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
					select x.Trim() into x
					where x.Length > 0
					select x).Take(5));
				if (text2.Length > 700)
				{
					text2 = text2.Substring(0, 697) + "...";
				}
				list.Add(new ServerProfilerOffThreadReport
				{
					DurationMs = durationMs,
					Summary = text2,
					ModId = (modAttribution.IsAttributed ? modAttribution.ModId : ""),
					Confidence = modAttribution.Confidence
				});
			}
		}
		lastOffThreadReports = list.OrderByDescending((ServerProfilerOffThreadReport x) => x.DurationMs).ThenBy((ServerProfilerOffThreadReport x) => x.ModId, StringComparer.OrdinalIgnoreCase).Take(30)
			.ToList();
	}

	private static double ExtractOffThreadDurationMs(string raw)
	{
		int num = raw.IndexOf("A tick took ", StringComparison.OrdinalIgnoreCase);
		if (num < 0)
		{
			return 0.0;
		}
		num += "A tick took ".Length;
		int num2 = raw.IndexOf(" ms", num, StringComparison.OrdinalIgnoreCase);
		if (num2 <= num)
		{
			return 0.0;
		}
		string s = raw.Substring(num, num2 - num).Trim();
		if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			return result;
		}
		double result2;
		return double.TryParse(s, out result2) ? result2 : 0.0;
	}

	private void RestoreOffThreadProfilerSettings()
	{
		if (offThreadSettingsCaptured)
		{
			FrameProfilerUtil.PrintSlowTicks_Offthreads = offThreadPrintWasEnabled;
			FrameProfilerUtil.PrintSlowTicksThreshold_Offthreads = offThreadThresholdWas;
			offThreadBaseline.Clear();
			offThreadSettingsCaptured = false;
		}
	}

	private void ReleaseManualProfileWorkingSetUnsafe()
	{
		profileAccumulators.Clear();
		phaseAccumulators.Clear();
		modAccumulators.Clear();
		profileTickDurationsMs.Clear();
		capturedSlowProfileTicks.Clear();
		profileLeafBuffer.Clear();
		if (profileLeafBuffer.Capacity > 4096)
		{
			profileLeafBuffer.Capacity = 4096;
		}
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
			api.Logger.Error("[ServerHealth] engine profile finalization failed: {0}: {1}", ex.GetType().Name, ex.Message);
			lock (sync)
			{
				try
				{
					StopManualProfileUnsafe();
				}
				catch (Exception ex2)
				{
					api.Logger.Warning("[ServerHealth] profiler cleanup after failure also failed: {0}: {1}", ex2.GetType().Name, ex2.Message);
				}
			}
		}
	}

	private void FinishManualProfile()
	{
		lock (sync)
		{
			if (!profileRunning)
			{
				return;
			}
			CaptureProfilerTick();
			if (profileListenerId >= 0)
			{
				api.Event.UnregisterGameTickListener(profileListenerId);
				profileListenerId = -1L;
			}
			api.World.FrameProfiler.Enabled = profilerWasEnabled;
			if (!profileAutomatic)
			{
				CollectOffThreadReports();
			}
			else
			{
				lastOffThreadReports = new List<ServerProfilerOffThreadReport>();
			}
			RestoreOffThreadProfilerSettings();
			profileRunning = false;
			List<double> orderedTicks = (from x in profileTickDurationsMs
				where x >= 0.0 && x < 60000.0
				orderby x
				select x).ToList();
			double totalTickMs = orderedTicks.Sum();
			modIdentities = BuildModIdentityCatalog();
			RebuildModAccumulatorsFromProfilePaths();
			int attributedSegments = modAccumulators.Where((KeyValuePair<string, ModProfileAccumulator> x) => x.Key != "<unattributed>").Sum((KeyValuePair<string, ModProfileAccumulator> x) => (int)((x.Value.Calls > int.MaxValue) ? int.MaxValue : x.Value.Calls));
			int unattributedSegments = (int)(modAccumulators.TryGetValue("<unattributed>", out ModProfileAccumulator value) ? ((value.Calls > int.MaxValue) ? int.MaxValue : value.Calls) : 0);
			double attributedMs = modAccumulators.Where((KeyValuePair<string, ModProfileAccumulator> x) => x.Key != "<unattributed>").Sum((KeyValuePair<string, ModProfileAccumulator> x) => x.Value.TotalMs);
			double unattributedMs = value?.TotalMs ?? 0.0;
			ServerProfileSummary serverProfileSummary = new ServerProfileSummary
			{
				CompletedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				DurationSeconds = profileDurationSeconds,
				TickCount = orderedTicks.Count,
				AverageTickMs = ((orderedTicks.Count == 0) ? 0.0 : orderedTicks.Average()),
				P95TickMs = Percentile(orderedTicks, 0.95),
				P99TickMs = Percentile(orderedTicks, 0.99)
			};
			double maxTickMs;
			if (orderedTicks.Count != 0)
			{
				List<double> list = orderedTicks;
				maxTickMs = list[list.Count - 1];
			}
			else
			{
				maxTickMs = 0.0;
			}
			serverProfileSummary.MaxTickMs = maxTickMs;
			serverProfileSummary.AttributedSegments = attributedSegments;
			serverProfileSummary.UnattributedSegments = unattributedSegments;
			serverProfileSummary.AttributedMs = attributedMs;
			serverProfileSummary.UnattributedMs = unattributedMs;
			serverProfileSummary.CaptureKind = profileCaptureKind;
			serverProfileSummary.TriggerIncidentUnixMilliseconds = profileTriggerIncidentUnixMilliseconds;
			lastProfile = serverProfileSummary;
			lastHotspots = (from x in profileAccumulators.Select((KeyValuePair<string, ProfileAccumulator> pair) =>
				{
					ModAttribution modAttribution = ResolveModAttribution(pair.Key);
					return new ServerProfilerHotspot
					{
						Code = pair.Key,
						TotalMs = pair.Value.TotalMs,
						MaxSingleMs = pair.Value.MaxSingleMs,
						Calls = pair.Value.Calls,
						PercentOfProfileTickTime = ((totalTickMs <= 0.0) ? 0.0 : Math.Clamp(pair.Value.TotalMs / totalTickMs * 100.0, 0.0, 999.0)),
						ModId = (modAttribution.IsAttributed ? modAttribution.ModId : ""),
						ModName = (modAttribution.IsAttributed ? modAttribution.ModName : ""),
						AttributionConfidence = modAttribution.Confidence,
						IsCore = modAttribution.IsCore
					};
				})
				orderby x.TotalMs descending
				select x).Take(200).ToList();
			lastModHotspots = (from x in modAccumulators.Select((KeyValuePair<string, ModProfileAccumulator> pair) =>
				{
					ModProfileAccumulator acc = pair.Value;
					string confidence = ((acc.TotalMs <= 0.0) ? "Low" : ((acc.HighConfidenceMs / acc.TotalMs >= 0.7) ? "High" : (((acc.HighConfidenceMs + acc.MediumConfidenceMs) / acc.TotalMs >= 0.7) ? "Medium" : "Low")));
					ModIdentity modIdentity = modIdentities.FirstOrDefault((ModIdentity x) => x.ModId.Equals(acc.ModId, StringComparison.OrdinalIgnoreCase));
					return new ServerProfilerModHotspot
					{
						ModId = acc.ModId,
						ModName = acc.ModName,
						TotalMs = acc.TotalMs,
						MaxSingleMs = acc.MaxSingleMs,
						Calls = acc.Calls,
						SlowSegments = acc.SlowSegments,
						PercentOfProfileTickTime = ((totalTickMs <= 0.0) ? 0.0 : Math.Clamp(acc.TotalMs / totalTickMs * 100.0, 0.0, 999.0)),
						Confidence = confidence,
						TopMarker = acc.TopMarker,
						IsCore = (modIdentity?.IsCore ?? (acc.ModId == "game"))
					};
				})
				orderby x.TotalMs descending
				select x).Take(100).ToList();
			lastPhases = (from x in phaseAccumulators.Select((KeyValuePair<string, ProfileAccumulator> pair) =>
				{
					ModAttribution modAttribution = ResolveModAttribution(pair.Key);
					return new ServerProfilerPhase
					{
						Code = pair.Key,
						TotalMs = pair.Value.TotalMs,
						AveragePerTickMs = ((orderedTicks.Count == 0) ? 0.0 : (pair.Value.TotalMs / (double)orderedTicks.Count)),
						MaxSingleMs = pair.Value.MaxSingleMs,
						Calls = pair.Value.Calls,
						PercentOfProfileTickTime = ((totalTickMs <= 0.0) ? 0.0 : Math.Clamp(pair.Value.TotalMs / totalTickMs * 100.0, 0.0, 999.0)),
						ModId = (modAttribution.IsAttributed ? modAttribution.ModId : ""),
						AttributionConfidence = modAttribution.Confidence
					};
				})
				orderby x.TotalMs descending
				select x).Take(120).ToList();
			lastProfileSlowTicks = capturedSlowProfileTicks.OrderByDescending((ServerProfileSlowTick x) => x.TickMs).Take(30).ToList();
			cachedSmartAnalysisUnixMilliseconds = 0L;
			api.Logger.Notification("[ServerHealth] {0}s engine profile complete. ticks={1}, avg={2:0.00}ms, p95={3:0.00}ms, p99={4:0.00}ms, max={5:0.00}ms, mods={6}, attributed={7}, unattributed={8}", profileDurationSeconds, lastProfile.TickCount, lastProfile.AverageTickMs, lastProfile.P95TickMs, lastProfile.P99TickMs, lastProfile.MaxTickMs, modIdentities.Count, lastProfile.AttributedSegments, lastProfile.UnattributedSegments);
			profileAutomatic = false;
			profileLeafLimit = 4096;
			ReleaseManualProfileWorkingSetUnsafe();
		}
	}

	private static double Percentile(List<double> sorted, double p)
	{
		if (sorted.Count == 0)
		{
			return 0.0;
		}
		int index = Math.Clamp((int)Math.Ceiling(p * (double)sorted.Count) - 1, 0, sorted.Count - 1);
		return sorted[index];
	}

	private static double TicksToMs(long ticks)
	{
		return (double)ticks / (double)Stopwatch.Frequency * 1000.0;
	}

	private static List<ServerHealthSample> Downsample(List<ServerHealthSample> source, int maxPoints)
	{
		if (source.Count <= maxPoints)
		{
			return source;
		}
		int num = Math.Max(1, (int)Math.Ceiling((double)source.Count / (double)maxPoints));
		List<ServerHealthSample> list = new List<ServerHealthSample>(maxPoints + 1);
		for (int i = 0; i < source.Count; i += num)
		{
			list.Add(source[i]);
		}
		if (list.Count != 0)
		{
			if (list[list.Count - 1].UnixMilliseconds == source[source.Count - 1].UnixMilliseconds)
			{
				goto IL_00b3;
			}
		}
		list.Add(source[source.Count - 1]);
		goto IL_00b3;
		IL_00b3:
		return list;
	}

	public void Dispose()
	{
		lock (sync)
		{
			if (started)
			{
				started = false;
				enabled = false;
				StopSamplingUnsafe();
				StopManualProfileUnsafe();
				process.Dispose();
			}
		}
	}
}
