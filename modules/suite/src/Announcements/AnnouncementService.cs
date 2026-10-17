using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.Announcements;

public sealed class AnnouncementConfig
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool Enabled { get; set; } = true;
    public string Prefix { get; set; } = "[Server]";
    public List<AnnouncementRule> Rules { get; set; } = new();
}

public sealed class AnnouncementRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "Announcement";
    public bool Enabled { get; set; } = true;
    public string Message { get; set; } = "";
    // interval | daily
    public string Mode { get; set; } = "interval";
    public int IntervalMinutes { get; set; } = 60;
    // Optional local-time anchor for interval schedules (HH:mm).
    public string StartTime { get; set; } = "";
    // Local-time daily schedule entries (HH:mm).
    public List<string> DailyTimes { get; set; } = new();
    // 0 = unlimited.
    public int MaxRuns { get; set; } = 0;
    public int RunsCompleted { get; set; } = 0;
    public long LastRunUnixMilliseconds { get; set; }
    public long NextRunUnixMilliseconds { get; set; }
}

public sealed class AnnouncementSnapshot
{
    public bool Enabled { get; set; }
    public string Prefix { get; set; } = "";
    public string Timezone { get; set; } = "";
    public int EnabledRules { get; set; }
    public int TotalRules { get; set; }
    public List<AnnouncementRule> Rules { get; set; } = new();
}

public sealed class AnnouncementService : IDisposable
{
    private const string ConfigFile = "NewfiesAnnouncements.json";
    private readonly ICoreServerAPI api;
    private readonly object sync = new();
    private AnnouncementConfig config = new();
    private long tickListenerId = -1;
    private bool disposed;

    public AnnouncementService(ICoreServerAPI api) => this.api = api;

    public void Start()
    {
        Load();
        RecalculateAllNextRuns(DateTime.Now, onlyMissing: false);
        Save();
        tickListenerId = api.Event.RegisterGameTickListener(_ => Tick(), 10_000);
        api.Logger.Notification("[ServerEssentials/Announcements] Loaded {0} announcement rule(s), {1} enabled.", config.Rules.Count, config.Rules.Count(x => x.Enabled));
    }

    public AnnouncementSnapshot GetSnapshot()
    {
        lock (sync)
        {
            return new AnnouncementSnapshot
            {
                Enabled = config.Enabled,
                Prefix = config.Prefix,
                Timezone = TimeZoneInfo.Local.DisplayName,
                EnabledRules = config.Rules.Count(x => x.Enabled),
                TotalRules = config.Rules.Count,
                Rules = config.Rules.Select(Clone).OrderBy(x => x.NextRunUnixMilliseconds <= 0 ? long.MaxValue : x.NextRunUnixMilliseconds).ToList()
            };
        }
    }

