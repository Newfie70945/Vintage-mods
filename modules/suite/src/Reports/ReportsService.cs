using Newfies.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BlockLogger.Admin;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BlockLogger.Reports;

public sealed class ReportsService : IDisposable
{
    private const int SchemaVersion = 1;
    private const int MaxReports = 5000;
    private const int MaxReasonLength = 700;
    private const int MaxNoteLength = 1200;
    private const int MaxReporterCooldownEntries = 4096;
    private const int MaxNotesPerReport = 1000;
    private const int MaxEventsPerReport = 2000;
    private readonly ICoreServerAPI api;
    private readonly object gate = new();
    private readonly Dictionary<string, long> lastReportByReporter = new(StringComparer.OrdinalIgnoreCase);
    private readonly string statePath;
    private readonly DeferredJsonFileWriter writer;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private PlayerReportState state = new();
    private bool disposed;
    private bool dirty;
    private bool storageReady;
    public string StorageError { get; private set; } = "";
    public string RecoveryFile { get; private set; } = "";
    private long saveListenerId = -1;

    public string RootCommandOwner { get; private set; } = "Not registered";
    public bool RootCommandRegisteredBySuite { get; private set; }

    public ReportsService(ICoreServerAPI api)
    {
        this.api = api;
        string root = Path.Combine(SuiteDataPaths.GetWorldRoot(api), "reports");
        Directory.CreateDirectory(root);
        statePath = Path.Combine(root, "player-reports.json");
        writer = new DeferredJsonFileWriter(statePath, error => api.Logger.Warning("[ServerEssentials/Reports] Persistence failed; snapshot retained for retry: {0}", error));
    }

    public void Start()
    {
        Load();
        api.Event.GameWorldSave += OnWorldSave;
        saveListenerId = api.Event.RegisterGameTickListener(_ => SaveIfDirty(), 60_000);
        api.Logger.Notification("[ServerEssentials/Reports] Reports service started. Reports={0}", state.Reports.Count);
    }

    public void SetRootCommandOwnership(string owner, bool suiteOwns)
    {
        RootCommandOwner = string.IsNullOrWhiteSpace(owner) ? "Unknown" : owner;
        RootCommandRegisteredBySuite = suiteOwns;
    }

