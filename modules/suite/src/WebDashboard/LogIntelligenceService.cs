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
using Vintagestory.API.Server;

namespace BlockLogger.WebDashboard;

/// <summary>
/// Low-overhead log diagnosis and startup preflight service.
///
/// Design rules:
/// - The live server-main.log is tailed incrementally; it is never reparsed every tick.
/// - Archived logs are only scanned after an explicit admin request.
/// - Issue signatures are normalized so repeated warnings collapse into one actionable issue.
/// - The service recommends fixes but does not automatically modify third-party mods/configuration.
/// </summary>
public sealed class LogIntelligenceService : IDisposable
{
    private const int MaxIssueSignatures = 750;
    private const int MaxSamplesPerIssue = 5;
    private const int MaxLiveBootstrapBytes = 4 * 1024 * 1024;
    private const int MaxArchivedBytesPerFile = 8 * 1024 * 1024;
    private const int MaxArchivedFiles = 24;
    private const int MaxArchivedFingerprints = 2000;
    private readonly IncrementalLogReader liveLogReader = new();
    private long liveLogBacklogBytes, oversizedLogLines;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly Regex TimestampPrefix = new(
        @"^\s*(?:\d{1,4}[-./]\d{1,2}[-./]\d{1,4}|\d{1,2}:\d{2}:\d{2}|\[\d{1,2}:\d{2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GuidRegex = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HexRegex = new(
        @"\b0x[0-9a-fA-F]+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NumberRegex = new(
        @"(?<![A-Za-z])[-+]?\d+(?:\.\d+)?(?![A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CoordinateRegex = new(
        @"\b-?\d+\s*[,/]\s*-?\d+\s*[,/]\s*-?\d+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsPathRegex = new(
        @"\b[A-Za-z]:\\[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnixPathRegex = new(
        @"(?<!:)\/(?:[^\s:/]+\/)+[^\s:]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ICoreServerAPI api;
    private readonly Func<ServerHealthSnapshot?> healthProvider;
    private readonly BadBlockEntityTracker badBlockEntityTracker;
    private readonly object sync = new();
    private readonly Dictionary<string, LogIssueState> issues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> fileOffsets = new(StringComparer.OrdinalIgnoreCase);
    private ModAlias[] modAliases = Array.Empty<ModAlias>();
    private readonly Dictionary<string, RecentLogIssueCounts> recentIssues = new(StringComparer.Ordinal);
    private readonly long observationStarted = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly HashSet<string> archivedScanFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly string diagnosticsRoot;
    private readonly string statePath;
    private readonly string manifestPath;
    private readonly string preflightPath;
    private readonly string preflightHistoryRoot;
    private readonly string logsRoot;
    private readonly string modsRoot;
    private readonly string configRoot;

    private StartupPreflightSnapshot preflight = new();
    private long scanListenerId = -1;
    private long lastScanUnixMilliseconds;
    private long lastArchiveScanUnixMilliseconds;
    private string currentLogPath = "";
    private int scanInProgress;
    private bool disposed;

    public LogIntelligenceService(
        ICoreServerAPI api,
        Func<ServerHealthSnapshot?> healthProvider)
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

        // This runs before WebDashboardService binds its listener, so a port test here
        // can distinguish a real collision from the suite's own listener.
        preflight = RunPreflight(bindAddress, port, webEnabled, dashboardAlreadyRunning: false);
        SavePreflight(preflight, archiveHistory: true);
        api.Logger.Notification(
            "[LogIntelligence] Startup preflight: {0}. Critical={1}, High={2}, Watch={3}, Mods={4}, Game={5}.",
            preflight.Status,
            preflight.Critical,
            preflight.High,
            preflight.Watch,
            preflight.LoadedModCount,
            preflight.LoadedGameVersion);
        foreach (var check in preflight.Checks.Where(x => x.Severity is "Critical" or "High" or "Watch").Take(20))
            api.Logger.Warning("[LogIntelligence/Preflight] {0} {1}: {2} - {3}", check.Severity, check.Category, check.Title, check.Detail);
        SaveState();

        TriggerIncrementalScan(bootstrap: true);
        if (scanListenerId < 0)
        {
            scanListenerId = api.Event.RegisterGameTickListener(
                _ => TriggerIncrementalScan(bootstrap: false),
                30_000);
        }
    }

    public StartupPreflightSnapshot RefreshPreflight(
        string bindAddress,
        int port,
        bool webEnabled,
        bool dashboardAlreadyRunning)
    {
        BuildModAliases();
        var snapshot = RunPreflight(bindAddress, port, webEnabled, dashboardAlreadyRunning);
        lock (sync) preflight = snapshot;
        SavePreflight(snapshot, archiveHistory: false);
        SaveState();
        return snapshot;
    }