    public string SaveFromJson(string body, JsonSerializerOptions options)
    {
        AnnouncementConfig? incoming;
        try { incoming = JsonSerializer.Deserialize<AnnouncementConfig>(body, options); }
        catch (Exception ex) { return "Invalid announcement configuration: " + ex.Message; }
        if (incoming == null) return "Announcement configuration is required.";

        lock (sync)
        {
            Normalize(incoming);
            // Runtime counters are server-owned. Editing a rule must not accidentally
            // reset a finite schedule; the explicit Reset runs action does that.
            var old = config.Rules.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var rule in incoming.Rules)
            {
                if (!old.TryGetValue(rule.Id, out var previous)) continue;
                rule.RunsCompleted = previous.RunsCompleted;
                rule.LastRunUnixMilliseconds = previous.LastRunUnixMilliseconds;
            }
            config = incoming;
            RecalculateAllNextRuns(DateTime.Now, onlyMissing: false);
            SaveUnsafe();
            return $"Saved {config.Rules.Count} announcement rule(s).";
        }
    }

    public string RunNow(string id)
    {
        lock (sync)
        {
            var rule = config.Rules.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (rule == null) return "Announcement rule was not found.";
            return BroadcastUnsafe(rule, manual: true);
        }
    }

    public string ResetRuns(string id)
    {
        lock (sync)
        {
            var rule = config.Rules.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (rule == null) return "Announcement rule was not found.";
            rule.RunsCompleted = 0;
            rule.LastRunUnixMilliseconds = 0;
            rule.Enabled = true;
            rule.NextRunUnixMilliseconds = CalculateNextRun(rule, DateTime.Now);
            SaveUnsafe();
            return $"Reset run counter for {rule.Name}.";
        }
    }

    private void Tick()
    {
        if (disposed) return;
        lock (sync)
        {
            if (!config.Enabled) return;
            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            bool changed = false;
            foreach (var rule in config.Rules.Where(x => x.Enabled).ToList())
            {
                if (rule.MaxRuns > 0 && rule.RunsCompleted >= rule.MaxRuns)
                {
                    rule.Enabled = false;
                    rule.NextRunUnixMilliseconds = 0;
                    changed = true;
                    continue;
                }
                if (rule.NextRunUnixMilliseconds <= 0)
                {
                    rule.NextRunUnixMilliseconds = CalculateNextRun(rule, DateTime.Now);
                    changed = true;
                    continue;
                }
                if (now < rule.NextRunUnixMilliseconds) continue;
                BroadcastUnsafe(rule, manual: false);
                changed = true;
            }
            if (changed) SaveUnsafe();
        }
    }

    private string BroadcastUnsafe(AnnouncementRule rule, bool manual)
    {
        if (string.IsNullOrWhiteSpace(rule.Message)) return "Announcement message is empty.";
        string message = Expand(rule.Message);
        string prefix = string.IsNullOrWhiteSpace(config.Prefix) ? "" : config.Prefix.Trim() + " ";
        api.BroadcastMessageToAllGroups(prefix + message, EnumChatType.Notification);
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        rule.LastRunUnixMilliseconds = now;
        if (!manual) rule.RunsCompleted++;
        if (!manual && rule.MaxRuns > 0 && rule.RunsCompleted >= rule.MaxRuns)
        {
            rule.Enabled = false;
            rule.NextRunUnixMilliseconds = 0;
        }
        else
        {
            rule.NextRunUnixMilliseconds = CalculateNextRun(rule, DateTime.Now.AddSeconds(1));
        }
        SaveUnsafe();
        return manual ? $"Sent test announcement: {rule.Name}." : $"Sent announcement: {rule.Name}.";
    }

    private string Expand(string message)
    {
        int players = 0;
        try { players = api.World.AllOnlinePlayers?.Length ?? 0; } catch { }
        return (message ?? "")
            .Replace("{players}", players.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    private void RecalculateAllNextRuns(DateTime now, bool onlyMissing)
    {
        foreach (var rule in config.Rules)
        {
            if (rule.MaxRuns > 0 && rule.RunsCompleted >= rule.MaxRuns)
            {
                rule.Enabled = false;
                rule.NextRunUnixMilliseconds = 0;
                continue;
            }
            if (!onlyMissing || rule.NextRunUnixMilliseconds <= 0)
                rule.NextRunUnixMilliseconds = rule.Enabled ? CalculateNextRun(rule, now) : 0;
        }
    }

    private static long CalculateNextRun(AnnouncementRule rule, DateTime now)
    {
        if (!rule.Enabled) return 0;
        if (string.Equals(rule.Mode, "daily", StringComparison.OrdinalIgnoreCase))
        {
            var times = ParseTimes(rule.DailyTimes);
            if (times.Count == 0) times.Add(new TimeSpan(12, 0, 0));
            foreach (var t in times)
            {
                var candidate = now.Date + t;
                if (candidate > now) return new DateTimeOffset(candidate).ToUnixTimeMilliseconds();
            }
            return new DateTimeOffset(now.Date.AddDays(1) + times[0]).ToUnixTimeMilliseconds();
        }

        int interval = Math.Clamp(rule.IntervalMinutes, 1, 10080);
        if (TryTime(rule.StartTime, out var anchorTime))
        {
            DateTime anchor = now.Date + anchorTime;
            if (anchor > now) return new DateTimeOffset(anchor).ToUnixTimeMilliseconds();
            double elapsed = (now - anchor).TotalMinutes;
            double steps = Math.Floor(elapsed / interval) + 1;
            return new DateTimeOffset(anchor.AddMinutes(steps * interval)).ToUnixTimeMilliseconds();
        }
        return new DateTimeOffset(now.AddMinutes(interval)).ToUnixTimeMilliseconds();
    }

    private void Load()
    {
        config = ModConfigRecovery.LoadOrNull<AnnouncementConfig>(
            api,
            ConfigFile,
            "ServerEssentials/Announcements") ?? new AnnouncementConfig();
        Normalize(config);
    }

    private static void Normalize(AnnouncementConfig cfg)
    {
        cfg.SchemaVersion = Math.Max(AnnouncementConfig.CurrentSchemaVersion, cfg.SchemaVersion);
        cfg.Prefix = (cfg.Prefix ?? "").Trim();
        if (cfg.Prefix.Length > 80) cfg.Prefix = cfg.Prefix[..80];
        cfg.Rules ??= new List<AnnouncementRule>();
        if (cfg.Rules.Count > 100) cfg.Rules = cfg.Rules.Take(100).ToList();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in cfg.Rules)
        {
            r.Id = (r.Id ?? "").Trim();
            if (r.Id.Length == 0 || !ids.Add(r.Id))
            {
                do { r.Id = Guid.NewGuid().ToString("N")[..10]; } while (!ids.Add(r.Id));
            }
            r.Name = string.IsNullOrWhiteSpace(r.Name) ? "Announcement" : r.Name.Trim();
            if (r.Name.Length > 80) r.Name = r.Name[..80];
            r.Message = (r.Message ?? "").Trim();
            if (r.Message.Length > 1000) r.Message = r.Message[..1000];
            r.Mode = string.Equals(r.Mode, "daily", StringComparison.OrdinalIgnoreCase) ? "daily" : "interval";
            r.IntervalMinutes = Math.Clamp(r.IntervalMinutes, 1, 10080);
            r.StartTime = NormalizeTime(r.StartTime);
            r.DailyTimes ??= new List<string>();
            r.DailyTimes = r.DailyTimes.Select(NormalizeTime).Where(x => x.Length > 0).Distinct().OrderBy(x => x).Take(24).ToList();
            r.MaxRuns = Math.Clamp(r.MaxRuns, 0, 1_000_000);
            r.RunsCompleted = Math.Max(0, r.RunsCompleted);
        }
    }

    private static string NormalizeTime(string? value) => TryTime(value, out var t) ? $"{t.Hours:00}:{t.Minutes:00}" : "";
    private static bool TryTime(string? value, out TimeSpan time) => TimeSpan.TryParseExact((value ?? "").Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out time) && time.TotalHours < 24;
    private static List<TimeSpan> ParseTimes(IEnumerable<string>? values) => (values ?? Array.Empty<string>()).Select(x => TryTime(x, out var t) ? (TimeSpan?)t : null).Where(x => x.HasValue).Select(x => x!.Value).Distinct().OrderBy(x => x).ToList();
    private static AnnouncementRule Clone(AnnouncementRule r) => new()
    {
        Id = r.Id, Name = r.Name, Enabled = r.Enabled, Message = r.Message, Mode = r.Mode,
        IntervalMinutes = r.IntervalMinutes, StartTime = r.StartTime, DailyTimes = r.DailyTimes.ToList(),
        MaxRuns = r.MaxRuns, RunsCompleted = r.RunsCompleted, LastRunUnixMilliseconds = r.LastRunUnixMilliseconds,
        NextRunUnixMilliseconds = r.NextRunUnixMilliseconds
    };

    private void Save() { lock (sync) SaveUnsafe(); }
    private void SaveUnsafe()
    {
        try { BlockLogger.Services.SuiteConfigStorage.Store(api, config, ConfigFile); }
        catch (Exception ex) { api.Logger.Warning("[ServerEssentials/Announcements] Could not save config: {0}: {1}", ex.GetType().Name, ex.Message); }
    }

    public void Dispose()
    {
        disposed = true;
        if (tickListenerId >= 0) { try { api.Event.UnregisterGameTickListener(tickListenerId); } catch { } tickListenerId = -1; }
        Save();
    }
}