    public (bool Success, string Message, PlayerReportRecord? Report) CreateReport(
        IServerPlayer reporter,
        string issue)
    {
        string cleanIssue = NormalizeText(issue, MaxReasonLength);
        if (string.IsNullOrWhiteSpace(cleanIssue))
            return (false, "Usage: /report <issue>  (example: /report Spawn bridge is broken)", null);

        PlayerReportRecord row;
        lock (gate)
        {
            if (!storageReady) return (false, "Cannot create a report: stored reports are unavailable. Ask an administrator to check the server log.", null);
            long now = NowMs();
            string reporterKey = string.IsNullOrWhiteSpace(reporter.PlayerUID) ? reporter.PlayerName : reporter.PlayerUID;
            if (!string.IsNullOrWhiteSpace(reporterKey) && lastReportByReporter.TryGetValue(reporterKey, out long previous) && now - previous < 30_000)
            {
                long remaining = Math.Max(1, (30_000 - (now - previous) + 999) / 1000);
                return (false, $"Please wait {remaining}s before submitting another report.", null);
            }
            if (state.Reports.Count >= MaxReports)
            {
                var oldestResolved = state.Reports
                    .Where(x => string.Equals(x.Status, "Resolved", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.UpdatedUnixMilliseconds)
                    .FirstOrDefault();
                if (oldestResolved == null)
                    return (false, "The report queue is at its safety limit. Please contact staff directly.", null);
                state.Reports.Remove(oldestResolved);
            }
            if (!string.IsNullOrWhiteSpace(reporterKey))
            {
                if (lastReportByReporter.Count >= MaxReporterCooldownEntries && !lastReportByReporter.ContainsKey(reporterKey))
                {
                    long cutoff = now - 5 * 60_000L;
                    foreach (var stale in lastReportByReporter.Where(x => x.Value < cutoff).Select(x => x.Key).Take(1024).ToList())
                        lastReportByReporter.Remove(stale);
                    if (lastReportByReporter.Count >= MaxReporterCooldownEntries)
                        lastReportByReporter.Clear();
                }
                lastReportByReporter[reporterKey] = now;
            }
            row = new PlayerReportRecord
            {
                Id = Math.Max(1, state.NextId++),
                CreatedUnixMilliseconds = now,
                UpdatedUnixMilliseconds = now,
                Status = "Open",
                ReporterUid = reporter.PlayerUID ?? "",
                ReporterName = reporter.PlayerName ?? "Unknown",
                TargetUid = "",
                TargetName = "General issue",
                Reason = cleanIssue
            };
            row.Events.Add(new PlayerReportEvent
            {
                UnixMilliseconds = now,
                Actor = row.ReporterName,
                Action = "Created",
                Detail = $"Issue reported: {row.Reason}"
            });
            state.Reports.Add(row);
            dirty = true;
        }

        NotifyStaff(row);
        return (true, $"Report #{row.Id} submitted. Staff have been notified.", Clone(row, summary: true));
    }

    public PlayerReportSnapshot GetSnapshot(string status = "all", int limit = 500)
    {
        lock (gate)
        {
            string normalized = NormalizeStatusFilter(status);
            var filtered = state.Reports
                .Where(x => normalized == "All" || string.Equals(x.Status, normalized, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.UpdatedUnixMilliseconds)
                .Take(Math.Clamp(limit, 1, 1000))
                .Select(x => Clone(x, summary: true)!)
                .ToList();
            return new PlayerReportSnapshot
            {
                GeneratedUnixMilliseconds = NowMs(),
                OpenCount = state.Reports.Count(x => x.Status == "Open"),
                InvestigatingCount = state.Reports.Count(x => x.Status == "Investigating"),
                ResolvedCount = state.Reports.Count(x => x.Status == "Resolved"),
                TotalCount = state.Reports.Count,
                RootCommandOwner = RootCommandOwner,
                RootCommandRegisteredBySuite = RootCommandRegisteredBySuite,
                Reports = filtered,
                StorageReady = storageReady,
                StorageError = writer.LastError.Length > 0 ? writer.LastError : StorageError
            };
        }
    }

    public PlayerReportRecord? GetReport(long id, int offset = 0, int limit = 100)
    {
        lock (gate) return Clone(state.Reports.FirstOrDefault(x => x.Id == id), offset: Math.Max(0, offset), limit: Math.Clamp(limit, 1, 100));
    }

    public string SetStatus(long id, string status, string actor, string note = "")
    {
        string normalized = NormalizeStatus(status);
        if (normalized.Length == 0) return "Status must be open, investigating, or resolved.";
        lock (gate)
        {
            if (!storageReady) return "Cannot update reports while storage is unavailable.";
            var row = state.Reports.FirstOrDefault(x => x.Id == id);
            if (row == null) return $"Report #{id} was not found.";
            if (row.Status == normalized && string.IsNullOrWhiteSpace(note)) return $"Report #{id} is already {normalized}.";
            if (row.Events.Count + (string.IsNullOrWhiteSpace(note) ? 1 : 2) > MaxEventsPerReport || (!string.IsNullOrWhiteSpace(note) && row.Notes.Count >= MaxNotesPerReport)) return "Cannot extend this report's history: its safety limit is reached. Existing notes are retained; create a follow-up report.";
            string old = row.Status;
            row.Status = normalized;
            row.UpdatedUnixMilliseconds = NowMs();
            row.Events.Add(new PlayerReportEvent
            {
                UnixMilliseconds = row.UpdatedUnixMilliseconds,
                Actor = CleanActor(actor),
                Action = "Status",
                Detail = $"{old} -> {normalized}"
            });
            if (!string.IsNullOrWhiteSpace(note)) AddNoteUnsafe(row, actor, note);
            dirty = true;
            return $"Report #{id} is now {normalized}.";
        }
    }

    public string Assign(long id, string staff, string actor)
    {
        lock (gate)
        {
            if (!storageReady) return "Cannot update reports while storage is unavailable.";
            var row = state.Reports.FirstOrDefault(x => x.Id == id);
            if (row == null) return $"Report #{id} was not found.";
            string clean = NormalizeText(staff, 80);
            string old = row.AssignedStaff;
            if (string.Equals(old, clean, StringComparison.Ordinal)) return $"Report #{id} assignment is unchanged.";
            if (row.Events.Count >= MaxEventsPerReport) return "Cannot extend this report's history: its safety limit is reached. Create a follow-up report.";
            row.AssignedStaff = clean;
            row.UpdatedUnixMilliseconds = NowMs();
            row.Events.Add(new PlayerReportEvent
            {
                UnixMilliseconds = row.UpdatedUnixMilliseconds,
                Actor = CleanActor(actor),
                Action = "Assignment",
                Detail = string.IsNullOrWhiteSpace(clean) ? $"Unassigned (was {old})" : $"Assigned to {clean}"
            });
            dirty = true;
            return string.IsNullOrWhiteSpace(clean) ? $"Report #{id} is unassigned." : $"Report #{id} assigned to {clean}.";
        }
    }

    public string AddNote(long id, string actor, string text)
    {
        lock (gate)
        {
            if (!storageReady) return "Cannot update reports while storage is unavailable.";
            var row = state.Reports.FirstOrDefault(x => x.Id == id);
            if (row == null) return $"Report #{id} was not found.";
            if (string.IsNullOrWhiteSpace(NormalizeText(text, MaxNoteLength))) return "Note text is required.";
            if (row.Notes.Count >= MaxNotesPerReport || row.Events.Count >= MaxEventsPerReport) return "Cannot extend this report's history: its safety limit is reached. Existing notes are retained; create a follow-up report.";
            AddNoteUnsafe(row, actor, text);
            dirty = true;
            return $"Private note added to report #{id}.";
        }
    }

    private void AddNoteUnsafe(PlayerReportRecord row, string actor, string text)
    {
        long now = NowMs();
        string clean = NormalizeText(text, MaxNoteLength);
        row.Notes.Add(new PlayerReportNote { UnixMilliseconds = now, Author = CleanActor(actor), Text = clean });
        row.Events.Add(new PlayerReportEvent { UnixMilliseconds = now, Actor = CleanActor(actor), Action = "Note", Detail = "Private staff note added." });
        row.UpdatedUnixMilliseconds = now;
    }

    private void NotifyStaff(PlayerReportRecord row)
    {
        string message = $"[NSE Reports] New issue report #{row.Id} from {row.ReporterName}: {row.Reason}";
        foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (!IsReportStaff(player)) continue;
            try { player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification); } catch { }
        }
        api.Logger.Notification("{0}", message);
    }