    public async Task RunManualScanAsync(bool includeArchived)
    {
        if (Interlocked.CompareExchange(ref scanInProgress, 1, 0) != 0)
            return;

        try
        {
            await Task.Run(() =>
            {
                ScanCurrentLog(forceRecentWindow: false);
                if (includeArchived) ScanArchivedLogs();
                SaveState();
            }).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref scanInProgress, 0);
        }
    }

    public LogIntelligenceSnapshot GetSnapshot()
    {
        ServerHealthSnapshot? health = null;
        try { health = healthProvider(); } catch { }

        List<LogIssueView> issueViews;
        StartupPreflightSnapshot preflightCopy;
        long lastScan;
        long archiveScan;
        string logPath;

        lock (sync)
        {
            issueViews = issues.Values
                .Select(x => ToView(x, health))
                .OrderBy(x => SeverityRank(x.Impact))
                .ThenByDescending(x => x.LastSeenUnixMilliseconds)
                .ThenByDescending(x => x.Occurrences)
                .Take(MaxIssueSignatures)
                .ToList();
            preflightCopy = preflight;
            lastScan = lastScanUnixMilliseconds;
            archiveScan = lastArchiveScanUnixMilliseconds;
            logPath = currentLogPath;
        }

        int critical = issueViews.Count(x => x.Impact == "Critical");
        int high = issueViews.Count(x => x.Impact == "High");
        int moderate = issueViews.Count(x => x.Impact == "Moderate");
        int low = issueViews.Count(x => x.Impact == "Low");
        int score = Math.Clamp(100 - critical * 20 - high * 8 - moderate * 3 - low, 0, 100);
        string state = score >= 90 ? "Healthy" : score >= 75 ? "Watch" : score >= 50 ? "Investigate" : "Critical";

        var categories = issueViews
            .GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .Select(g => new LogCategorySummary
            {
                Category = g.Key,
                Occurrences = g.Sum(x => x.Occurrences),
                UniqueIssues = g.Count(),
                Impact = g.OrderBy(x => SeverityRank(x.Impact)).First().Impact
            })
            .OrderBy(x => SeverityRank(x.Impact))
            .ThenByDescending(x => x.Occurrences)
            .ToList();

        return new LogIntelligenceSnapshot
        {
            GeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastScanUnixMilliseconds = lastScan,
            LastArchiveScanUnixMilliseconds = archiveScan,
            CurrentLogPath = logPath,
            LiveLogBacklogBytes = Volatile.Read(ref liveLogBacklogBytes),
            OversizedLogLines = Volatile.Read(ref oversizedLogLines),
            Score = score,
            State = state,
            Critical = critical,
            High = high,
            Moderate = moderate,
            Low = low,
            TotalOccurrences = issueViews.Sum(x => x.Occurrences),
            UniqueIssues = issueViews.Count,
            Categories = categories,
            Issues = issueViews,
            Preflight = preflightCopy,
            BadBlockEntities = badBlockEntityTracker.GetSnapshot(),
            ScanInProgress = Volatile.Read(ref scanInProgress) != 0
        };
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (scanListenerId >= 0)
        {
            api.Event.UnregisterGameTickListener(scanListenerId);
            scanListenerId = -1;
        }
        SaveState();
        badBlockEntityTracker.SaveState();
    }

    public BadBlockEntitySnapshot GetBadBlockEntitySnapshot() => badBlockEntityTracker.GetSnapshot();

    public BadBlockEntitySnapshot GetBadBlockEntityConsoleAlerts() => badBlockEntityTracker.GetConsoleAlertSnapshot();

    public bool TryGetBadBlockEntity(string key, out BadBlockEntityRecord record) =>
        badBlockEntityTracker.TryGet(key, out record);

    public bool SetBadBlockEntityTracking(string key, bool tracking, string actor, out string message) =>
        badBlockEntityTracker.SetTracking(key, tracking, actor, out message);

    public void RecordBadBlockEntityAction(string key, string actor, string action, string detail) =>
        badBlockEntityTracker.RecordAction(key, actor, action, detail);

    public int ClearAllBadBlockEntities(string actor, out string message) =>
        badBlockEntityTracker.ClearAll(actor, out message);

    public int ClearIssueSignatures()
    {
        int cleared;
        lock (sync)
        {
            cleared = issues.Count;
            issues.Clear();
            recentIssues.Clear();
        }
        SaveState();
        return cleared;
    }

    private void TriggerIncrementalScan(bool bootstrap)
    {
        if (disposed) return;

        // The 30-second listener is intentionally cheap at idle. Do not queue a worker
        // or rewrite the persisted issue state unless the live log has actually grown,
        // rotated, or been truncated.
        if (!bootstrap && !CurrentLogNeedsScan()) return;

        if (Interlocked.CompareExchange(ref scanInProgress, 1, 0) != 0) return;

        _ = Task.Run(() =>
        {
            try
            {
                ScanCurrentLog(forceRecentWindow: bootstrap);
                SaveState();
            }
            catch (Exception ex)
            {
                // Keep periodic collection failure-safe and quiet; the next scan retries automatically.
                _ = ex;
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
            string path = ResolveCurrentLogPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            long length = new FileInfo(path).Length;
            lock (sync)
            {
                if (!fileOffsets.TryGetValue(path, out long offset)) return length > 0;
                return length != offset;
            }
        }
        catch
        {
            // If the cheap check cannot determine state, let the normal guarded scan retry.
            return true;
        }
    }

    private void ScanCurrentLog(bool forceRecentWindow)
    {
        string path = ResolveCurrentLogPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            lock (sync)
            {
                currentLogPath = path;
                lastScanUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            return;
        }

        long offset;
        var info = new FileInfo(path);
        lock (sync)
        {
            currentLogPath = path;
            if (!fileOffsets.TryGetValue(path, out offset)) offset = 0;
        }

        if (forceRecentWindow && offset == 0 && info.Length > MaxLiveBootstrapBytes)
            offset = Math.Max(0, info.Length - MaxLiveBootstrapBytes);
        if (info.Length < offset)
            offset = Math.Max(0, info.Length - Math.Min(info.Length, MaxLiveBootstrapBytes));

        string text = liveLogReader.Read(path, offset, LooksActionable);
        long finalOffset = liveLogReader.Offset;
        Volatile.Write(ref liveLogBacklogBytes, liveLogReader.BacklogBytes);
        Volatile.Write(ref oversizedLogLines, liveLogReader.OversizedLines);
        if (!string.IsNullOrWhiteSpace(text)) ParseText(text, Path.GetFileName(path), archived: false);

        lock (sync)
        {
            fileOffsets[path] = finalOffset;
            lastScanUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    private void ScanArchivedLogs()
    {
        if (!Directory.Exists(logsRoot)) return;

        string current = ResolveCurrentLogPath();
        var files = Directory.EnumerateFiles(logsRoot, "server-main*.log", SearchOption.TopDirectoryOnly)
            .Where(x => !string.Equals(Path.GetFullPath(x), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
            .Select(x => new FileInfo(x))
            .OrderByDescending(x => x.LastWriteTimeUtc)
            .Take(MaxArchivedFiles)
            .ToList();

        foreach (var file in files)
        {
            string fingerprint = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            lock (sync)
            {
                if (archivedScanFingerprints.Contains(fingerprint)) continue;
            }

            long start = Math.Max(0, file.Length - MaxArchivedBytesPerFile);
            string text = ReadFileSlice(file.FullName, start, MaxArchivedBytesPerFile, out _);
            if (!string.IsNullOrWhiteSpace(text)) ParseText(text, file.Name, archived: true);
            lock (sync)
            {
                archivedScanFingerprints.Add(fingerprint);
                while (archivedScanFingerprints.Count > MaxArchivedFingerprints)
                {
                    string? oldest = archivedScanFingerprints.FirstOrDefault();
                    if (oldest == null) break;
                    archivedScanFingerprints.Remove(oldest);
                }
            }
        }

        lock (sync)
            lastArchiveScanUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static string ReadFileSlice(string path, long offset, int maxBytes, out long finalOffset)
    {
        finalOffset = offset;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset > stream.Length) offset = 0;
            stream.Seek(offset, SeekOrigin.Begin);

            long remaining = stream.Length - offset;
            int count = (int)Math.Min(remaining, Math.Min(MaxArchivedBytesPerFile, maxBytes <= 0 ? MaxArchivedBytesPerFile : maxBytes));
            if (count <= 0)
            {
                finalOffset = stream.Length;
                return "";
            }

            byte[] buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) break;
                read += n;
            }
            finalOffset = offset + read;
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch
        {
            return "";
        }
    }

    private void ParseText(string text, string sourceFile, bool archived)
    {
        string normalizedNewlines = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalizedNewlines.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (!LooksActionable(line)) continue;

            var context = new StringBuilder(line.Trim());
            int consumed = 0;
            bool blockEntityTickHeader =
                line.IndexOf("threw an error when ticked", StringComparison.OrdinalIgnoreCase) >= 0 &&
                line.IndexOf("At position", StringComparison.OrdinalIgnoreCase) >= 0;
            for (int j = i + 1; j < lines.Length && consumed < 8; j++)
            {
                string next = lines[j];
                if (string.IsNullOrWhiteSpace(next)) break;
                string trimmed = next.Trim();

                // Vintage Story emits block-entity tick exceptions as two timestamped [Error]
                // lines: the position/header first, then "Exception: ...". Keep that second line
                // with the header so the bad-block tracker captures the actual exception text.
                bool pairedBlockEntityException =
                    blockEntityTickHeader &&
                    consumed == 0 &&
                    trimmed.IndexOf("[Error] Exception:", StringComparison.OrdinalIgnoreCase) >= 0;
                if (pairedBlockEntityException)
                {
                    int exceptionIndex = trimmed.IndexOf("Exception:", StringComparison.OrdinalIgnoreCase);
                    context.Append('\n').Append(exceptionIndex >= 0 ? trimmed[exceptionIndex..] : trimmed);
                    consumed++;
                    continue;
                }

                bool stackContinuation =
                    trimmed.StartsWith("at ", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("--->", StringComparison.Ordinal) ||
                    trimmed.StartsWith("Caused by", StringComparison.OrdinalIgnoreCase) ||
                    next.StartsWith(" ") || next.StartsWith("\t");
                if (stackContinuation)
                {
                    context.Append('\n').Append(trimmed);
                    consumed++;
                    continue;
                }
                if (LooksActionable(next) || TimestampPrefix.IsMatch(next)) break;
                break;
            }

            AddOccurrence(line, context.ToString(), sourceFile, archived);
        }
    }

    private void AddOccurrence(string line, string context, string sourceFile, bool archived)
    {
        string category = ClassifyCategory(line, context);
        string impact = ClassifyImpact(line, category);
        string origin = LogIssueEvidence.Origin(context);
        string signature = LogIssueEvidence.WithOrigin(NormalizeSignature(line, category), origin);
        if (string.IsNullOrWhiteSpace(signature)) return;

        long now = ParseLogTimestampUnixMilliseconds(line) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        badBlockEntityTracker.Observe(line, context, archived, now);
        var attribution = LogIssueEvidence.IsNativeBuildLogger(origin)
            ? new ModAttribution("", "Vintage Story native build logger", "High")
            : AttributeMod(origin.Length > 0 ? origin : line + "\n" + context);

        lock (sync)
        {
            if (!issues.TryGetValue(signature, out var issue))
            {
                if (issues.Count >= MaxIssueSignatures)
                {
                    string? evict = issues.Values
                        .OrderByDescending(x => SeverityRank(x.Impact))
                        .ThenBy(x => x.LastSeenUnixMilliseconds)
                        .Select(x => x.Signature)
                        .FirstOrDefault();
                    if (evict != null) { issues.Remove(evict); recentIssues.Remove(evict); }
                }

                issue = new LogIssueState
                {
                    Signature = signature,
                    Title = CleanTitle(line),
                    Origin = origin,
                    Category = category,
                    Impact = impact,
                    FirstSeenUnixMilliseconds = now,
                    LastSeenUnixMilliseconds = now,
                    LikelyModId = attribution.ModId,
                    LikelyModName = attribution.ModName,
                    AttributionConfidence = attribution.Confidence,
                    Diagnosis = BuildDiagnosis(category, attribution.ModName),
                    RecommendedFix = BuildRecommendedFix(category, attribution.ModName)
                };
                issues[signature] = issue;
            }

            long observedNow=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if(!archived&&now>=observationStarted&&now<=observedNow&&observedNow-now<300000)
            {
                if(!recentIssues.TryGetValue(signature,out var counter))recentIssues[signature]=counter=new RecentLogIssueCounts();
                counter.Observe(now);
            }
            issue.Occurrences++;
            issue.FirstSeenUnixMilliseconds = Math.Min(issue.FirstSeenUnixMilliseconds, now);
            issue.LastSeenUnixMilliseconds = Math.Max(issue.LastSeenUnixMilliseconds, now);
            issue.SeenInArchivedLogs |= archived;
            if (SeverityRank(impact) < SeverityRank(issue.Impact)) issue.Impact = impact;
            if (string.IsNullOrWhiteSpace(issue.LikelyModId) && !string.IsNullOrWhiteSpace(attribution.ModId))
            {
                issue.LikelyModId = attribution.ModId;
                issue.LikelyModName = attribution.ModName;
                issue.AttributionConfidence = attribution.Confidence;
            }

            issue.Samples.Add(new LogIssueSample
            {
                UnixMilliseconds = now,
                SourceFile = sourceFile,
                Archived = archived,
                Context = context.Length > 6000 ? context[..6000] : context
            });
            while (issue.Samples.Count > MaxSamplesPerIssue)
                issue.Samples.RemoveAt(0);
        }
    }

    private StartupPreflightSnapshot RunPreflight(
        string bindAddress,
        int port,
        bool webEnabled,
        bool dashboardAlreadyRunning)
    {
        var snapshot = new StartupPreflightSnapshot
        {
            GeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TargetGameVersion = WebDashboardService.TargetGameVersion
        };

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

        var loaded = api.ModLoader.Mods
            .Where(x => x?.Info != null)
            .Select(x => new ModManifestEntry
            {
                ModId = x.Info.ModID ?? "",
                Name = x.Info.Name ?? "",
                Version = x.Info.Version ?? "",
                FileName = Path.GetFileName(x.FileName ?? "")
            })
            .OrderBy(x => x.ModId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        snapshot.LoadedModCount = loaded.Count;
        string loadedGameVersion = loaded.FirstOrDefault(x => string.Equals(x.ModId, "game", StringComparison.OrdinalIgnoreCase))?.Version ?? "";
        snapshot.LoadedGameVersion = loadedGameVersion;

        var duplicateLoaded = loaded.GroupBy(x => x.ModId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        foreach (var group in duplicateLoaded)
        {
            Add("Critical", "Mods", $"Duplicate loaded mod ID: {group.Key}",
                string.Join(", ", group.Select(x => $"{x.FileName} ({x.Version})")),
                "Remove duplicate/older packages so exactly one copy of the mod ID is available.");
        }
        if (duplicateLoaded.Count == 0)
            Add("OK", "Mods", "Loaded mod IDs", "No duplicate loaded mod IDs were reported by the mod loader.");

        var packageManifests = ScanModPackages();
        var duplicatePackages = packageManifests
            .Where(x => !string.IsNullOrWhiteSpace(x.ModId))
            .GroupBy(x => x.ModId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToList();
        foreach (var group in duplicatePackages)
        {
            Add("High", "Mods", $"Multiple packages for {group.Key}",
                string.Join(" | ", group.Select(x => $"{Path.GetFileName(x.SourcePath)} v{x.Version}")),
                "Keep only the intended package version in VintagestoryData/Mods, then restart.");
        }
        if (duplicatePackages.Count == 0)
            Add("OK", "Mods", "Mod package duplicates", "No duplicate mod IDs were found across readable user mod packages.");

        if (!string.IsNullOrWhiteSpace(loadedGameVersion))
        {
            Add(
                string.Equals(loadedGameVersion, WebDashboardService.TargetGameVersion, StringComparison.OrdinalIgnoreCase) ? "OK" : "High",
                "Version",
                "Suite game target",
                $"Loaded game version is {loadedGameVersion}; this suite build targets {WebDashboardService.TargetGameVersion}.",
                string.Equals(loadedGameVersion, WebDashboardService.TargetGameVersion, StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : "Use a suite build compiled and tested for the loaded Vintage Story version.");
        }

        var loadedIds = loaded.Select(x => x.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in packageManifests.Where(x => loadedIds.Contains(x.ModId)))
        {
            foreach (var dep in pkg.Dependencies)
            {
                if (dep.Key.Equals("game", StringComparison.OrdinalIgnoreCase))
                {
                    if (LooksLikeMajorMinorMismatch(dep.Value, loadedGameVersion))
                        Add("High", "Dependency", $"{pkg.ModId} game-version requirement", $"Package declares game dependency '{dep.Value}' while the loaded game is {loadedGameVersion}.", "Verify that this mod version supports the current game version before relying on it.");
                    continue;
                }
                if (dep.Key.Equals("core", StringComparison.OrdinalIgnoreCase)) continue;
                if (!loadedIds.Contains(dep.Key))
                    Add("Critical", "Dependency", $"Missing dependency for {pkg.ModId}", $"{pkg.ModId} declares dependency '{dep.Key}' ({dep.Value}), but that mod ID is not loaded.", $"Install/enable {dep.Key} or use a compatible build of {pkg.ModId}.");
            }
        }

        AddCommandOwnershipCheck(Add, "restart", "Server restart command");
        AddCommandOwnershipCheck(Add, "shutdown", "Server shutdown command");
        AddCommandOwnershipCheck(Add, "back", "Teleport return command");
        AddCommandOwnershipCheck(Add, "report", "Player report command");

        ScanConfigHealth(Add);

        if (webEnabled && !dashboardAlreadyRunning)
        {
            if (CanBind(bindAddress, port, out string bindError))
                Add("OK", "Network", "Web dashboard port", $"{bindAddress}:{port} was available during startup preflight.");
            else
                Add("Critical", "Network", "Web dashboard port conflict", $"Cannot bind {bindAddress}:{port}: {bindError}", "Stop the process using the port or change NewfiesWebServer.json before restart.");
        }
        else if (webEnabled)
        {
            Add("OK", "Network", "Web dashboard port", "Dashboard is already running; self-port collision test was skipped on this re-check.");
        }
        else
        {
            Add("Info", "Network", "Web dashboard disabled", "Web dashboard port validation is not required while the listener is disabled.");
        }

        snapshot.ModChanges = CompareAndSaveManifest(loaded);
        if (snapshot.ModChanges.Added.Count == 0 && snapshot.ModChanges.Removed.Count == 0 && snapshot.ModChanges.Updated.Count == 0)
            Add("OK", "Regression", "Loaded-mod manifest", snapshot.ModChanges.HasPreviousManifest ? "No loaded-mod changes since the previous recorded startup." : "Baseline loaded-mod manifest recorded for future startup comparisons.");
        else
            Add("Watch", "Regression", "Loaded-mod manifest changed",
                $"Added {snapshot.ModChanges.Added.Count}, removed {snapshot.ModChanges.Removed.Count}, updated {snapshot.ModChanges.Updated.Count} since the previous recorded startup.",
                "If new errors or lag appeared after this boot, start investigation with the changed mods listed here.");

        Add("Info", "Harmony", "Harmony overlap inspection", "Deep patch-owner overlap inspection remains manual in Compatibility Center so startup preflight stays cheap.", "Run the Compatibility Center deep scan only when investigating an actual compatibility symptom.");

        snapshot.Critical = snapshot.Checks.Count(x => x.Severity == "Critical");
        snapshot.High = snapshot.Checks.Count(x => x.Severity == "High");
        snapshot.Watch = snapshot.Checks.Count(x => x.Severity == "Watch");
        snapshot.Status = snapshot.Critical > 0 ? "Critical" : snapshot.High > 0 ? "Investigate" : snapshot.Watch > 0 ? "Watch" : "Ready";
        return snapshot;
    }

    private void AddCommandOwnershipCheck(Action<string, string, string, string, string> add, string commandName, string title)
    {
        try
        {
            var command = api.ChatCommands.Get(commandName);
            if (command == null)
            {
                add("Info", "Commands", title, $"/{commandName} is not currently registered.", "No action required unless a provider is expected to own this command.");
                return;
            }

            string owner = command.GetType().Assembly.GetName().Name ?? command.GetType().FullName ?? "unknown";
            string suiteOwner = owner.Contains("NewfiesServerEssentialsSuite", StringComparison.OrdinalIgnoreCase) ? "suite" : "external/provider";
            add("OK", "Commands", title, $"/{commandName} is registered by {owner} ({suiteOwner}).", "Provider detection should preserve the current owner; use /nse namespaced fallbacks where applicable.");
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
            add("Info", "Config", "Suite config folder", $"ModConfig/{SuiteConfigStorage.FolderName}/ is not present or not readable yet.", "No action required if this is an early/fresh startup.");
            return;
        }

        string[] suiteFiles = Directory
            .EnumerateFiles(configRoot, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int checkedCount = 0;
        foreach (string name in suiteFiles)
        {
            string path = Path.Combine(configRoot, name);
            checkedCount++;
            try
            {
                using var _ = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                add("High", "Config", $"Invalid configuration: {name}", $"JSON parse failed: {ex.Message}", "Fix or restore the configuration from a known-good snapshot, then restart/reload the affected subsystem.");
            }
        }
        if (checkedCount > 0)
            add("OK", "Config", "Suite configuration syntax", $"Checked {checkedCount} JSON files in ModConfig/{SuiteConfigStorage.FolderName}/ for basic readability.", "");

        var legacyPairs = new[]
        {
            (Legacy: "blocklogger-v2.json", Current: "NewfiesBlocklogger.json"),
            (Legacy: "loginprotection.json", Current: "NewfiesLoginProtection.json"),
            (Legacy: "animal_limiter.json", Current: "NewfiesAnimalLimiter.json")
        };
        foreach (var pair in legacyPairs)
        {
            string legacy = Path.Combine(configRoot, pair.Legacy);
            string current = Path.Combine(configRoot, pair.Current);
            if (File.Exists(legacy) && File.Exists(current))
                add("Watch", "Config", $"Legacy config still present: {pair.Legacy}", $"Both {pair.Legacy} and {pair.Current} exist. The current Newfies-prefixed file is authoritative after migration.", "Archive/remove the legacy file once you have verified the migrated current config.");
        }
    }

    private List<ModPackageManifest> ScanModPackages()
    {
        var result = new List<ModPackageManifest>();
        if (!Directory.Exists(modsRoot)) return result;

        foreach (string zipPath in Directory.EnumerateFiles(modsRoot, "*.zip", SearchOption.TopDirectoryOnly).Take(500))
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.Entries.FirstOrDefault(x => string.Equals(x.FullName.TrimStart('/'), "modinfo.json", StringComparison.OrdinalIgnoreCase));
                if (entry == null) continue;
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
                string json = reader.ReadToEnd();
                var manifest = ParsePackageManifest(json, zipPath);
                if (manifest != null) result.Add(manifest);
            }
            catch { }
        }

        foreach (string dir in Directory.EnumerateDirectories(modsRoot, "*", SearchOption.TopDirectoryOnly).Take(500))
        {
            string path = Path.Combine(dir, "modinfo.json");
            if (!File.Exists(path)) continue;
            try
            {
                var manifest = ParsePackageManifest(File.ReadAllText(path), dir);
                if (manifest != null) result.Add(manifest);
            }
            catch { }
        }

        return result;
    }

    private static ModPackageManifest? ParsePackageManifest(string json, string sourcePath)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        var root = doc.RootElement;
        string modId = root.TryGetProperty("modid", out var id) ? id.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(modId)) return null;
        string version = root.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : "";
        var manifest = new ModPackageManifest { ModId = modId, Version = version, SourcePath = sourcePath };
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
        {
            foreach (var dep in deps.EnumerateObject())
                manifest.Dependencies[dep.Name] = dep.Value.ValueKind == JsonValueKind.String ? dep.Value.GetString() ?? "" : dep.Value.ToString();
        }
        return manifest;
    }

    private static bool LooksLikeMajorMinorMismatch(string requirement, string loadedVersion)
    {
        if (string.IsNullOrWhiteSpace(requirement) || string.IsNullOrWhiteSpace(loadedVersion)) return false;
        var req = Regex.Match(requirement, @"(?<!\d)(\d+)\.(\d+)");
        var loaded = Regex.Match(loadedVersion, @"(?<!\d)(\d+)\.(\d+)");
        if (!req.Success || !loaded.Success) return false;
        return req.Groups[1].Value != loaded.Groups[1].Value || req.Groups[2].Value != loaded.Groups[2].Value;
    }

    private ModChangeSummary CompareAndSaveManifest(List<ModManifestEntry> current)
    {
        var summary = new ModChangeSummary();
        List<ModManifestEntry> previous = new();
        try
        {
            if (File.Exists(manifestPath))
            {
                previous = JsonSerializer.Deserialize<List<ModManifestEntry>>(File.ReadAllText(manifestPath), JsonOptions) ?? new();
                summary.HasPreviousManifest = true;
            }
        }
        catch { }

        var prev = previous
            .Where(x => !string.IsNullOrWhiteSpace(x.ModId))
            .GroupBy(x => x.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var cur = current
            .Where(x => !string.IsNullOrWhiteSpace(x.ModId))
            .GroupBy(x => x.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in current)
        {
            if (!prev.TryGetValue(entry.ModId, out var old))
                summary.Added.Add($"{entry.ModId} {entry.Version}".Trim());
            else if (!string.Equals(old.Version, entry.Version, StringComparison.OrdinalIgnoreCase) || !string.Equals(old.FileName, entry.FileName, StringComparison.OrdinalIgnoreCase))
                summary.Updated.Add($"{entry.ModId}: {old.Version} -> {entry.Version}");
        }
        foreach (var entry in previous)
            if (!cur.ContainsKey(entry.ModId)) summary.Removed.Add($"{entry.ModId} {entry.Version}".Trim());

        try { File.WriteAllText(manifestPath, JsonSerializer.Serialize(current, JsonOptions)); } catch { }
        return summary;
    }

    private void BuildModAliases()
    {
        var aliases = new List<ModAlias>();
        foreach (var mod in api.ModLoader.Mods.Where(x => x?.Info != null))
        {
            string id = mod.Info.ModID ?? "";
            string name = mod.Info.Name ?? id;
            string file = Path.GetFileNameWithoutExtension(mod.FileName ?? "");
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (id.Equals("game", StringComparison.OrdinalIgnoreCase) ||
                id.Equals("core", StringComparison.OrdinalIgnoreCase) ||
                id.Equals("creative", StringComparison.OrdinalIgnoreCase) ||
                id.Equals("survival", StringComparison.OrdinalIgnoreCase))
                continue;
            aliases.Add(new ModAlias
            {
                ModId = id,
                ModName = name,
                Aliases = new[] { id, name, file }
                    .Where(x => !string.IsNullOrWhiteSpace(x) && x.Length >= 4)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Length)
                    .ToArray()
            });
        }

        lock (sync)
        {
            Volatile.Write(ref modAliases, aliases.OrderByDescending(x => x.Aliases.Length == 0 ? 0 : x.Aliases.Max(a => a.Length)).ToArray());
        }
    }

    private ModAttribution AttributeMod(string text)
    {
        if (text.StartsWith("BlockLogger.", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("WebDashboard", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ServerEssentials", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("NewfiesServerEssentialsSuite", StringComparison.OrdinalIgnoreCase))
            return new ModAttribution("newfiesserveressentialssuite", "Newfies Server Essentials Suite", "High");

        var aliases = Volatile.Read(ref modAliases);
        foreach (var mod in aliases)
        {
            foreach (string alias in mod.Aliases)
            {
                if (text.IndexOf(alias, StringComparison.OrdinalIgnoreCase) >= 0)
                    return new ModAttribution(mod.ModId, mod.ModName, alias.Equals(mod.ModId, StringComparison.OrdinalIgnoreCase) ? "High" : "Medium");
            }
        }
        return new ModAttribution("", "", "Low");
    }

    private LogIssueView ToView(LogIssueState state, ServerHealthSnapshot? health)
    {
        string correlation = "No profiler/health correlation is currently available.";
        if (health != null)
        {
            var pieces = new List<string>();
            if (health.Current.MainThreadDelayMs >= 50)
                pieces.Add($"current main-thread delay {health.Current.MainThreadDelayMs:0} ms");
            if (health.Current.BlockLoggerQueue >= 1000)
                pieces.Add($"BlockLogger queue {health.Current.BlockLoggerQueue:N0}");
            if (!string.IsNullOrWhiteSpace(state.LikelyModId))
            {
                var mod = health.ModHotspots.FirstOrDefault(x => string.Equals(x.ModId, state.LikelyModId, StringComparison.OrdinalIgnoreCase));
                if (mod != null && mod.TotalMs > 0)
                    pieces.Add($"profiler attributed {mod.PercentOfProfileTickTime:0.0}% of profile tick time to {mod.ModName} ({mod.Confidence} confidence)");
            }
            if (pieces.Count > 0) correlation = string.Join("; ", pieces) + ".";
        }

        long observedNow=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int recent=recentIssues.TryGetValue(state.Signature,out var counter)?counter.Count(observedNow):0;
        double seconds=Math.Min(300,Math.Max(0,(observedNow-observationStarted)/1000d));
        bool legacy=!state.Signature.Contains("|origin:",StringComparison.Ordinal)&&state.Samples.Exists(x=>LogIssueEvidence.Origin(x.Context).Length>0);
        return new LogIssueView
        {
            Signature = state.Signature,
            Title = state.Title,
            Category = state.Category,
            Impact = state.Impact,
            Occurrences = state.Occurrences,
            Origin = state.Origin,
            RecentOccurrences = recent,
            RecentRatePerMinute = seconds>=60?Math.Round(recent*60/seconds,2):null,
            RecentObservedSeconds = seconds,
            LegacyAggregate = legacy,
            FirstSeenUnixMilliseconds = state.FirstSeenUnixMilliseconds,
            LastSeenUnixMilliseconds = state.LastSeenUnixMilliseconds,
            LikelyModId = legacy ? "" : state.LikelyModId,
            LikelyModName = legacy ? "Legacy aggregate — source not reassigned" : state.LikelyModName,
            AttributionConfidence = legacy ? "Low" : state.AttributionConfidence,
            Diagnosis = legacy ? "Older message-only signatures may contain multiple sources. New observations are separated by originating stack frame; historical totals cannot be reassigned to one mod." : state.Diagnosis,
            RecommendedFix = state.RecommendedFix,
            HealthCorrelation = correlation,
            ActivityState = BuildActivityState(state.LastSeenUnixMilliseconds),
            SeenInArchivedLogs = state.SeenInArchivedLogs,
            Samples = state.Samples.ToList()
        };
    }

    private string ResolveCurrentLogPath()
    {
        string preferred = Path.Combine(logsRoot, "server-main.log");
        if (File.Exists(preferred)) return preferred;
        if (!Directory.Exists(logsRoot)) return preferred;
        return Directory.EnumerateFiles(logsRoot, "server-main*.log", SearchOption.TopDirectoryOnly)
            .Select(x => new FileInfo(x))
            .OrderByDescending(x => x.LastWriteTimeUtc)
            .Select(x => x.FullName)
            .FirstOrDefault() ?? preferred;
    }

    private string ResolveDataDirectory(string name)
    {
        try { return api.GetOrCreateDataPath(name); }
        catch
        {
            string modData = api.GetOrCreateDataPath("ModData");
            string root = Directory.GetParent(modData)?.FullName ?? modData;
            string path = Path.Combine(root, name);
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private static bool CanBind(string bindAddress, int port, out string error)
    {
        error = "";
        TcpListener? listener = null;
        try
        {
            IPAddress address;
            string value = (bindAddress ?? "127.0.0.1").Trim();
            if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase)) address = IPAddress.Loopback;
            else if (value == "*" || value == "0.0.0.0") address = IPAddress.Any;
            else if (value == "::" || value == "[::]") address = IPAddress.IPv6Any;
            else if (!IPAddress.TryParse(value, out var parsedAddress))
            {
                error = "bind address is not a numeric IP/localhost; live listener will resolve it normally";
                return true;
            }
            else address = parsedAddress;
            listener = new TcpListener(address, port);
            listener.Start(1);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { listener?.Stop(); } catch { }
        }
    }

    private static bool LooksActionable(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length < 8) return false;
        return line.Contains("[Error]", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("[Fatal]", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("[Warning]", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("[Warn]", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
               line.Contains(" failed", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("failure", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("already registered", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyCategory(string line, string context)
    {
        string text = line + " " + context;
        if (ContainsAny(text, "failed to load mod", "could not load mod", "error loading mod", "mod startup", "startserverside", "startpre")) return "Mod startup failures";
        if (ContainsAny(text, "missing dependency", "dependency missing", "requires mod", "dependency")) return "Dependency problems";
        if (ContainsAny(text, "already registered", "duplicate item", "duplicate block", "duplicate registration", "same code", "command") && ContainsAny(text, "duplicate", "already", "collision", "registered")) return "Duplicate registration / command collisions";
        if (ContainsAny(text, "recipe") && ContainsAny(text, "failed", "error", "warning", "invalid", "could not")) return "Recipe / patch failures";
        if (ContainsAny(text, "json patch", "patch") && ContainsAny(text, "failed", "error", "warning", "could not", "invalid")) return "Recipe / patch failures";
        if (ContainsAny(text, "missing attribute", "attribute missing", "no attribute", "attribute not found")) return "Missing entity attributes";
        if (ContainsAny(text, "socket", "tcp", "network", "connection failed", "connection reset", "httplistener", "webdashboard", "broken pipe")) return "Network / web failures";
        if (ContainsAny(text, "config", ".json") && ContainsAny(text, "invalid", "failed", "exception", "error", "malformed")) return "Configuration failures";
        if (ContainsAny(text, "harmony", "patching exception", "patch owner")) return "Harmony / patch overlap";
        if (ContainsAny(text, "exception", "stack trace", "nullreference", "argumentexception", "invalidoperationexception", "indexoutofrange")) return "Runtime exceptions";
        return "Informational warnings";
    }

    private static string ClassifyImpact(string line, string category)
    {
        bool fatal = line.Contains("[Fatal]", StringComparison.OrdinalIgnoreCase) || line.Contains("fatal", StringComparison.OrdinalIgnoreCase);
        bool error = line.Contains("[Error]", StringComparison.OrdinalIgnoreCase) || line.Contains("exception", StringComparison.OrdinalIgnoreCase) || line.Contains(" failed", StringComparison.OrdinalIgnoreCase);
        if (fatal) return "Critical";
        if (category is "Mod startup failures" or "Dependency problems") return error ? "Critical" : "High";
        if (category is "Runtime exceptions" or "Network / web failures" or "Configuration failures") return error ? "High" : "Moderate";
        if (category is "Duplicate registration / command collisions") return error ? "High" : "Moderate";
        if (category is "Recipe / patch failures" or "Missing entity attributes" or "Harmony / patch overlap") return error ? "Moderate" : "Low";
        return error ? "Moderate" : "Low";
    }

    private static string NormalizeSignature(string line, string category)
    {
        string value = line.Trim();
        int bracket = value.IndexOf(']');
        // Strip a timestamp-shaped prefix but preserve the actual log-level/message text.
        if (TimestampPrefix.IsMatch(value) && bracket >= 0 && bracket < 40)
            value = value[(bracket + 1)..].Trim();
        value = GuidRegex.Replace(value, "<guid>");
        value = HexRegex.Replace(value, "<hex>");
        value = CoordinateRegex.Replace(value, "<xyz>");
        value = WindowsPathRegex.Replace(value, "<path>");
        value = UnixPathRegex.Replace(value, "<path>");
        value = NumberRegex.Replace(value, "<n>");
        value = Regex.Replace(value, @"\s+", " ").Trim();
        if (value.Length > 420) value = value[..420];
        return category + "|" + value.ToLowerInvariant();
    }

    private static long? ParseLogTimestampUnixMilliseconds(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !char.IsDigit(line.TrimStart()[0])) return null;
        string value = line.TrimStart();
        int marker = value.IndexOf('[');
        string prefix = marker > 0 && marker <= 40 ? value[..marker].Trim() : value[..Math.Min(32, value.Length)].Trim();
        string[] formats =
        {
            "d.M.yyyy H:mm:ss", "dd.MM.yyyy HH:mm:ss", "d.M.yyyy HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "M/d/yyyy h:mm:ss tt",
            "M/d/yyyy H:mm:ss", "d/M/yyyy H:mm:ss"
        };
        if (DateTime.TryParseExact(prefix, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var exact))
            return new DateTimeOffset(exact).ToUniversalTime().ToUnixTimeMilliseconds();
        if (DateTime.TryParse(prefix, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed))
            return new DateTimeOffset(parsed).ToUniversalTime().ToUnixTimeMilliseconds();
        return null;
    }

    private static string CleanTitle(string line)
    {
        string value = Regex.Replace(line.Trim(), @"\s+", " ");
        if (value.Length > 240) value = value[..240] + "…";
        return value;
    }

    private static string BuildDiagnosis(string category, string modName) => category switch
    {
        "Mod startup failures" => $"A mod failed during initialization{ModSuffix(modName)}. This can leave commands, hooks or content only partially registered.",
        "Dependency problems" => $"A required mod/version relationship appears unsatisfied{ModSuffix(modName)}.",
        "Duplicate registration / command collisions" => $"Two providers appear to be registering the same command/content key{ModSuffix(modName)}.",
        "Recipe / patch failures" => $"A recipe or JSON/Harmony patch operation failed{ModSuffix(modName)}. Gameplay content may be missing or partially applied.",
        "Missing entity attributes" => $"Code expected an entity attribute that was absent{ModSuffix(modName)}. Repetition can indicate a compatibility or bad-state problem.",
        "Network / web failures" => $"A network/listener/connection path reported failure{ModSuffix(modName)}.",
        "Configuration failures" => $"Configuration data could not be read or validated cleanly{ModSuffix(modName)}.",
        "Harmony / patch overlap" => $"Harmony patching reported a warning/failure{ModSuffix(modName)}. Overlap is evidence to inspect, not automatic proof of incompatibility.",
        "Runtime exceptions" => $"Runtime code threw an exception{ModSuffix(modName)}. Repeated occurrences are more significant than a one-off handled exception.",
        _ => $"The server emitted a warning{ModSuffix(modName)}. Review frequency and surrounding context before treating it as a fault."
    };

    private static string BuildRecommendedFix(string category, string modName) => category switch
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
        _ => "Monitor recurrence. Escalate only if frequency rises, gameplay breaks, or health/profiler evidence correlates with the warning."
    };

    private static string BuildActivityState(long lastSeenUnixMilliseconds)
    {
        if (lastSeenUnixMilliseconds <= 0) return "Unknown";
        long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastSeenUnixMilliseconds;
        if (age <= 15 * 60_000L) return "Active";
        if (age <= 24 * 60 * 60_000L) return "Recent";
        return "Historical";
    }

    private static string ModSuffix(string modName) => string.IsNullOrWhiteSpace(modName) ? "" : $"; likely owner: {modName}";

    private static bool ContainsAny(string text, params string[] values) => values.Any(x => text.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);

    private static int SeverityRank(string severity) => severity switch
    {
        "Critical" => 0,
        "High" => 1,
        "Moderate" => 2,
        "Watch" => 2,
        "Low" => 3,
        "Info" => 4,
        "OK" => 5,
        _ => 6
    };


    private void SavePreflight(StartupPreflightSnapshot snapshot, bool archiveHistory)
    {
        try
        {
            string json = JsonSerializer.Serialize(snapshot, JsonOptions);
            string tmp = preflightPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, preflightPath, true);

            if (archiveHistory)
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string historyPath = Path.Combine(preflightHistoryRoot, $"startup-preflight-{stamp}.json");
                File.WriteAllText(historyPath, json);
                foreach (var old in Directory.EnumerateFiles(preflightHistoryRoot, "startup-preflight-*.json")
                             .Select(x => new FileInfo(x))
                             .OrderByDescending(x => x.LastWriteTimeUtc)
                             .Skip(20))
                {
                    try { old.Delete(); } catch { }
                }
            }
        }
        catch { }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(statePath)) return;
            var state = JsonSerializer.Deserialize<LogIntelligencePersistedState>(File.ReadAllText(statePath), JsonOptions);
            if (state == null) return;
            lock (sync)
            {
                issues.Clear();
            recentIssues.Clear();
                foreach (var issue in state.Issues.Take(MaxIssueSignatures))
                    if (!string.IsNullOrWhiteSpace(issue.Signature)) issues[issue.Signature] = issue;
                lastScanUnixMilliseconds = state.LastScanUnixMilliseconds;
                lastArchiveScanUnixMilliseconds = state.LastArchiveScanUnixMilliseconds;
                archivedScanFingerprints.Clear();
                foreach (var fingerprint in state.ArchivedScanFingerprints.Take(MaxArchivedFingerprints))
                    archivedScanFingerprints.Add(fingerprint);
                fileOffsets.Clear();
                foreach (var offset in state.FileOffsets.Take(20))
                    if (!string.IsNullOrWhiteSpace(offset.Key) && offset.Value >= 0) fileOffsets[offset.Key] = offset.Value;
            }
        }
        catch { }
    }

    private void SaveState()
    {
        try
        {
            LogIntelligencePersistedState state;
            lock (sync)
            {
                state = new LogIntelligencePersistedState
                {
                    LastScanUnixMilliseconds = lastScanUnixMilliseconds,
                    LastArchiveScanUnixMilliseconds = lastArchiveScanUnixMilliseconds,
                    Issues = issues.Values
                        .OrderBy(x => SeverityRank(x.Impact))
                        .ThenByDescending(x => x.LastSeenUnixMilliseconds)
                        .Take(MaxIssueSignatures)
                        .ToList(),
                    ArchivedScanFingerprints = archivedScanFingerprints.TakeLast(MaxArchivedFingerprints).ToList(),
                    FileOffsets = fileOffsets
                        .OrderByDescending(x => string.Equals(x.Key, currentLogPath, StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(x => x.Value)
                        .Take(20)
                        .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase)
                };
            }
            string tmp = statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tmp, statePath, true);
            badBlockEntityTracker.SaveState();
        }
        catch { }
    }

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
        public Dictionary<string, string> Dependencies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class LogIntelligenceSnapshot
{
    public long GeneratedUnixMilliseconds { get; set; }
    public long LastScanUnixMilliseconds { get; set; }
    public long LastArchiveScanUnixMilliseconds { get; set; }
    public string CurrentLogPath { get; set; } = "";
    public long LiveLogBacklogBytes { get; set; }
    public long OversizedLogLines { get; set; }
    public int Score { get; set; }
    public string State { get; set; } = "Healthy";
    public int Critical { get; set; }
    public int High { get; set; }
    public int Moderate { get; set; }
    public int Low { get; set; }
    public int TotalOccurrences { get; set; }
    public int UniqueIssues { get; set; }
    public bool ScanInProgress { get; set; }
    public List<LogCategorySummary> Categories { get; set; } = new();
    public List<LogIssueView> Issues { get; set; } = new();
    public StartupPreflightSnapshot Preflight { get; set; } = new();
    public BadBlockEntitySnapshot BadBlockEntities { get; set; } = new();
}

public sealed class LogCategorySummary
{
    public string Category { get; set; } = "";
    public int Occurrences { get; set; }
    public int UniqueIssues { get; set; }
    public string Impact { get; set; } = "Low";
}

public sealed class LogIssueView
{
    public string Origin { get; set; } = "";
    public int RecentOccurrences { get; set; }
    public double? RecentRatePerMinute { get; set; }
    public double RecentObservedSeconds { get; set; }
    public bool LegacyAggregate { get; set; }
    public string Signature { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public string Impact { get; set; } = "Low";
    public int Occurrences { get; set; }
    public long FirstSeenUnixMilliseconds { get; set; }
    public long LastSeenUnixMilliseconds { get; set; }
    public string LikelyModId { get; set; } = "";
    public string LikelyModName { get; set; } = "";
    public string AttributionConfidence { get; set; } = "Low";
    public string Diagnosis { get; set; } = "";
    public string RecommendedFix { get; set; } = "";
    public string HealthCorrelation { get; set; } = "";
    public string ActivityState { get; set; } = "Unknown";
    public bool SeenInArchivedLogs { get; set; }
    public List<LogIssueSample> Samples { get; set; } = new();
}

public sealed class LogIssueState
{
    public string Origin { get; set; } = "";
    public string Signature { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public string Impact { get; set; } = "Low";
    public int Occurrences { get; set; }
    public long FirstSeenUnixMilliseconds { get; set; }
    public long LastSeenUnixMilliseconds { get; set; }
    public string LikelyModId { get; set; } = "";
    public string LikelyModName { get; set; } = "";
    public string AttributionConfidence { get; set; } = "Low";
    public string Diagnosis { get; set; } = "";
    public string RecommendedFix { get; set; } = "";
    public bool SeenInArchivedLogs { get; set; }
    public List<LogIssueSample> Samples { get; set; } = new();
}

public sealed class LogIssueSample
{
    public long UnixMilliseconds { get; set; }
    public string SourceFile { get; set; } = "";
    public bool Archived { get; set; }
    public string Context { get; set; } = "";
}

public sealed class StartupPreflightSnapshot
{
    public long GeneratedUnixMilliseconds { get; set; }
    public string Status { get; set; } = "Ready";
    public string TargetGameVersion { get; set; } = "";
    public string LoadedGameVersion { get; set; } = "";
    public int LoadedModCount { get; set; }
    public int Critical { get; set; }
    public int High { get; set; }
    public int Watch { get; set; }
    public List<PreflightCheck> Checks { get; set; } = new();
    public ModChangeSummary ModChanges { get; set; } = new();
}

public sealed class PreflightCheck
{
    public string Severity { get; set; } = "Info";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string RecommendedAction { get; set; } = "";
}

public sealed class ModChangeSummary
{
    public bool HasPreviousManifest { get; set; }
    public List<string> Added { get; set; } = new();
    public List<string> Removed { get; set; } = new();
    public List<string> Updated { get; set; } = new();
}

public sealed class ModManifestEntry
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
}

public sealed class LogIntelligencePersistedState
{
    public long LastScanUnixMilliseconds { get; set; }
    public long LastArchiveScanUnixMilliseconds { get; set; }
    public List<LogIssueState> Issues { get; set; } = new();
    public List<string> ArchivedScanFingerprints { get; set; } = new();
    public Dictionary<string, long> FileOffsets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
