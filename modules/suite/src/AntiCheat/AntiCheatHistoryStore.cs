using Newfies.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BlockLogger.Services;
using NewfiesAntiCheat.Shared.Models;
using Vintagestory.API.Server;

namespace BlockLogger.AntiCheat;

internal sealed class AntiCheatHistoryStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly ICoreServerAPI api;
    private readonly AntiCheatConfig config;
    private readonly object gate = new();
    private readonly string statePath;
    private readonly DeferredJsonFileWriter writer;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private AntiCheatHistoryState state = new();
    private bool dirty;
    private bool storageReady;
    public string StorageError { get; private set; } = "";
    public string RecoveryFile { get; private set; } = "";
    private bool disposed;
    private long saveListenerId = -1;

    public AntiCheatHistoryStore(ICoreServerAPI api, AntiCheatConfig config)
    {
        this.api = api;
        this.config = config;
        string root = Path.Combine(SuiteDataPaths.GetWorldRoot(api), "anticheat");
        Directory.CreateDirectory(root);
        statePath = Path.Combine(root, "incident-history.json");
        writer = new DeferredJsonFileWriter(statePath, error => api.Logger.Warning("[ServerEssentials/AntiCheat] Incident persistence failed; snapshot retained for retry: {0}", error));
    }

    public void Start()
    {
        Load();
        Trim();
        api.Event.GameWorldSave += OnWorldSave;
        saveListenerId = api.Event.RegisterGameTickListener(_ => SaveIfDirty(), 60_000);
        api.Logger.Notification(
            "[ServerEssentials/AntiCheat] Persistent incident history ready: enabled={0}, records={1}, retention={2}d, max={3}.",
            config.HistoryEnabled,
            Count,
            config.HistoryRetentionDays,
            config.HistoryMaxRecords);
    }

    public int Count
    {
        get { lock (gate) return state.Incidents.Count; }
    }

    public void Record(
        IServerPlayer player,
        string code,
        int severity,
        string summary,
        string subject,
        string evidence,
        int riskBefore,
        int riskAfter,
        string statusAfter,
        string consequence)
        => Record(player, "ServerBehavior", code, severity, summary, subject, evidence, riskBefore, riskAfter, statusAfter, consequence, 0);

    public void RecordScanFinding(
        IServerPlayer player,
        ScanFindingDto finding,
        int riskBefore,
        int riskAfter,
        string statusAfter,
        string consequence)
    {
        if (finding == null) return;
        long dedupeWindowMs = string.Equals(finding.Code, "CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(config.IncidentCorrelationWindowSeconds, 10, 600) * 1000L
            : 10 * 60_000L;
        Record(
            player,
            finding.Scanner ?? "Client",
            finding.Code ?? "",
            finding.Severity,
            finding.Summary ?? "",
            finding.Subject ?? "",
            finding.Evidence ?? "",
            riskBefore,
            riskAfter,
            statusAfter,
            consequence,
            dedupeWindowMs);
    }

    private void Record(
        IServerPlayer player,
        string scanner,
        string code,
        int severity,
        string summary,
        string subject,
        string evidence,
        int riskBefore,
        int riskAfter,
        string statusAfter,
        string consequence,
        long dedupeWindowMs)
    {
        if (!storageReady || !config.HistoryEnabled || player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;

        long now = NowMs();
        var pos = EntityRuntimeCompat.GetPos(player.Entity);
        var row = new AntiCheatIncidentRecord
        {
            Id = 0,
            UnixMilliseconds = now,
            PlayerUid = player.PlayerUID ?? "",
            PlayerName = player.PlayerName ?? "Unknown",
            Scanner = Clean(scanner, 64),
            Code = Clean(code, 96),
            Severity = Math.Clamp(severity, 0, 4),
            Summary = Clean(summary, 500),
            Subject = Clean(subject, 160),
            Evidence = Clean(evidence, 1600),
            RiskBefore = Math.Clamp(riskBefore, 0, 100),
            RiskAfter = Math.Clamp(riskAfter, 0, 100),
            StatusAfter = Clean(statusAfter, 40),
            Consequence = Clean(consequence, 500),
            X = pos?.X ?? 0,
            Y = pos?.Y ?? 0,
            Z = pos?.Z ?? 0,
            Dimension = pos?.Dimension ?? 0
        };

        lock (gate)
        {
            if (dedupeWindowMs > 0)
            {
                long cutoff = now - dedupeWindowMs;
                bool duplicate = state.Incidents.Any(x =>
                    x.UnixMilliseconds >= cutoff &&
                    string.Equals(x.PlayerUid, row.PlayerUid, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Scanner, row.Scanner, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Code, row.Code, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Subject, row.Subject, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Evidence, row.Evidence, StringComparison.Ordinal));
                if (duplicate) return;
            }

            row.Id = Math.Max(1, state.NextId++);
            state.Incidents.Add(row);
            TrimUnsafe(now);
            dirty = true;
        }
    }

    public AntiCheatHistoryWebSnapshot GetSnapshot(string? playerFilter, int limit, string? findingFilter = null)
    {
        lock (gate)
        {
            TrimUnsafe(NowMs());
            string filter = (playerFilter ?? "").Trim();
            string finding = (findingFilter ?? "").Trim();
            IEnumerable<AntiCheatIncidentRecord> query = state.Incidents;
            if (filter.Length > 0)
            {
                query = query.Where(x =>
                    (x.PlayerUid ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    (x.PlayerName ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase));
            }

            var findingOptions = query
                .Select(x => (x.Code ?? "").Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (finding.Length > 0)
            {
                query = query.Where(x => string.Equals((x.Code ?? "").Trim(), finding, StringComparison.OrdinalIgnoreCase));
            }

            var selected = query
                .OrderByDescending(x => x.UnixMilliseconds)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToList();

            // Build the player correlation index once per request. The previous implementation
            // rescanned the complete history for every returned row, turning a 250-row dashboard
            // request into O(selected * history) work. Per-player lists are already maintained in
            // timestamp order by the history store, so nearby evidence can be found with a binary
            // search and a very small bounded walk.
            var byPlayer = state.Incidents
                .Where(x => !string.IsNullOrWhiteSpace(x.PlayerUid))
                .GroupBy(x => x.PlayerUid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<AntiCheatIncidentRecord>)g.OrderBy(x => x.UnixMilliseconds).ToList(), StringComparer.OrdinalIgnoreCase);
            var rows = selected.Select(row => ToWeb(row, byPlayer, config.IncidentCorrelationWindowSeconds)).ToList();

            long dayAgo = NowMs() - 86_400_000L;
            return new AntiCheatHistoryWebSnapshot
            {
                StorageReady = storageReady,
                StorageError = writer.LastError.Length > 0 ? writer.LastError : StorageError,
                Enabled = config.HistoryEnabled,
                RetentionDays = config.HistoryRetentionDays,
                MaxRecords = config.HistoryMaxRecords,
                TotalRecords = state.Incidents.Count,
                UniquePlayers = state.Incidents.Select(x => x.PlayerUid).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Last24Hours = state.Incidents.Count(x => x.UnixMilliseconds >= dayAgo),
                PlayerFilter = filter,
                FindingFilter = finding,
                FindingOptions = findingOptions,
                Incidents = rows
            };
        }
    }

    public AntiCheatHistorySummaryWebSnapshot GetSummary()
    {
        lock (gate)
        {
            TrimUnsafe(NowMs());
            long dayAgo = NowMs() - 86_400_000L;
            return new AntiCheatHistorySummaryWebSnapshot
            {
                Enabled = config.HistoryEnabled,
                RetentionDays = config.HistoryRetentionDays,
                MaxRecords = config.HistoryMaxRecords,
                TotalRecords = state.Incidents.Count,
                UniquePlayers = state.Incidents.Select(x => x.PlayerUid).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Last24Hours = state.Incidents.Count(x => x.UnixMilliseconds >= dayAgo)
            };
        }
    }

    public int ClearAll()
    {
        int cleared;
        lock (gate)
        {
            cleared = state.Incidents.Count;
            state.Incidents.Clear();
            state.NextId = 1;
            dirty = true;
        }
        SaveIfDirty();
        return cleared;
    }

    public void ApplyRetentionNow()
    {
        lock (gate)
        {
            int before = state.Incidents.Count;
            TrimUnsafe(NowMs());
            if (state.Incidents.Count != before) dirty = true;
        }
        SaveIfDirty();
    }

    private static AntiCheatIncidentWebSnapshot ToWeb(
        AntiCheatIncidentRecord row,
        IReadOnlyDictionary<string, IReadOnlyList<AntiCheatIncidentRecord>> byPlayer,
        int correlationWindowSeconds)
    {
        long correlationWindowMs = Math.Clamp(correlationWindowSeconds, 10, 600) * 1000L;
        var nearbyRows = new List<AntiCheatIncidentRecord>(16);

        if (byPlayer.TryGetValue(row.PlayerUid ?? "", out var playerRows) && playerRows.Count > 0)
        {
            long min = row.UnixMilliseconds - correlationWindowMs;
            long max = row.UnixMilliseconds + correlationWindowMs;
            int lo = 0, hi = playerRows.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (playerRows[mid].UnixMilliseconds < min) lo = mid + 1;
                else hi = mid;
            }

            for (int i = lo; i < playerRows.Count && playerRows[i].UnixMilliseconds <= max; i++)
                nearbyRows.Add(playerRows[i]);

            if (nearbyRows.Count > 16)
            {
                nearbyRows = nearbyRows
                    .OrderBy(x => Math.Abs(x.UnixMilliseconds - row.UnixMilliseconds))
                    .Take(16)
                    .ToList();
            }
        }

        var nearby = nearbyRows.Select(x => new ScanFindingDto
        {
            Scanner = x.Scanner ?? "", Code = x.Code ?? "", Severity = x.Severity, Summary = x.Summary ?? "",
            Subject = x.Subject ?? "", Evidence = x.Evidence ?? ""
        }).ToList();
        var current = nearby.FirstOrDefault(x => string.Equals(x.Code, row.Code, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Evidence, row.Evidence, StringComparison.Ordinal))
            ?? new ScanFindingDto { Scanner = row.Scanner ?? "", Code = row.Code ?? "", Severity = row.Severity, Summary = row.Summary ?? "", Subject = row.Subject ?? "", Evidence = row.Evidence ?? "" };
        var insight = AntiCheatFindingIntelligence.Analyze(current, nearby);
        return new AntiCheatIncidentWebSnapshot
        {
            Id = row.Id, UnixMilliseconds = row.UnixMilliseconds, PlayerUid = row.PlayerUid ?? "", PlayerName = row.PlayerName ?? "", Scanner = row.Scanner ?? "", Code = row.Code ?? "", Severity = row.Severity,
            Summary = row.Summary ?? "", Subject = row.Subject ?? "", Evidence = row.Evidence ?? "", RiskBefore = row.RiskBefore, RiskAfter = row.RiskAfter, StatusAfter = row.StatusAfter ?? "", Consequence = row.Consequence ?? "",
            X = row.X, Y = row.Y, Z = row.Z, Dimension = row.Dimension, Category = insight.Category ?? "", Confidence = insight.Confidence ?? "Low", ConfidenceScore = insight.ConfidenceScore,
            CorroboratingSignals = insight.CorroboratingSignals, Why = insight.Why ?? "", RecommendedAction = insight.RecommendedAction ?? ""
        };
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(statePath))
            {
                state = new AntiCheatHistoryState { SchemaVersion = SchemaVersion };
                storageReady = true;
                return;
            }
            var loaded = JsonStateRecovery.Read<AntiCheatHistoryState>(statePath, jsonOptions);
            if (loaded.SchemaVersion > SchemaVersion || loaded.SchemaVersion < 1) throw new NotSupportedException("Unsupported incident state schema; original file was retained.");
            if (loaded.Incidents == null) throw new InvalidDataException("Incident collection is null.");
            bool repair = loaded.Incidents.Any(x => x == null || x.Id <= 0 || x.Id == long.MaxValue || x.PlayerUid == null || x.PlayerName == null || x.Scanner == null || x.Code == null || x.Summary == null || x.Subject == null || x.Evidence == null || x.StatusAfter == null || x.Consequence == null) || loaded.Incidents.Where(x => x != null).GroupBy(x => x.Id).Any(g => g.Count() > 1);
            if (repair) RecoveryFile = JsonStateRecovery.Preserve(statePath);
            loaded.Incidents = loaded.Incidents.Where(x => x != null && x.Id > 0 && x.Id < long.MaxValue).GroupBy(x => x.Id).Select(g => g.OrderByDescending(x => x.UnixMilliseconds).First()).Select(Clone).ToList();
            loaded.NextId = Math.Max(loaded.NextId, loaded.Incidents.Count == 0 ? 1 : loaded.Incidents.Max(x => x.Id) + 1);
            if (loaded.NextId <= 0 || loaded.NextId == long.MaxValue) throw new NotSupportedException("Incident IDs cannot safely advance; original file was retained.");
            state = loaded;
            storageReady = true;
            StorageError = repair ? "Recovered valid incidents; original file preserved in recovery/." : "";
        }
        catch (Exception ex)
        {
            storageReady = false;
            StorageError = ex.Message;
            if (ex is JsonException || ex is InvalidDataException)
            {
                try { RecoveryFile = JsonStateRecovery.Preserve(statePath); state = new AntiCheatHistoryState { SchemaVersion = SchemaVersion }; storageReady = true; StorageError = "Damaged incident file preserved in recovery/; a new history is available."; }
                catch (Exception preserveError) { StorageError += " Recovery failed: " + preserveError.Message; }
            }
            api.Logger.Warning("[ServerEssentials/AntiCheat] Incident recovery: writable={0}; {1}", storageReady, StorageError);
        }
    }

    private void Trim()
    {
        lock (gate)
        {
            int before = state.Incidents.Count;
            TrimUnsafe(NowMs());
            if (before != state.Incidents.Count) dirty = true;
        }
    }

    private void TrimUnsafe(long now)
    {
        state.Incidents ??= new List<AntiCheatIncidentRecord>();
        long cutoff = now - (long)config.HistoryRetentionDays * 86_400_000L;
        if (cutoff > 0) state.Incidents.RemoveAll(x => x.UnixMilliseconds > 0 && x.UnixMilliseconds < cutoff);

        int max = Math.Max(100, config.HistoryMaxRecords);
        if (state.Incidents.Count > max)
        {
            state.Incidents = state.Incidents
                .OrderByDescending(x => x.UnixMilliseconds)
                .Take(max)
                .OrderBy(x => x.UnixMilliseconds)
                .ToList();
        }
    }

    private void OnWorldSave() { SaveIfDirty(); if (!writer.Flush()) api.Logger.Warning("[ServerEssentials/AntiCheat] Incident world-save flush did not complete; pending snapshot remains queued."); }

    private void SaveIfDirty()
    {
        if (!dirty || disposed) return;
        Save();
    }

    private void Save()
    {
        if (!storageReady) return;
        AntiCheatHistoryState snapshot;
        lock (gate)
        {
            snapshot = new AntiCheatHistoryState
            {
                SchemaVersion = SchemaVersion,
                NextId = state.NextId,
                Incidents = state.Incidents.Select(Clone).ToList()
            };
            dirty = false;
        }

        if (!writer.Queue(() => JsonSerializer.SerializeToUtf8Bytes(snapshot, jsonOptions))) lock (gate) dirty = true;
    }

    private static AntiCheatIncidentRecord Clone(AntiCheatIncidentRecord x) => new()
    {
        Id = x.Id,
        UnixMilliseconds = x.UnixMilliseconds,
        PlayerUid = x.PlayerUid ?? "",
        PlayerName = x.PlayerName ?? "",
        Scanner = x.Scanner ?? "",
        Code = x.Code ?? "",
        Severity = x.Severity,
        Summary = x.Summary ?? "",
        Subject = x.Subject ?? "",
        Evidence = x.Evidence ?? "",
        RiskBefore = x.RiskBefore,
        RiskAfter = x.RiskAfter,
        StatusAfter = x.StatusAfter ?? "",
        Consequence = x.Consequence ?? "",
        X = x.X,
        Y = x.Y,
        Z = x.Z,
        Dimension = x.Dimension
    };

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

    private static string Clean(string? value, int max)
    {
        string text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max];
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

internal sealed class AntiCheatHistoryState
{
    public int SchemaVersion { get; set; } = 1;
    public long NextId { get; set; } = 1;
    public List<AntiCheatIncidentRecord> Incidents { get; set; } = new();
}

internal sealed class AntiCheatIncidentRecord
{
    public long Id { get; set; }
    public long UnixMilliseconds { get; set; }
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string Scanner { get; set; } = "ServerBehavior";
    public string Code { get; set; } = "";
    public int Severity { get; set; }
    public string Summary { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Evidence { get; set; } = "";
    public int RiskBefore { get; set; }
    public int RiskAfter { get; set; }
    public string StatusAfter { get; set; } = "";
    public string Consequence { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int Dimension { get; set; }
}