    private static bool IsReportStaff(IServerPlayer player) =>
        player.HasPrivilege(Privilege.controlserver) || player.HasPrivilege(Privilege.root) ||
        player.HasPrivilege(AdminService.PrivReports) || player.HasPrivilege(AdminService.PrivManage) ||
        player.HasPrivilege(AdminService.PrivView);

    private void Load()
    {
        try
        {
            if (!File.Exists(statePath))
            {
                state = new PlayerReportState { SchemaVersion = SchemaVersion };
                storageReady = true;
                return;
            }
            var loaded = JsonStateRecovery.Read<PlayerReportState>(statePath, jsonOptions);
            if (loaded.SchemaVersion > SchemaVersion || loaded.SchemaVersion < 1) throw new NotSupportedException("Unsupported report state schema; original file was retained.");
            if (loaded.Reports == null) throw new InvalidDataException("Report collection is null.");
            bool repair = loaded.Reports.Any(x => x == null || x.Id <= 0 || x.Id == long.MaxValue || x.Notes?.Any(n => n == null) == true || x.Events?.Any(e => e == null) == true) || loaded.Reports.Where(x => x != null).GroupBy(x => x.Id).Any(g => g.Count() > 1);
            if (repair) RecoveryFile = JsonStateRecovery.Preserve(statePath);
            loaded.Reports = loaded.Reports.Where(x => x != null && x.Id > 0 && x.Id < long.MaxValue).GroupBy(x => x.Id).Select(g => g.OrderByDescending(x => x.UpdatedUnixMilliseconds).First()).ToList();
            foreach (var row in loaded.Reports)
            {
                row.Notes ??= new List<PlayerReportNote>();
                row.Events ??= new List<PlayerReportEvent>();
                string normalizedStatus = NormalizeStatus(row.Status);
                row.Status = normalizedStatus.Length > 0 ? normalizedStatus : "Open";
                row.Notes = row.Notes.Where(x => x != null).ToList();
                row.Events = row.Events.Where(x => x != null).ToList();
            }
            loaded.NextId = Math.Max(loaded.NextId, loaded.Reports.Count == 0 ? 1 : loaded.Reports.Max(x => x.Id) + 1);
            if (loaded.NextId <= 0 || loaded.NextId == long.MaxValue) throw new NotSupportedException("Report IDs cannot safely advance; original file was retained.");
            state = loaded;
            storageReady = true;
            StorageError = repair ? "Recovered valid reports; the original file was preserved in recovery/." : "";
        }
        catch (Exception ex)
        {
            storageReady = false;
            StorageError = ex.Message;
            if (ex is JsonException || ex is InvalidDataException)
            {
                try { RecoveryFile = JsonStateRecovery.Preserve(statePath); state = new PlayerReportState { SchemaVersion = SchemaVersion }; storageReady = true; StorageError = "Damaged report file preserved in recovery/; a new queue is available."; }
                catch (Exception preserveError) { StorageError += " Recovery failed: " + preserveError.Message; }
            }
            api.Logger.Warning("[ServerEssentials/Reports] Report recovery: writable={0}; {1}", storageReady, StorageError);
        }
    }

