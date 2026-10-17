using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BlockLogger.ServerHealth;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.WebDashboard;

public sealed class LogIntelligenceService : IDisposable
{
	private sealed record ModAttribution(string ModId, string ModName, string Confidence);

	private sealed class ModAlias
	{
		public string ModId { get; set; } = "";

		public string ModName { get; set; } = "";

		public string[] Aliases { get; set; } = Array.Empty<string>();
	}

	private sealed class ModPackageManifest
	{
		public string ModId { get; set; } = "";

		public string Version { get; set; } = "";

		public string SourcePath { get; set; } = "";

		public Dictionary<string, string> Dependencies { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}

	private const int MaxIssueSignatures = 750;

	private const int MaxSamplesPerIssue = 5;

	private const int MaxLiveBootstrapBytes = 4194304;

	private const int MaxArchivedBytesPerFile = 8388608;

	private const int MaxArchivedFiles = 24;

	private const int MaxArchivedFingerprints = 2000;

	private readonly IncrementalLogReader liveLogReader = new IncrementalLogReader();

	private long liveLogBacklogBytes;

	private long oversizedLogLines;

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};

	private static readonly Regex TimestampPrefix = new Regex("^\\s*(?:\\d{1,4}[-./]\\d{1,2}[-./]\\d{1,4}|\\d{1,2}:\\d{2}:\\d{2}|\\[\\d{1,2}:\\d{2}:\\d{2})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex GuidRegex = new Regex("\\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex HexRegex = new Regex("\\b0x[0-9a-fA-F]+\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex NumberRegex = new Regex("(?<![A-Za-z])[-+]?\\d+(?:\\.\\d+)?(?![A-Za-z])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex CoordinateRegex = new Regex("\\b-?\\d+\\s*[,/]\\s*-?\\d+\\s*[,/]\\s*-?\\d+\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex WindowsPathRegex = new Regex("\\b[A-Za-z]:\\\\[^\\r\\n]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex UnixPathRegex = new Regex("(?<!:)\\/(?:[^\\s:/]+\\/)+[^\\s:]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private readonly ICoreServerAPI api;

	private readonly Func<ServerHealthSnapshot?> healthProvider;

	private readonly BadBlockEntityTracker badBlockEntityTracker;

	private readonly object sync = new object();

	private readonly Dictionary<string, LogIssueState> issues = new Dictionary<string, LogIssueState>(StringComparer.Ordinal);

	private readonly Dictionary<string, long> fileOffsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	private readonly List<ModAlias> modAliases = new List<ModAlias>();

	private readonly HashSet<string> archivedScanFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private readonly string diagnosticsRoot;

	private readonly string statePath;

	private readonly string manifestPath;

	private readonly string preflightPath;

	private readonly string preflightHistoryRoot;

	private readonly string logsRoot;

	private readonly string modsRoot;

	private readonly string configRoot;

	private StartupPreflightSnapshot preflight = new StartupPreflightSnapshot();

	private long scanListenerId = -1L;

	private long lastScanUnixMilliseconds;

	private long lastArchiveScanUnixMilliseconds;

	private string currentLogPath = "";

	private int scanInProgress;

	private bool disposed;

	public LogIntelligenceService(ICoreServerAPI api, Func<ServerHealthSnapshot?> healthProvider)
	{
		this.api = api;
		this.healthProvider = healthProvider;
		badBlockEntityTracker = new BadBlockEntityTracker(api);
		diagnosticsRoot = Path.Combine(SuiteDataPaths.GetWorldRoot(api), "diagnostics");
		Directory.CreateDirectory(diagnosticsRoot);
		statePath = Path.Combine(diagnosticsRoot, "log-intelligence.json");
		manifestPath = Path.Combine(diagnosticsRoot, "loaded-mod-manifest.json");
		preflightPath = Path.Combine(diagnosticsRoot, "startup-preflight-latest.json");
		preflightHistoryRoot = Path.Combine(diagnosticsRoot, "preflight-history");
		Directory.CreateDirectory(preflightHistoryRoot);
		logsRoot = ResolveDataDirectory("Logs");
		modsRoot = ResolveDataDirectory("Mods");
		configRoot = SuiteConfigStorage.GetConfigRoot(api);
	}

	public void Start(string bindAddress, int port, bool webEnabled)
	{
		BuildModAliases();
		LoadState();
		currentLogPath = ResolveCurrentLogPath();
		preflight = RunPreflight(bindAddress, port, webEnabled, dashboardAlreadyRunning: false);
		SavePreflight(preflight, archiveHistory: true);
		api.Logger.Notification("[LogIntelligence] Startup preflight: {0}. Critical={1}, High={2}, Watch={3}, Mods={4}, Game={5}.", preflight.Status, preflight.Critical, preflight.High, preflight.Watch, preflight.LoadedModCount, preflight.LoadedGameVersion);
		foreach (PreflightCheck item in preflight.Checks.Where((PreflightCheck x) =>
		{
			switch (x.Severity)
			{
			case "Critical":
			case "High":
			case "Watch":
				return true;
			default:
				return false;
			}
		}).Take(20))
		{
			api.Logger.Warning("[LogIntelligence/Preflight] {0} {1}: {2} - {3}", item.Severity, item.Category, item.Title, item.Detail);
		}
		SaveState();
		TriggerIncrementalScan(bootstrap: true);
		if (scanListenerId < 0)
		{
			scanListenerId = api.Event.RegisterGameTickListener((float _) =>
			{
				TriggerIncrementalScan(bootstrap: false);
			}, 30000);
		}
	}

	public StartupPreflightSnapshot RefreshPreflight(string bindAddress, int port, bool webEnabled, bool dashboardAlreadyRunning)
	{
		BuildModAliases();
		StartupPreflightSnapshot startupPreflightSnapshot = RunPreflight(bindAddress, port, webEnabled, dashboardAlreadyRunning);
		lock (sync)
		{
			preflight = startupPreflightSnapshot;
		}
		SavePreflight(startupPreflightSnapshot, archiveHistory: false);
		SaveState();
		return startupPreflightSnapshot;
	}

	public async Task RunManualScanAsync(bool includeArchived)
	{
		if (Interlocked.CompareExchange(ref scanInProgress, 1, 0) != 0)
		{
			return;
		}
		try
		{
			await Task.Run(() =>
			{
				ScanCurrentLog(forceRecentWindow: false);
				if (includeArchived)
				{
					ScanArchivedLogs();
				}
				SaveState();
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			Volatile.Write(ref scanInProgress, 0);
		}
	}

	public LogIntelligenceSnapshot GetSnapshot()
	{
		ServerHealthSnapshot health = null;
		try
		{
			health = healthProvider();
		}
		catch
		{
		}
		List<LogIssueView> list;
		StartupPreflightSnapshot startupPreflightSnapshot;
		long num;
		long num2;
		string text;
		lock (sync)
		{
			list = (from x in issues.Values
				select ToView(x, health) into x
				orderby SeverityRank(x.Impact), x.LastSeenUnixMilliseconds descending, x.Occurrences descending
				select x).Take(750).ToList();
			startupPreflightSnapshot = preflight;
			num = lastScanUnixMilliseconds;
			num2 = lastArchiveScanUnixMilliseconds;
			text = currentLogPath;
		}
		int num3 = list.Count((LogIssueView x) => x.Impact == "Critical");
		int num4 = list.Count((LogIssueView x) => x.Impact == "High");
		int num5 = list.Count((LogIssueView x) => x.Impact == "Moderate");
		int num6 = list.Count((LogIssueView x) => x.Impact == "Low");
		int num7 = Math.Clamp(100 - num3 * 20 - num4 * 8 - num5 * 3 - num6, 0, 100);
		string state;
		if (num7 >= 90)
		{
			state = "Healthy";
		}
		else if (num7 >= 75)
		{
			state = "Watch";
		}
		else
		{
			state = ((num7 >= 50) ? "Investigate" : "Critical");
		}
		List<LogCategorySummary> categories = (from g in list.GroupBy((LogIssueView x) => x.Category, StringComparer.OrdinalIgnoreCase)
			select new LogCategorySummary
			{
				Category = g.Key,
				Occurrences = g.Sum((LogIssueView x) => x.Occurrences),
				UniqueIssues = g.Count(),
				Impact = g.OrderBy((LogIssueView x) => SeverityRank(x.Impact)).First().Impact
			} into x
			orderby SeverityRank(x.Impact), x.Occurrences descending
			select x).ToList();
		return new LogIntelligenceSnapshot
		{
			GeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			LastScanUnixMilliseconds = num,
			LastArchiveScanUnixMilliseconds = num2,
			CurrentLogPath = text,
			LiveLogBacklogBytes = Volatile.Read(in liveLogBacklogBytes),
			OversizedLogLines = Volatile.Read(in oversizedLogLines),
			Score = num7,
			State = state,
			Critical = num3,
			High = num4,
			Moderate = num5,
			Low = num6,
			TotalOccurrences = list.Sum((LogIssueView x) => x.Occurrences),
			UniqueIssues = list.Count,
			Categories = categories,
			Issues = list,
			Preflight = startupPreflightSnapshot,
			BadBlockEntities = badBlockEntityTracker.GetSnapshot(),
			ScanInProgress = (Volatile.Read(in scanInProgress) != 0)
		};
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			if (scanListenerId >= 0)
			{
				api.Event.UnregisterGameTickListener(scanListenerId);
				scanListenerId = -1L;
			}
			SaveState();
			badBlockEntityTracker.SaveState();
		}
	}

	public BadBlockEntitySnapshot GetBadBlockEntitySnapshot()
	{
		return badBlockEntityTracker.GetSnapshot();
	}

	public BadBlockEntitySnapshot GetBadBlockEntityConsoleAlerts()
	{
		return badBlockEntityTracker.GetConsoleAlertSnapshot(3, 300000L);
	}

	public bool TryGetBadBlockEntity(string key, out BadBlockEntityRecord record)
	{
		return badBlockEntityTracker.TryGet(key, out record);
	}

	public bool SetBadBlockEntityTracking(string key, bool tracking, string actor, out string message)
	{
		return badBlockEntityTracker.SetTracking(key, tracking, actor, out message);
	}

	public void RecordBadBlockEntityAction(string key, string actor, string action, string detail)
	{
		badBlockEntityTracker.RecordAction(key, actor, action, detail);
	}

	public int ClearAllBadBlockEntities(string actor, out string message)
	{
		return badBlockEntityTracker.ClearAll(actor, out message);
	}

	public int ClearIssueSignatures()
	{
		int count;
		lock (sync)
		{
			count = issues.Count;
			issues.Clear();
		}
		SaveState();
		return count;
	}

	private void TriggerIncrementalScan(bool bootstrap)
	{
		if (disposed || (!bootstrap && !CurrentLogNeedsScan()) || Interlocked.CompareExchange(ref scanInProgress, 1, 0) != 0)
		{
			return;
		}
		Task.Run(() =>
		{
			try
			{
				ScanCurrentLog(bootstrap);
				SaveState();
			}
			catch (Exception)
			{
			}
			finally
			{
				Volatile.Write(ref scanInProgress, 0);
			}
		});
	}

	private bool CurrentLogNeedsScan()
	{
		try
		{
			string text = ResolveCurrentLogPath();
			if (string.IsNullOrWhiteSpace(text) || !File.Exists(text))
			{
				return false;
			}
			long length = new FileInfo(text).Length;
			lock (sync)
			{
				if (!fileOffsets.TryGetValue(text, out var value))
				{
					return length > 0;
				}
				return length != value;
			}
		}
		catch
		{
			return true;
		}
	}

	private void ScanCurrentLog(bool forceRecentWindow)
	{
		string text = ResolveCurrentLogPath();
		if (string.IsNullOrWhiteSpace(text) || !File.Exists(text))
		{
			lock (sync)
			{
				currentLogPath = text;
				lastScanUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				return;
			}
		}
		FileInfo fileInfo = new FileInfo(text);
		long value;
		lock (sync)
		{
			currentLogPath = text;
			if (!fileOffsets.TryGetValue(text, out value))
			{
				value = 0L;
			}
		}
		if (forceRecentWindow && value == 0L && fileInfo.Length > 4194304)
		{
			value = Math.Max(0L, fileInfo.Length - 4194304);
		}
		if (fileInfo.Length < value)
		{
			value = Math.Max(0L, fileInfo.Length - Math.Min(fileInfo.Length, 4194304L));
		}
		string text2 = liveLogReader.Read(text, value, LooksActionable);
		long offset = liveLogReader.Offset;
		Volatile.Write(ref liveLogBacklogBytes, liveLogReader.BacklogBytes);
		Volatile.Write(ref oversizedLogLines, liveLogReader.OversizedLines);
		if (!string.IsNullOrWhiteSpace(text2))
		{
			ParseText(text2, Path.GetFileName(text), archived: false);
		}
		lock (sync)
		{
			fileOffsets[text] = offset;
			lastScanUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}
	}

	private void ScanArchivedLogs()
	{
		if (!Directory.Exists(logsRoot))
		{
			return;
		}
		string current = ResolveCurrentLogPath();
		List<FileInfo> list = (from x in Directory.EnumerateFiles(logsRoot, "server-main*.log", SearchOption.TopDirectoryOnly)
			where !string.Equals(Path.GetFullPath(x), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)
			select new FileInfo(x) into x
			orderby x.LastWriteTimeUtc descending
			select x).Take(24).ToList();
		foreach (FileInfo item2 in list)
		{
			string item = $"{item2.FullName}|{item2.Length}|{item2.LastWriteTimeUtc.Ticks}";
			lock (sync)
			{
				if (archivedScanFingerprints.Contains(item))
				{
					continue;
				}
			}
			long offset = Math.Max(0L, item2.Length - 8388608);
			string text = ReadFileSlice(item2.FullName, offset, 8388608, out var _);
			if (!string.IsNullOrWhiteSpace(text))
			{
				ParseText(text, item2.Name, archived: true);
			}
			lock (sync)
			{
				archivedScanFingerprints.Add(item);
				while (archivedScanFingerprints.Count > 2000)
				{
					string text2 = archivedScanFingerprints.FirstOrDefault();
					if (text2 == null)
					{
						break;
					}
					archivedScanFingerprints.Remove(text2);
				}
			}
		}
		lock (sync)
		{
			lastArchiveScanUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}
	}

	private static string ReadFileSlice(string path, long offset, int maxBytes, out long finalOffset)
	{
		finalOffset = offset;
		try
		{
			using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (offset > fileStream.Length)
			{
				offset = 0L;
			}
			fileStream.Seek(offset, SeekOrigin.Begin);
			long val = fileStream.Length - offset;
			int num = (int)Math.Min(val, Math.Min(8388608, (maxBytes <= 0) ? 8388608 : maxBytes));
			if (num <= 0)
			{
				finalOffset = fileStream.Length;
				return "";
			}
			byte[] array = new byte[num];
			int i;
			int num2;
			for (i = 0; i < num; i += num2)
			{
				num2 = fileStream.Read(array, i, num - i);
				if (num2 <= 0)
				{
					break;
				}
			}
			finalOffset = offset + i;
			return Encoding.UTF8.GetString(array, 0, i);
		}
		catch
		{
			return "";
		}
	}

	private void ParseText(string text, string sourceFile, bool archived)
	{
		string text2 = text.Replace("\r\n", "\n").Replace('\r', '\n');
		string[] array = text2.Split('\n');
		for (int i = 0; i < array.Length; i++)
		{
			string text3 = array[i].TrimEnd();
			if (!LooksActionable(text3))
			{
				continue;
			}
			StringBuilder stringBuilder = new StringBuilder(text3.Trim());
			int num = 0;
			bool flag = text3.IndexOf("threw an error when ticked", StringComparison.OrdinalIgnoreCase) >= 0 && text3.IndexOf("At position", StringComparison.OrdinalIgnoreCase) >= 0;
			for (int j = i + 1; j < array.Length; j++)
			{
				if (num >= 8)
				{
					break;
				}
				string text4 = array[j];
				if (string.IsNullOrWhiteSpace(text4))
				{
					break;
				}
				string text5 = text4.Trim();
				if (flag && num == 0 && text5.IndexOf("[Error] Exception:", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					int num2 = text5.IndexOf("Exception:", StringComparison.OrdinalIgnoreCase);
					stringBuilder.Append('\n').Append((num2 >= 0) ? text5.Substring(num2) : text5);
					num++;
					continue;
				}
				if (text5.StartsWith("at ", StringComparison.OrdinalIgnoreCase) || text5.StartsWith("--->", StringComparison.Ordinal) || text5.StartsWith("Caused by", StringComparison.OrdinalIgnoreCase) || text4.StartsWith(" ") || text4.StartsWith("\t"))
				{
					stringBuilder.Append('\n').Append(text5);
					num++;
					continue;
				}
				if (!LooksActionable(text4) && !TimestampPrefix.IsMatch(text4))
				{
				}
				break;
			}
			AddOccurrence(text3, stringBuilder.ToString(), sourceFile, archived);
		}
	}

	private void AddOccurrence(string line, string context, string sourceFile, bool archived)
	{
		string category = ClassifyCategory(line, context);
		string text = ClassifyImpact(line, category);
		string text2 = NormalizeSignature(line, category);
		if (string.IsNullOrWhiteSpace(text2))
		{
			return;
		}
		long num = ParseLogTimestampUnixMilliseconds(line) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		badBlockEntityTracker.Observe(line, context, archived, num);
		ModAttribution modAttribution = AttributeMod(line + "\n" + context);
		lock (sync)
		{
			if (!issues.TryGetValue(text2, out LogIssueState value))
			{
				if (issues.Count >= 750)
				{
					string text3 = (from x in issues.Values
						orderby SeverityRank(x.Impact) descending, x.LastSeenUnixMilliseconds
						select x.Signature).FirstOrDefault();
					if (text3 != null)
					{
						issues.Remove(text3);
					}
				}
				value = new LogIssueState
				{
					Signature = text2,
					Title = CleanTitle(line),
					Category = category,
					Impact = text,
					FirstSeenUnixMilliseconds = num,
					LastSeenUnixMilliseconds = num,
					LikelyModId = modAttribution.ModId,
					LikelyModName = modAttribution.ModName,
					AttributionConfidence = modAttribution.Confidence,
					Diagnosis = BuildDiagnosis(category, modAttribution.ModName),
					RecommendedFix = BuildRecommendedFix(category, modAttribution.ModName)
				};
				issues[text2] = value;
			}
			value.Occurrences++;
			value.FirstSeenUnixMilliseconds = Math.Min(value.FirstSeenUnixMilliseconds, num);
			value.LastSeenUnixMilliseconds = Math.Max(value.LastSeenUnixMilliseconds, num);
			value.SeenInArchivedLogs |= archived;
			if (SeverityRank(text) < SeverityRank(value.Impact))
			{
				value.Impact = text;
			}
			if (string.IsNullOrWhiteSpace(value.LikelyModId) && !string.IsNullOrWhiteSpace(modAttribution.ModId))
			{
				value.LikelyModId = modAttribution.ModId;
				value.LikelyModName = modAttribution.ModName;
				value.AttributionConfidence = modAttribution.Confidence;
			}
			value.Samples.Add(new LogIssueSample
			{
				UnixMilliseconds = num,
				SourceFile = sourceFile,
				Archived = archived,
				Context = ((context.Length > 6000) ? context.Substring(0, 6000) : context)
			});
			while (value.Samples.Count > 5)
			{
				value.Samples.RemoveAt(0);
			}
		}
	}

	private StartupPreflightSnapshot RunPreflight(string bindAddress, int port, bool webEnabled, bool dashboardAlreadyRunning)
	{
		StartupPreflightSnapshot snapshot = new StartupPreflightSnapshot
		{
			GeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			TargetGameVersion = "1.22.7"
		};
		List<ModManifestEntry> list = (from x in api.ModLoader.Mods
			where x?.Info != null
			select new ModManifestEntry
			{
				ModId = (x.Info.ModID ?? ""),
				Name = (x.Info.Name ?? ""),
				Version = (x.Info.Version ?? ""),
				FileName = Path.GetFileName(x.FileName ?? "")
			}).OrderBy((ModManifestEntry x) => x.ModId, StringComparer.OrdinalIgnoreCase).ToList();
		snapshot.LoadedModCount = list.Count;
		string text = list.FirstOrDefault((ModManifestEntry x) => string.Equals(x.ModId, "game", StringComparison.OrdinalIgnoreCase))?.Version ?? "";
		snapshot.LoadedGameVersion = text;
		List<IGrouping<string, ModManifestEntry>> list2 = (from g in list.GroupBy((ModManifestEntry x) => x.ModId, StringComparer.OrdinalIgnoreCase)
			where g.Count() > 1
			select g).ToList();
		foreach (IGrouping<string, ModManifestEntry> item in list2)
		{
			Add("Critical", "Mods", "Duplicate loaded mod ID: " + item.Key, string.Join(", ", item.Select((ModManifestEntry x) => x.FileName + " (" + x.Version + ")")), "Remove duplicate/older packages so exactly one copy of the mod ID is available.");
		}
		if (list2.Count == 0)
		{
			Add("OK", "Mods", "Loaded mod IDs", "No duplicate loaded mod IDs were reported by the mod loader.");
		}
		List<ModPackageManifest> source = ScanModPackages();
		List<IGrouping<string, ModPackageManifest>> list3 = (from g in source.Where((ModPackageManifest x) => !string.IsNullOrWhiteSpace(x.ModId)).GroupBy((ModPackageManifest x) => x.ModId, StringComparer.OrdinalIgnoreCase)
			where g.Select((ModPackageManifest x) => x.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
			select g).ToList();
		foreach (IGrouping<string, ModPackageManifest> item2 in list3)
		{
			Add("High", "Mods", "Multiple packages for " + item2.Key, string.Join(" | ", item2.Select((ModPackageManifest x) => Path.GetFileName(x.SourcePath) + " v" + x.Version)), "Keep only the intended package version in VintagestoryData/Mods, then restart.");
		}
		if (list3.Count == 0)
		{
			Add("OK", "Mods", "Mod package duplicates", "No duplicate mod IDs were found across readable user mod packages.");
		}
		if (!string.IsNullOrWhiteSpace(text))
		{
			Add(string.Equals(text, "1.22.7", StringComparison.OrdinalIgnoreCase) ? "OK" : "High", "Version", "Suite game target", $"Loaded game version is {text}; this suite build targets {"1.22.7"}.", string.Equals(text, "1.22.7", StringComparison.OrdinalIgnoreCase) ? "" : "Use a suite build compiled and tested for the loaded Vintage Story version.");
		}
		HashSet<string> loadedIds = list.Select((ModManifestEntry x) => x.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (ModPackageManifest item3 in source.Where((ModPackageManifest x) => loadedIds.Contains(x.ModId)))
		{
			foreach (KeyValuePair<string, string> dependency in item3.Dependencies)
			{
				if (dependency.Key.Equals("game", StringComparison.OrdinalIgnoreCase))
				{
					if (LooksLikeMajorMinorMismatch(dependency.Value, text))
					{
						Add("High", "Dependency", item3.ModId + " game-version requirement", $"Package declares game dependency '{dependency.Value}' while the loaded game is {text}.", "Verify that this mod version supports the current game version before relying on it.");
					}
				}
				else if (!dependency.Key.Equals("core", StringComparison.OrdinalIgnoreCase) && !loadedIds.Contains(dependency.Key))
				{
					Add("Critical", "Dependency", "Missing dependency for " + item3.ModId, $"{item3.ModId} declares dependency '{dependency.Key}' ({dependency.Value}), but that mod ID is not loaded.", $"Install/enable {dependency.Key} or use a compatible build of {item3.ModId}.");
				}
			}
		}
		AddCommandOwnershipCheck(Add, "restart", "Server restart command");
		AddCommandOwnershipCheck(Add, "shutdown", "Server shutdown command");
		AddCommandOwnershipCheck(Add, "back", "Teleport return command");
		AddCommandOwnershipCheck(Add, "report", "Player report command");
		ScanConfigHealth(Add);
		if (webEnabled && !dashboardAlreadyRunning)
		{
			if (CanBind(bindAddress, port, out string error))
			{
				Add("OK", "Network", "Web dashboard port", $"{bindAddress}:{port} was available during startup preflight.");
			}
			else
			{
				Add("Critical", "Network", "Web dashboard port conflict", $"Cannot bind {bindAddress}:{port}: {error}", "Stop the process using the port or change NewfiesWebServer.json before restart.");
			}
		}
		else if (webEnabled)
		{
			Add("OK", "Network", "Web dashboard port", "Dashboard is already running; self-port collision test was skipped on this re-check.");
		}
		else
		{
			Add("Info", "Network", "Web dashboard disabled", "Web dashboard port validation is not required while the listener is disabled.");
		}
		snapshot.ModChanges = CompareAndSaveManifest(list);
		if (snapshot.ModChanges.Added.Count == 0 && snapshot.ModChanges.Removed.Count == 0 && snapshot.ModChanges.Updated.Count == 0)
		{
			Add("OK", "Regression", "Loaded-mod manifest", snapshot.ModChanges.HasPreviousManifest ? "No loaded-mod changes since the previous recorded startup." : "Baseline loaded-mod manifest recorded for future startup comparisons.");
		}
		else
		{
			Add("Watch", "Regression", "Loaded-mod manifest changed", $"Added {snapshot.ModChanges.Added.Count}, removed {snapshot.ModChanges.Removed.Count}, updated {snapshot.ModChanges.Updated.Count} since the previous recorded startup.", "If new errors or lag appeared after this boot, start investigation with the changed mods listed here.");
		}
		Add("Info", "Harmony", "Harmony overlap inspection", "Deep patch-owner overlap inspection remains manual in Compatibility Center so startup preflight stays cheap.", "Run the Compatibility Center deep scan only when investigating an actual compatibility symptom.");
		snapshot.Critical = snapshot.Checks.Count((PreflightCheck x) => x.Severity == "Critical");
		snapshot.High = snapshot.Checks.Count((PreflightCheck x) => x.Severity == "High");
		snapshot.Watch = snapshot.Checks.Count((PreflightCheck x) => x.Severity == "Watch");
		StartupPreflightSnapshot startupPreflightSnapshot = snapshot;
		string status;
		if (snapshot.Critical > 0)
		{
			status = "Critical";
		}
		else if (snapshot.High > 0)
		{
			status = "Investigate";
		}
		else
		{
			status = ((snapshot.Watch > 0) ? "Watch" : "Ready");
		}
		startupPreflightSnapshot.Status = status;
		return snapshot;
		void Add(string severity, string category, string title, string detail, string action = "")
		{
			snapshot.Checks.Add(new PreflightCheck
			{
				Severity = severity,
				Category = category,
				Title = title,
				Detail = detail,
				RecommendedAction = action
			});
		}
	}

	private void AddCommandOwnershipCheck(Action<string, string, string, string, string> add, string commandName, string title)
	{
		try
		{
			IChatCommand chatCommand = api.ChatCommands.Get(commandName);
			if (chatCommand == null)
			{
				add("Info", "Commands", title, "/" + commandName + " is not currently registered.", "No action required unless a provider is expected to own this command.");
				return;
			}
			string text = chatCommand.GetType().Assembly.GetName().Name ?? chatCommand.GetType().FullName ?? "unknown";
			string value = (text.Contains("NewfiesServerEssentialsSuite", StringComparison.OrdinalIgnoreCase) ? "suite" : "external/provider");
			add("OK", "Commands", title, $"/{commandName} is registered by {text} ({value}).", "Provider detection should preserve the current owner; use /nse namespaced fallbacks where applicable.");
		}
		catch (Exception ex)
		{
			add("Watch", "Commands", title, $"Could not inspect /{commandName}: {ex.GetType().Name}: {ex.Message}", "Check the startup log if a command collision is reported.");
		}
	}

	private void ScanConfigHealth(Action<string, string, string, string, string> add)
	{
		if (!Directory.Exists(configRoot))
		{
			add("Info", "Config", "Suite config folder", "ModConfig/NewfiesServerEssentialsSuite/ is not present or not readable yet.", "No action required if this is an early/fresh startup.");
			return;
		}
		string[] array = (from name in Directory.EnumerateFiles(configRoot, "*.json", SearchOption.TopDirectoryOnly).Select(Path.GetFileName)
			where !string.IsNullOrWhiteSpace(name)
			select name).Cast<string>().OrderBy((string name) => name, StringComparer.OrdinalIgnoreCase).ToArray();
		int num = 0;
		string[] array2 = array;
		foreach (string text in array2)
		{
			string path = Path.Combine(configRoot, text);
			num++;
			try
			{
				using (JsonDocument.Parse(File.ReadAllText(path)))
				{
				}
			}
			catch (Exception ex)
			{
				add("High", "Config", "Invalid configuration: " + text, "JSON parse failed: " + ex.Message, "Fix or restore the configuration from a known-good snapshot, then restart/reload the affected subsystem.");
			}
		}
		if (num > 0)
		{
			add("OK", "Config", "Suite configuration syntax", $"Checked {num} JSON files in ModConfig/{"NewfiesServerEssentialsSuite"}/ for basic readability.", "");
		}
		(string, string)[] array3 = new (string, string)[3]
		{
			("blocklogger-v2.json", "NewfiesBlocklogger.json"),
			("loginprotection.json", "NewfiesLoginProtection.json"),
			("animal_limiter.json", "NewfiesAnimalLimiter.json")
		};
		(string, string)[] array4 = array3;
		for (int num3 = 0; num3 < array4.Length; num3++)
		{
			(string, string) tuple = array4[num3];
			string path2 = Path.Combine(configRoot, tuple.Item1);
			string path3 = Path.Combine(configRoot, tuple.Item2);
			if (File.Exists(path2) && File.Exists(path3))
			{
				add("Watch", "Config", "Legacy config still present: " + tuple.Item1, $"Both {tuple.Item1} and {tuple.Item2} exist. The current Newfies-prefixed file is authoritative after migration.", "Archive/remove the legacy file once you have verified the migrated current config.");
			}
		}
	}

	private List<ModPackageManifest> ScanModPackages()
	{
		List<ModPackageManifest> list = new List<ModPackageManifest>();
		if (!Directory.Exists(modsRoot))
		{
			return list;
		}
		foreach (string item in Directory.EnumerateFiles(modsRoot, "*.zip", SearchOption.TopDirectoryOnly).Take(500))
		{
			try
			{
				using ZipArchive zipArchive = ZipFile.OpenRead(item);
				ZipArchiveEntry zipArchiveEntry = zipArchive.Entries.FirstOrDefault((ZipArchiveEntry x) => string.Equals(x.FullName.TrimStart('/'), "modinfo.json", StringComparison.OrdinalIgnoreCase));
				if (zipArchiveEntry == null)
				{
					continue;
				}
				using StreamReader streamReader = new StreamReader(zipArchiveEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
				string json = streamReader.ReadToEnd();
				ModPackageManifest modPackageManifest = ParsePackageManifest(json, item);
				if (modPackageManifest != null)
				{
					list.Add(modPackageManifest);
				}
			}
			catch
			{
			}
		}
		foreach (string item2 in Directory.EnumerateDirectories(modsRoot, "*", SearchOption.TopDirectoryOnly).Take(500))
		{
			string path = Path.Combine(item2, "modinfo.json");
			if (!File.Exists(path))
			{
				continue;
			}
			try
			{
				ModPackageManifest modPackageManifest2 = ParsePackageManifest(File.ReadAllText(path), item2);
				if (modPackageManifest2 != null)
				{
					list.Add(modPackageManifest2);
				}
			}
			catch
			{
			}
		}
		return list;
	}

	private static ModPackageManifest? ParsePackageManifest(string json, string sourcePath)
	{
		using JsonDocument jsonDocument = JsonDocument.Parse(json);
		if (jsonDocument.RootElement.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		JsonElement rootElement = jsonDocument.RootElement;
		string text = (rootElement.TryGetProperty("modid", out var value) ? (value.GetString() ?? "") : "");
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		string version = (rootElement.TryGetProperty("version", out var value2) ? (value2.GetString() ?? "") : "");
		ModPackageManifest modPackageManifest = new ModPackageManifest
		{
			ModId = text,
			Version = version,
			SourcePath = sourcePath
		};
		if (rootElement.TryGetProperty("dependencies", out var value3) && value3.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty item in value3.EnumerateObject())
			{
				modPackageManifest.Dependencies[item.Name] = ((item.Value.ValueKind == JsonValueKind.String) ? (item.Value.GetString() ?? "") : item.Value.ToString());
			}
		}
		return modPackageManifest;
	}

	private static bool LooksLikeMajorMinorMismatch(string requirement, string loadedVersion)
	{
		if (string.IsNullOrWhiteSpace(requirement) || string.IsNullOrWhiteSpace(loadedVersion))
		{
			return false;
		}
		Match match = Regex.Match(requirement, "(?<!\\d)(\\d+)\\.(\\d+)");
		Match match2 = Regex.Match(loadedVersion, "(?<!\\d)(\\d+)\\.(\\d+)");
		if (!match.Success || !match2.Success)
		{
			return false;
		}
		return match.Groups[1].Value != match2.Groups[1].Value || match.Groups[2].Value != match2.Groups[2].Value;
	}

	private ModChangeSummary CompareAndSaveManifest(List<ModManifestEntry> current)
	{
		ModChangeSummary modChangeSummary = new ModChangeSummary();
		List<ModManifestEntry> list = new List<ModManifestEntry>();
		try
		{
			if (File.Exists(manifestPath))
			{
				list = JsonSerializer.Deserialize<List<ModManifestEntry>>(File.ReadAllText(manifestPath), JsonOptions) ?? new List<ModManifestEntry>();
				modChangeSummary.HasPreviousManifest = true;
			}
		}
		catch
		{
		}
		Dictionary<string, ModManifestEntry> dictionary = list.Where((ModManifestEntry x) => !string.IsNullOrWhiteSpace(x.ModId)).GroupBy((ModManifestEntry x) => x.ModId, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, ModManifestEntry> g) => g.Key, (IGrouping<string, ModManifestEntry> g) => g.First(), StringComparer.OrdinalIgnoreCase);
		Dictionary<string, ModManifestEntry> dictionary2 = current.Where((ModManifestEntry x) => !string.IsNullOrWhiteSpace(x.ModId)).GroupBy((ModManifestEntry x) => x.ModId, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, ModManifestEntry> g) => g.Key, (IGrouping<string, ModManifestEntry> g) => g.First(), StringComparer.OrdinalIgnoreCase);
		foreach (ModManifestEntry item in current)
		{
			if (!dictionary.TryGetValue(item.ModId, out var value))
			{
				modChangeSummary.Added.Add((item.ModId + " " + item.Version).Trim());
			}
			else if (!string.Equals(value.Version, item.Version, StringComparison.OrdinalIgnoreCase) || !string.Equals(value.FileName, item.FileName, StringComparison.OrdinalIgnoreCase))
			{
				modChangeSummary.Updated.Add($"{item.ModId}: {value.Version} -> {item.Version}");
			}
		}
		foreach (ModManifestEntry item2 in list)
		{
			if (!dictionary2.ContainsKey(item2.ModId))
			{
				modChangeSummary.Removed.Add((item2.ModId + " " + item2.Version).Trim());
			}
		}
		try
		{
			File.WriteAllText(manifestPath, JsonSerializer.Serialize(current, JsonOptions));
		}
		catch
		{
		}
		return modChangeSummary;
	}

	private void BuildModAliases()
	{
		List<ModAlias> list = new List<ModAlias>();
		foreach (Mod item in api.ModLoader.Mods.Where((Mod x) => x?.Info != null))
		{
			string text = item.Info.ModID ?? "";
			string text2 = item.Info.Name ?? text;
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(item.FileName ?? "");
			if (!string.IsNullOrWhiteSpace(text) && !text.Equals("game", StringComparison.OrdinalIgnoreCase) && !text.Equals("core", StringComparison.OrdinalIgnoreCase) && !text.Equals("creative", StringComparison.OrdinalIgnoreCase) && !text.Equals("survival", StringComparison.OrdinalIgnoreCase))
			{
				list.Add(new ModAlias
				{
					ModId = text,
					ModName = text2,
					Aliases = (from x in new string[3] { text, text2, fileNameWithoutExtension }.Where((string x) => !string.IsNullOrWhiteSpace(x) && x.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase)
						orderby x.Length descending
						select x).ToArray()
				});
			}
		}
		lock (sync)
		{
			modAliases.Clear();
			modAliases.AddRange(list.OrderByDescending((ModAlias x) => (x.Aliases.Length != 0) ? x.Aliases.Max((string a) => a.Length) : 0));
		}
	}

	private ModAttribution AttributeMod(string text)
	{
		if (text.Contains("BlockLogger", StringComparison.OrdinalIgnoreCase) || text.Contains("WebDashboard", StringComparison.OrdinalIgnoreCase) || text.Contains("ServerEssentials", StringComparison.OrdinalIgnoreCase) || text.Contains("NewfiesServerEssentialsSuite", StringComparison.OrdinalIgnoreCase))
		{
			return new ModAttribution("newfiesserveressentialssuite", "Newfies Server Essentials Suite", "High");
		}
		List<ModAlias> list;
		lock (sync)
		{
			list = modAliases.ToList();
		}
		foreach (ModAlias item in list)
		{
			string[] aliases = item.Aliases;
			foreach (string text2 in aliases)
			{
				if (text.IndexOf(text2, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return new ModAttribution(item.ModId, item.ModName, text2.Equals(item.ModId, StringComparison.OrdinalIgnoreCase) ? "High" : "Medium");
				}
			}
		}
		return new ModAttribution("", "", "Low");
	}

	private LogIssueView ToView(LogIssueState state, ServerHealthSnapshot? health)
	{
		string healthCorrelation = "No profiler/health correlation is currently available.";
		if (health != null)
		{
			List<string> list = new List<string>();
			if (health.Current.MainThreadDelayMs >= 50.0)
			{
				list.Add($"current main-thread delay {health.Current.MainThreadDelayMs:0} ms");
			}
			if (health.Current.BlockLoggerQueue >= 1000)
			{
				list.Add($"BlockLogger queue {health.Current.BlockLoggerQueue:N0}");
			}
			if (!string.IsNullOrWhiteSpace(state.LikelyModId))
			{
				ServerProfilerModHotspot serverProfilerModHotspot = health.ModHotspots.FirstOrDefault((ServerProfilerModHotspot x) => string.Equals(x.ModId, state.LikelyModId, StringComparison.OrdinalIgnoreCase));
				if (serverProfilerModHotspot != null && serverProfilerModHotspot.TotalMs > 0.0)
				{
					list.Add($"profiler attributed {serverProfilerModHotspot.PercentOfProfileTickTime:0.0}% of profile tick time to {serverProfilerModHotspot.ModName} ({serverProfilerModHotspot.Confidence} confidence)");
				}
			}
			if (list.Count > 0)
			{
				healthCorrelation = string.Join("; ", list) + ".";
			}
		}
		return new LogIssueView
		{
			Signature = state.Signature,
			Title = state.Title,
			Category = state.Category,
			Impact = state.Impact,
			Occurrences = state.Occurrences,
			FirstSeenUnixMilliseconds = state.FirstSeenUnixMilliseconds,
			LastSeenUnixMilliseconds = state.LastSeenUnixMilliseconds,
			LikelyModId = state.LikelyModId,
			LikelyModName = state.LikelyModName,
			AttributionConfidence = state.AttributionConfidence,
			Diagnosis = state.Diagnosis,
			RecommendedFix = state.RecommendedFix,
			HealthCorrelation = healthCorrelation,
			ActivityState = BuildActivityState(state.LastSeenUnixMilliseconds),
			SeenInArchivedLogs = state.SeenInArchivedLogs,
			Samples = state.Samples.ToList()
		};
	}

	private string ResolveCurrentLogPath()
	{
		string text = Path.Combine(logsRoot, "server-main.log");
		if (File.Exists(text))
		{
			return text;
		}
		if (!Directory.Exists(logsRoot))
		{
			return text;
		}
		return (from x in Directory.EnumerateFiles(logsRoot, "server-main*.log", SearchOption.TopDirectoryOnly)
			select new FileInfo(x) into x
			orderby x.LastWriteTimeUtc descending
			select x.FullName).FirstOrDefault() ?? text;
	}

	private string ResolveDataDirectory(string name)
	{
		try
		{
			return api.GetOrCreateDataPath(name);
		}
		catch
		{
			string orCreateDataPath = api.GetOrCreateDataPath("ModData");
			string path = Directory.GetParent(orCreateDataPath)?.FullName ?? orCreateDataPath;
			string text = Path.Combine(path, name);
			Directory.CreateDirectory(text);
			return text;
		}
	}

	private static bool CanBind(string bindAddress, int port, out string error)
	{
		error = "";
		TcpListener tcpListener = null;
		try
		{
			string text = (bindAddress ?? "127.0.0.1").Trim();
			IPAddress localaddr;
			if (text.Equals("localhost", StringComparison.OrdinalIgnoreCase))
			{
				localaddr = IPAddress.Loopback;
			}
			else if (text == "*" || text == "0.0.0.0")
			{
				localaddr = IPAddress.Any;
			}
			else if (text == "::" || text == "[::]")
			{
				localaddr = IPAddress.IPv6Any;
			}
			else
			{
				if (!IPAddress.TryParse(text, out IPAddress address))
				{
					error = "bind address is not a numeric IP/localhost; live listener will resolve it normally";
					return true;
				}
				localaddr = address;
			}
			tcpListener = new TcpListener(localaddr, port);
			tcpListener.Start(1);
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
		finally
		{
			try
			{
				tcpListener?.Stop();
			}
			catch
			{
			}
		}
	}

	private static bool LooksActionable(string line)
	{
		if (string.IsNullOrWhiteSpace(line) || line.Length < 8)
		{
			return false;
		}
		return line.Contains("[Error]", StringComparison.OrdinalIgnoreCase) || line.Contains("[Fatal]", StringComparison.OrdinalIgnoreCase) || line.Contains("[Warning]", StringComparison.OrdinalIgnoreCase) || line.Contains("[Warn]", StringComparison.OrdinalIgnoreCase) || line.Contains("Exception", StringComparison.OrdinalIgnoreCase) || line.Contains(" failed", StringComparison.OrdinalIgnoreCase) || line.Contains("failure", StringComparison.OrdinalIgnoreCase) || line.Contains("duplicate", StringComparison.OrdinalIgnoreCase) || line.Contains("already registered", StringComparison.OrdinalIgnoreCase);
	}

	private static string ClassifyCategory(string line, string context)
	{
		string text = line + " " + context;
		if (ContainsAny(text, "failed to load mod", "could not load mod", "error loading mod", "mod startup", "startserverside", "startpre"))
		{
			return "Mod startup failures";
		}
		if (ContainsAny(text, "missing dependency", "dependency missing", "requires mod", "dependency"))
		{
			return "Dependency problems";
		}
		if (ContainsAny(text, "already registered", "duplicate item", "duplicate block", "duplicate registration", "same code", "command") && ContainsAny(text, "duplicate", "already", "collision", "registered"))
		{
			return "Duplicate registration / command collisions";
		}
		if (ContainsAny(text, "recipe") && ContainsAny(text, "failed", "error", "warning", "invalid", "could not"))
		{
			return "Recipe / patch failures";
		}
		if (ContainsAny(text, "json patch", "patch") && ContainsAny(text, "failed", "error", "warning", "could not", "invalid"))
		{
			return "Recipe / patch failures";
		}
		if (ContainsAny(text, "missing attribute", "attribute missing", "no attribute", "attribute not found"))
		{
			return "Missing entity attributes";
		}
		if (ContainsAny(text, "socket", "tcp", "network", "connection failed", "connection reset", "httplistener", "webdashboard", "broken pipe"))
		{
			return "Network / web failures";
		}
		if (ContainsAny(text, "config", ".json") && ContainsAny(text, "invalid", "failed", "exception", "error", "malformed"))
		{
			return "Configuration failures";
		}
		if (ContainsAny(text, "harmony", "patching exception", "patch owner"))
		{
			return "Harmony / patch overlap";
		}
		if (ContainsAny(text, "exception", "stack trace", "nullreference", "argumentexception", "invalidoperationexception", "indexoutofrange"))
		{
			return "Runtime exceptions";
		}
		return "Informational warnings";
	}

	private static string ClassifyImpact(string line, string category)
	{
		bool flag = line.Contains("[Fatal]", StringComparison.OrdinalIgnoreCase) || line.Contains("fatal", StringComparison.OrdinalIgnoreCase);
		bool flag2 = line.Contains("[Error]", StringComparison.OrdinalIgnoreCase) || line.Contains("exception", StringComparison.OrdinalIgnoreCase) || line.Contains(" failed", StringComparison.OrdinalIgnoreCase);
		if (flag)
		{
			return "Critical";
		}
		if ((category == "Mod startup failures" || category == "Dependency problems") ? true : false)
		{
			return flag2 ? "Critical" : "High";
		}
		bool flag3;
		switch (category)
		{
		case "Runtime exceptions":
		case "Network / web failures":
		case "Configuration failures":
			flag3 = true;
			break;
		default:
			flag3 = false;
			break;
		}
		if (flag3)
		{
			return flag2 ? "High" : "Moderate";
		}
		switch (category)
		{
		case "Duplicate registration / command collisions":
			return flag2 ? "High" : "Moderate";
		case "Recipe / patch failures":
		case "Missing entity attributes":
		case "Harmony / patch overlap":
			flag3 = true;
			break;
		default:
			flag3 = false;
			break;
		}
		if (flag3)
		{
			return flag2 ? "Moderate" : "Low";
		}
		return flag2 ? "Moderate" : "Low";
	}

	private static string NormalizeSignature(string line, string category)
	{
		string text = line.Trim();
		int num = text.IndexOf(']');
		if (TimestampPrefix.IsMatch(text) && num >= 0 && num < 40)
		{
			text = text.Substring(num + 1).Trim();
		}
		text = GuidRegex.Replace(text, "<guid>");
		text = HexRegex.Replace(text, "<hex>");
		text = CoordinateRegex.Replace(text, "<xyz>");
		text = WindowsPathRegex.Replace(text, "<path>");
		text = UnixPathRegex.Replace(text, "<path>");
		text = NumberRegex.Replace(text, "<n>");
		text = Regex.Replace(text, "\\s+", " ").Trim();
		if (text.Length > 420)
		{
			text = text.Substring(0, 420);
		}
		return category + "|" + text.ToLowerInvariant();
	}

	private static long? ParseLogTimestampUnixMilliseconds(string line)
	{
		if (string.IsNullOrWhiteSpace(line) || !char.IsDigit(line.TrimStart()[0]))
		{
			return null;
		}
		string text = line.TrimStart();
		int num = text.IndexOf('[');
		string s = ((num > 0 && num <= 40) ? text.Substring(0, num).Trim() : text.Substring(0, Math.Min(32, text.Length)).Trim());
		string[] formats = new string[8] { "d.M.yyyy H:mm:ss", "dd.MM.yyyy HH:mm:ss", "d.M.yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "M/d/yyyy h:mm:ss tt", "M/d/yyyy H:mm:ss", "d/M/yyyy H:mm:ss" };
		if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var result))
		{
			return new DateTimeOffset(result).ToUniversalTime().ToUnixTimeMilliseconds();
		}
		if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var result2))
		{
			return new DateTimeOffset(result2).ToUniversalTime().ToUnixTimeMilliseconds();
		}
		return null;
	}

	private static string CleanTitle(string line)
	{
		string text = Regex.Replace(line.Trim(), "\\s+", " ");
		if (text.Length > 240)
		{
			text = text.Substring(0, 240) + "…";
		}
		return text;
	}

	private static string BuildDiagnosis(string category, string modName)
	{
		if (1 == 0)
		{
		}
		string result = category switch
		{
			"Mod startup failures" => "A mod failed during initialization" + ModSuffix(modName) + ". This can leave commands, hooks or content only partially registered.", 
			"Dependency problems" => "A required mod/version relationship appears unsatisfied" + ModSuffix(modName) + ".", 
			"Duplicate registration / command collisions" => "Two providers appear to be registering the same command/content key" + ModSuffix(modName) + ".", 
			"Recipe / patch failures" => "A recipe or JSON/Harmony patch operation failed" + ModSuffix(modName) + ". Gameplay content may be missing or partially applied.", 
			"Missing entity attributes" => "Code expected an entity attribute that was absent" + ModSuffix(modName) + ". Repetition can indicate a compatibility or bad-state problem.", 
			"Network / web failures" => "A network/listener/connection path reported failure" + ModSuffix(modName) + ".", 
			"Configuration failures" => "Configuration data could not be read or validated cleanly" + ModSuffix(modName) + ".", 
			"Harmony / patch overlap" => "Harmony patching reported a warning/failure" + ModSuffix(modName) + ". Overlap is evidence to inspect, not automatic proof of incompatibility.", 
			"Runtime exceptions" => "Runtime code threw an exception" + ModSuffix(modName) + ". Repeated occurrences are more significant than a one-off handled exception.", 
			_ => "The server emitted a warning" + ModSuffix(modName) + ". Review frequency and surrounding context before treating it as a fault.", 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static string BuildRecommendedFix(string category, string modName)
	{
		if (1 == 0)
		{
		}
		string result = category switch
		{
			"Mod startup failures" => "Verify the implicated mod's game version/dependencies, remove duplicate packages, then restart and confirm the signature no longer appears.", 
			"Dependency problems" => "Install/enable the required dependency or switch to a mod version whose declared dependencies match the server.", 
			"Duplicate registration / command collisions" => "Keep one provider for the root command/content key. Prefer provider detection and namespaced /nse fallbacks rather than forcing ownership.", 
			"Recipe / patch failures" => "Open a sample/stack trace, identify the target asset/mod, then verify load order and compatibility before changing the patch.", 
			"Missing entity attributes" => "Identify the entity/mod in the sample, reproduce once if safe, and update/fix the owning mod if the warning repeats.", 
			"Network / web failures" => "Check bind address/port, firewall/reverse proxy and disconnect context. Do not restart blindly if the listener is otherwise healthy.", 
			"Configuration failures" => "Validate the named JSON/config, compare with a known-good snapshot, and restore only the affected file if required.", 
			"Harmony / patch overlap" => "Run Compatibility Center's manual deep Harmony scan and compare patch owners before disabling either mod.", 
			"Runtime exceptions" => "Use the full sample stack trace and implicated mod attribution; correlate with profiler/health evidence and update or fix the responsible code path.", 
			_ => "Monitor recurrence. Escalate only if frequency rises, gameplay breaks, or health/profiler evidence correlates with the warning.", 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static string BuildActivityState(long lastSeenUnixMilliseconds)
	{
		if (lastSeenUnixMilliseconds <= 0)
		{
			return "Unknown";
		}
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastSeenUnixMilliseconds;
		if (num <= 900000)
		{
			return "Active";
		}
		if (num <= 86400000)
		{
			return "Recent";
		}
		return "Historical";
	}

	private static string ModSuffix(string modName)
	{
		return string.IsNullOrWhiteSpace(modName) ? "" : ("; likely owner: " + modName);
	}

	private static bool ContainsAny(string text, params string[] values)
	{
		return values.Any((string x) => text.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
	}

	private static int SeverityRank(string severity)
	{
		if (1 == 0)
		{
		}
		int result = severity switch
		{
			"Critical" => 0, 
			"High" => 1, 
			"Moderate" => 2, 
			"Watch" => 2, 
			"Low" => 3, 
			"Info" => 4, 
			"OK" => 5, 
			_ => 6, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private void SavePreflight(StartupPreflightSnapshot snapshot, bool archiveHistory)
	{
		try
		{
			string contents = JsonSerializer.Serialize(snapshot, JsonOptions);
			string text = preflightPath + ".tmp";
			File.WriteAllText(text, contents);
			File.Move(text, preflightPath, overwrite: true);
			if (!archiveHistory)
			{
				return;
			}
			string text2 = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
			string path = Path.Combine(preflightHistoryRoot, "startup-preflight-" + text2 + ".json");
			File.WriteAllText(path, contents);
			foreach (FileInfo item in (from x in Directory.EnumerateFiles(preflightHistoryRoot, "startup-preflight-*.json")
				select new FileInfo(x) into x
				orderby x.LastWriteTimeUtc descending
				select x).Skip(20))
			{
				try
				{
					item.Delete();
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}

	private void LoadState()
	{
		try
		{
			if (!File.Exists(statePath))
			{
				return;
			}
			LogIntelligencePersistedState logIntelligencePersistedState = JsonSerializer.Deserialize<LogIntelligencePersistedState>(File.ReadAllText(statePath), JsonOptions);
			if (logIntelligencePersistedState == null)
			{
				return;
			}
			lock (sync)
			{
				issues.Clear();
				foreach (LogIssueState item in logIntelligencePersistedState.Issues.Take(750))
				{
					if (!string.IsNullOrWhiteSpace(item.Signature))
					{
						issues[item.Signature] = item;
					}
				}
				lastScanUnixMilliseconds = logIntelligencePersistedState.LastScanUnixMilliseconds;
				lastArchiveScanUnixMilliseconds = logIntelligencePersistedState.LastArchiveScanUnixMilliseconds;
				archivedScanFingerprints.Clear();
				foreach (string item2 in logIntelligencePersistedState.ArchivedScanFingerprints.Take(2000))
				{
					archivedScanFingerprints.Add(item2);
				}
				fileOffsets.Clear();
				foreach (KeyValuePair<string, long> item3 in logIntelligencePersistedState.FileOffsets.Take(20))
				{
					if (!string.IsNullOrWhiteSpace(item3.Key) && item3.Value >= 0)
					{
						fileOffsets[item3.Key] = item3.Value;
					}
				}
			}
		}
		catch
		{
		}
	}

	private void SaveState()
	{
		try
		{
			LogIntelligencePersistedState value;
			lock (sync)
			{
				value = new LogIntelligencePersistedState
				{
					LastScanUnixMilliseconds = lastScanUnixMilliseconds,
					LastArchiveScanUnixMilliseconds = lastArchiveScanUnixMilliseconds,
					Issues = (from x in issues.Values
						orderby SeverityRank(x.Impact), x.LastSeenUnixMilliseconds descending
						select x).Take(750).ToList(),
					ArchivedScanFingerprints = archivedScanFingerprints.TakeLast(2000).ToList(),
					FileOffsets = (from x in fileOffsets
						orderby string.Equals(x.Key, currentLogPath, StringComparison.OrdinalIgnoreCase) descending, x.Value descending
						select x).Take(20).ToDictionary((KeyValuePair<string, long> x) => x.Key, (KeyValuePair<string, long> x) => x.Value, StringComparer.OrdinalIgnoreCase)
				};
			}
			string text = statePath + ".tmp";
			File.WriteAllText(text, JsonSerializer.Serialize(value, JsonOptions));
			File.Move(text, statePath, overwrite: true);
			badBlockEntityTracker.SaveState();
		}
		catch
		{
		}
	}
}