    private void OnWorldSave() { SaveIfDirty(); if (!writer.Flush()) api.Logger.Warning("[ServerEssentials/Reports] World-save flush did not complete; pending snapshot remains queued."); }

    private void SaveIfDirty()
    {
        if (!dirty || disposed) return;
        Save();
    }

    private void Save()
    {
        if (!storageReady) return;
        PlayerReportState snapshot;
        lock (gate)
        {
            snapshot = new PlayerReportState
            {
                SchemaVersion = SchemaVersion,
                NextId = state.NextId,
                Reports = state.Reports.Select(x => Clone(x)).Where(x => x != null).Cast<PlayerReportRecord>().ToList()
            };
            dirty = false;
        }
        if (!writer.Queue(() => JsonSerializer.SerializeToUtf8Bytes(snapshot, jsonOptions))) lock (gate) dirty = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        if (dirty)
        {
            try { Save(); } catch { }
        }
        disposed = true;
        writer.Dispose();
        api.Event.GameWorldSave -= OnWorldSave;
        if (saveListenerId >= 0)
        {
            api.Event.UnregisterGameTickListener(saveListenerId);
            saveListenerId = -1;
        }
    }

    private static PlayerReportRecord? Clone(PlayerReportRecord? row, bool summary = false, int offset = 0, int limit = int.MaxValue)
    {
        if (row == null) return null;
        return new PlayerReportRecord
        {
            Id = row.Id,
            CreatedUnixMilliseconds = row.CreatedUnixMilliseconds,
            UpdatedUnixMilliseconds = row.UpdatedUnixMilliseconds,
            Status = row.Status,
            ReporterUid = row.ReporterUid,
            ReporterName = row.ReporterName,
            TargetUid = row.TargetUid,
            TargetName = row.TargetName,
            Reason = row.Reason,
            AssignedStaff = row.AssignedStaff,
            NoteCount = row.Notes?.Count ?? 0, EventCount = row.Events?.Count ?? 0, HistoryOffset = offset, HistoryLimit = limit == int.MaxValue ? 0 : limit,
            Notes = (summary ? (row.Notes ?? new()).TakeLast(2) : limit == int.MaxValue ? row.Notes ?? new() : (row.Notes ?? new()).AsEnumerable().Reverse().Skip(offset).Take(limit)).Select(x => new PlayerReportNote { UnixMilliseconds = x.UnixMilliseconds, Author = x.Author, Text = x.Text }).ToList(),
            Events = (summary ? (row.Events ?? new()).TakeLast(5) : limit == int.MaxValue ? row.Events ?? new() : (row.Events ?? new()).AsEnumerable().Reverse().Skip(offset).Take(limit)).Select(x => new PlayerReportEvent { UnixMilliseconds = x.UnixMilliseconds, Actor = x.Actor, Action = x.Action, Detail = x.Detail }).ToList()
        };
    }

    private static string NormalizeStatus(string? value)
    {
        string v = (value ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            "open" => "Open",
            "investigating" or "investigate" or "working" => "Investigating",
            "resolved" or "resolve" or "closed" => "Resolved",
            _ => ""
        };
    }

    private static string NormalizeStatusFilter(string? value)
    {
        if (string.Equals((value ?? "").Trim(), "all", StringComparison.OrdinalIgnoreCase)) return "All";
        string status = NormalizeStatus(value);
        return status.Length == 0 ? "All" : status;
    }

    private static string NormalizeText(string? value, int maxLength)
    {
        string text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string CleanActor(string? actor) => string.IsNullOrWhiteSpace(actor) ? "Staff" : NormalizeText(actor, 80);
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
