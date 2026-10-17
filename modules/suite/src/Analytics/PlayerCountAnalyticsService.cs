using Newfies.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using BlockLogger.Services;
using Vintagestory.API.Server;

namespace BlockLogger.Analytics;

/// <summary>
/// Very low-overhead, profiler-independent player-count history.
/// Population changes are recorded on player lifecycle events, with a one-minute
/// reconciliation so missed events cannot leave the counter stale. A time-weighted
/// average is accumulated without high-frequency polling.
/// </summary>
public sealed class PlayerCountAnalyticsService : IDisposable
{
    private const int ReconcileIntervalMs = 60_000;
    private const int PersistIntervalMs = 300_000;
    private const int DailyRetentionDays = 400;
    private const int HourlyRetentionDays = 45;
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ICoreServerAPI api;
    private readonly object gate = new();
    private readonly HashSet<string> activePlayers = new(StringComparer.OrdinalIgnoreCase);
    private PlayerCountAnalyticsState state = new();
    private readonly string statePath;
    private readonly DeferredJsonFileWriter writer;
    private bool storageReady;
    private long listenerId = -1;
    private long lastObservedUnixMilliseconds;
    private long lastPersistUnixMilliseconds;
    private bool started;
    private bool disposed;

    public PlayerCountAnalyticsService(ICoreServerAPI api)
    {
        this.api = api;
        string diagnostics = Path.Combine(SuiteDataPaths.GetWorldRoot(api), "diagnostics");
        Directory.CreateDirectory(diagnostics);
        statePath = Path.Combine(diagnostics, "player-count-analytics.json");
        writer = new DeferredJsonFileWriter(statePath, error => api.Logger.Warning("[PlayerAnalytics] Persistence failed; snapshot retained for retry: {0}", error));
    }

    public void Start()
    {
        if (started) return;
        started = true;

        Load();
        lock (gate)
        {
            activePlayers.Clear();
            foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (!string.IsNullOrWhiteSpace(player.PlayerUID))
                    activePlayers.Add(player.PlayerUID);
            }

            long now = NowMs();
            lastObservedUnixMilliseconds = now;
            lastPersistUnixMilliseconds = now;
            TouchExtremaUnsafe(now, activePlayers.Count);
        }

        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        api.Event.PlayerDisconnect += OnPlayerDisconnect;
        api.Event.GameWorldSave += OnWorldSave;
        listenerId = api.Event.RegisterGameTickListener(
            _ => ReconcileAndMaintain(),
            ex => api.Logger.Warning(
                "[PlayerAnalytics] reconciliation failed: {0}: {1}",
                ex.GetType().Name,
                ex.Message),
            ReconcileIntervalMs);

        api.Logger.Notification(
            "[PlayerAnalytics] Player-count history started. CurrentPlayers={0}, retention={1} daily/{2} hourly days.",
            activePlayers.Count,
            DailyRetentionDays,
            HourlyRetentionDays);
    }

    public PlayerCountAnalyticsSnapshot GetSnapshot(int dailyDays = 30)
    {
        lock (gate)
        {
            ObserveElapsedUnsafe(NowMs());
            PruneUnsafe();

            int boundedDays = Math.Clamp(dailyDays, 7, 365);
            string todayKey = LocalDateKey(NowMs());
            var today = state.Daily.FirstOrDefault(x => x.Date == todayKey);

            var daily = state.Daily
                .OrderBy(x => x.Date, StringComparer.Ordinal)
                .TakeLast(boundedDays)
                .Select(x => new PlayerCountDailyPoint
                {
                    Date = x.Date,
                    Low = x.HasObservation ? x.Low : 0,
                    High = x.HasObservation ? x.High : 0,
                    Average = x.ObservedMilliseconds <= 0 ? x.High : WeightedAverage(x.WeightedPlayerMilliseconds, x.ObservedMilliseconds),
                    ObservedMinutes = x.ObservedMilliseconds / 60_000d
                })
                .ToList();

            var hourMap = state.Hourly
                .Where(x => x.Date == todayKey)
                .ToDictionary(x => x.Hour);

            var hourly = new List<PlayerCountHourlyPoint>(24);
            for (int hour = 0; hour < 24; hour++)
            {
                if (hourMap.TryGetValue(hour, out var row) && row.HasObservation)
                {
                    hourly.Add(new PlayerCountHourlyPoint
                    {
                        Hour = hour,
                        Label = $"{hour:00}:00",
                        Average = row.ObservedMilliseconds <= 0 ? row.High : WeightedAverage(row.WeightedPlayerMilliseconds, row.ObservedMilliseconds),
                        Low = row.Low,
                        High = row.High,
                        ObservedMinutes = row.ObservedMilliseconds / 60_000d
                    });
                }
                else
                {
                    hourly.Add(new PlayerCountHourlyPoint
                    {
                        Hour = hour,
                        Label = $"{hour:00}:00"
                    });
                }
            }

            DateTime localHourStart = DateTime.Now;
            localHourStart = new DateTime(localHourStart.Year, localHourStart.Month, localHourStart.Day, localHourStart.Hour, 0, 0, DateTimeKind.Local);
            var last24Hours = new List<PlayerCountHourlyPoint>(24);
            for (int offset = 23; offset >= 0; offset--)
            {
                DateTime hourStart = localHourStart.AddHours(-offset);
                string dateKey = hourStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                int hour = hourStart.Hour;
                var row = state.Hourly.FirstOrDefault(x => x.Date == dateKey && x.Hour == hour);
                if (row?.HasObservation == true)
                {
                    last24Hours.Add(new PlayerCountHourlyPoint
                    {
                        Date = dateKey,
                        Hour = hour,
                        Label = hourStart.ToString("HH:mm", CultureInfo.InvariantCulture),
                        Average = row.ObservedMilliseconds <= 0 ? row.High : WeightedAverage(row.WeightedPlayerMilliseconds, row.ObservedMilliseconds),
                        Low = row.Low,
                        High = row.High,
                        ObservedMinutes = row.ObservedMilliseconds / 60_000d
                    });
                }
                else
                {
                    last24Hours.Add(new PlayerCountHourlyPoint
                    {
                        Date = dateKey,
                        Hour = hour,
                        Label = hourStart.ToString("HH:mm", CultureInfo.InvariantCulture)
                    });
                }
            }

            return new PlayerCountAnalyticsSnapshot
            {
                GeneratedUnixMilliseconds = NowMs(),
                CurrentPlayers = activePlayers.Count,
                ServerTimeZone = TimeZoneInfo.Local.Id,
                DailyRetentionDays = DailyRetentionDays,
                HourlyRetentionDays = HourlyRetentionDays,
                TodayLow = today?.HasObservation == true ? today.Low : activePlayers.Count,
                TodayHigh = today?.HasObservation == true ? today.High : activePlayers.Count,
                TodayAverage = today == null || today.ObservedMilliseconds <= 0
                    ? activePlayers.Count
                    : WeightedAverage(today.WeightedPlayerMilliseconds, today.ObservedMilliseconds),
                Daily = daily,
                TodayHourly = hourly,
                Last24Hours = last24Hours
            };
        }
    }

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        if (disposed || string.IsNullOrWhiteSpace(player.PlayerUID)) return;
        lock (gate)
        {
            long now = NowMs();
            ObserveElapsedUnsafe(now);
            if (activePlayers.Add(player.PlayerUID))
                TouchExtremaUnsafe(now, activePlayers.Count);
        }
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        if (disposed || string.IsNullOrWhiteSpace(player.PlayerUID)) return;
        lock (gate)
        {
            long now = NowMs();
            ObserveElapsedUnsafe(now);
            if (activePlayers.Remove(player.PlayerUID))
                TouchExtremaUnsafe(now, activePlayers.Count);
        }
    }

    private void ReconcileAndMaintain()
    {
        if (disposed) return;

        bool shouldPersist;
        lock (gate)
        {
            long now = NowMs();
            ObserveElapsedUnsafe(now);

            var actual = new HashSet<string>(
                api.World.AllOnlinePlayers
                    .OfType<IServerPlayer>()
                    .Select(x => x.PlayerUID)
                    .Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);

            if (!activePlayers.SetEquals(actual))
            {
                activePlayers.Clear();
                foreach (string uid in actual) activePlayers.Add(uid);
                TouchExtremaUnsafe(now, activePlayers.Count);
            }

            PruneUnsafe();
            shouldPersist = now - lastPersistUnixMilliseconds >= PersistIntervalMs;
            if (shouldPersist) lastPersistUnixMilliseconds = now;
        }

        if (shouldPersist) Save();
    }

    private void ObserveElapsedUnsafe(long nowUnixMilliseconds)
    {
        if (lastObservedUnixMilliseconds <= 0)
        {
            lastObservedUnixMilliseconds = nowUnixMilliseconds;
            TouchExtremaUnsafe(nowUnixMilliseconds, activePlayers.Count);
            return;
        }

        if (nowUnixMilliseconds <= lastObservedUnixMilliseconds) return;

        long cursor = lastObservedUnixMilliseconds;
        int count = activePlayers.Count;

        // Split elapsed time at local-hour boundaries. This makes the hourly
        // averages time-weighted without a frequent sampler.
        while (cursor < nowUnixMilliseconds)
        {
            DateTimeOffset local = DateTimeOffset.FromUnixTimeMilliseconds(cursor).ToLocalTime();
            DateTimeOffset nextHour = new(
                local.Year,
                local.Month,
                local.Day,
                local.Hour,
                0,
                0,
                local.Offset);
            nextHour = nextHour.AddHours(1);

            long end = Math.Min(nowUnixMilliseconds, nextHour.ToUnixTimeMilliseconds());
            if (end <= cursor) end = nowUnixMilliseconds;
            long elapsed = end - cursor;

            string dateKey = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var daily = GetDailyUnsafe(dateKey);
            var hourly = GetHourlyUnsafe(dateKey, local.Hour);

            daily.ObservedMilliseconds += elapsed;
            daily.WeightedPlayerMilliseconds += elapsed * (double)count;
            hourly.ObservedMilliseconds += elapsed;
            hourly.WeightedPlayerMilliseconds += elapsed * (double)count;

            ObserveExtrema(daily, count);
            ObserveExtrema(hourly, count);
            cursor = end;
        }

        lastObservedUnixMilliseconds = nowUnixMilliseconds;
    }

    private void TouchExtremaUnsafe(long nowUnixMilliseconds, int count)
    {
        DateTimeOffset local = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMilliseconds).ToLocalTime();
        string dateKey = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        ObserveExtrema(GetDailyUnsafe(dateKey), count);
        ObserveExtrema(GetHourlyUnsafe(dateKey, local.Hour), count);
    }

    private PlayerCountDailyAccumulator GetDailyUnsafe(string date)
    {
        var row = state.Daily.FirstOrDefault(x => x.Date == date);
        if (row != null) return row;
        row = new PlayerCountDailyAccumulator { Date = date };
        state.Daily.Add(row);
        return row;
    }

    private PlayerCountHourlyAccumulator GetHourlyUnsafe(string date, int hour)
    {
        var row = state.Hourly.FirstOrDefault(x => x.Date == date && x.Hour == hour);
        if (row != null) return row;
        row = new PlayerCountHourlyAccumulator { Date = date, Hour = hour };
        state.Hourly.Add(row);
        return row;
    }

    private static void ObserveExtrema(PlayerCountAccumulator row, int count)
    {
        if (!row.HasObservation)
        {
            row.HasObservation = true;
            row.Low = count;
            row.High = count;
            return;
        }

        row.Low = Math.Min(row.Low, count);
        row.High = Math.Max(row.High, count);
    }

    private void PruneUnsafe()
    {
        DateTime today = DateTime.Now.Date;
        string dailyCutoff = today.AddDays(-(DailyRetentionDays - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string hourlyCutoff = today.AddDays(-(HourlyRetentionDays - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        state.Daily.RemoveAll(x => string.CompareOrdinal(x.Date, dailyCutoff) < 0);
        state.Hourly.RemoveAll(x => string.CompareOrdinal(x.Date, hourlyCutoff) < 0);
    }

    private void OnWorldSave() { Save(); if (!writer.Flush()) api.Logger.Warning("[PlayerAnalytics] World-save flush did not complete; snapshot remains queued."); }

    private void Load()
    {
        try
        {
            if (!File.Exists(statePath))
            {
                state = new PlayerCountAnalyticsState { SchemaVersion = SchemaVersion };
                storageReady = true;
                return;
            }

            var loaded = JsonStateRecovery.Read<PlayerCountAnalyticsState>(statePath, JsonOptions);
            if (loaded.SchemaVersion != SchemaVersion) throw new NotSupportedException("Unsupported analytics schema; original file retained.");
            bool ValidDate(string date) => DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            bool ValidAccumulator(PlayerCountAccumulator row) => row.ObservedMilliseconds >= 0 && double.IsFinite(row.WeightedPlayerMilliseconds) && row.WeightedPlayerMilliseconds >= 0 && (!row.HasObservation || (row.Low >= 0 && row.High >= row.Low));
            bool ValidDaily(PlayerCountDailyAccumulator? row) => row != null && ValidDate(row.Date) && ValidAccumulator(row);
            bool ValidHourly(PlayerCountHourlyAccumulator? row) => row != null && ValidDate(row.Date) && row.Hour >= 0 && row.Hour <= 23 && ValidAccumulator(row);
            var daily = loaded.Daily ?? new();
            var hourly = loaded.Hourly ?? new();
            bool repair = loaded.Daily == null || loaded.Hourly == null || daily.Any(x => !ValidDaily(x)) || hourly.Any(x => !ValidHourly(x))
                || daily.Where(ValidDaily).GroupBy(x => x.Date).Any(g => g.Count() > 1) || hourly.Where(ValidHourly).GroupBy(x => (x.Date, x.Hour)).Any(g => g.Count() > 1);
            if (repair) JsonStateRecovery.Preserve(statePath);
            loaded.Daily = daily.Where(ValidDaily).GroupBy(x => x.Date).Select(g => g.OrderByDescending(x => x.ObservedMilliseconds).First()).ToList();
            loaded.Hourly = hourly.Where(ValidHourly).GroupBy(x => (x.Date, x.Hour)).Select(g => g.OrderByDescending(x => x.ObservedMilliseconds).First()).ToList();
            state = loaded;
            storageReady = true;
            state.Daily ??= new List<PlayerCountDailyAccumulator>();
            state.Hourly ??= new List<PlayerCountHourlyAccumulator>();
        }
        catch (Exception ex)
        {
            storageReady = false;
            if (ex is JsonException || ex is InvalidDataException)
            {
                try { JsonStateRecovery.Preserve(statePath); state = new PlayerCountAnalyticsState { SchemaVersion = SchemaVersion }; storageReady = true; } catch { }
            }
            api.Logger.Warning(
                "[PlayerAnalytics] Could not load history; original data retained or preserved in recovery/: {0}: {1}",
                ex.GetType().Name,
                ex.Message);
        }
    }

    private void Save()
    {
        if (!storageReady) return;
        PlayerCountAnalyticsState snapshot;
        lock (gate) { ObserveElapsedUnsafe(NowMs()); PruneUnsafe(); snapshot = state.Clone(); }
        writer.Queue(() => JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        api.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
        api.Event.PlayerDisconnect -= OnPlayerDisconnect;
        api.Event.GameWorldSave -= OnWorldSave;
        if (listenerId >= 0)
        {
            api.Event.UnregisterGameTickListener(listenerId);
            listenerId = -1;
        }

        try { Save(); } catch { }
        writer.Dispose();
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string LocalDateKey(long unixMilliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds)
            .ToLocalTime()
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static double WeightedAverage(double weighted, long observed) =>
        observed <= 0 ? 0 : weighted / observed;
}

public sealed class PlayerCountAnalyticsSnapshot
{
    public long GeneratedUnixMilliseconds { get; set; }
    public int CurrentPlayers { get; set; }
    public string ServerTimeZone { get; set; } = "Local";
    public int DailyRetentionDays { get; set; }
    public int HourlyRetentionDays { get; set; }
    public int TodayLow { get; set; }
    public int TodayHigh { get; set; }
    public double TodayAverage { get; set; }
    public List<PlayerCountDailyPoint> Daily { get; set; } = new();
    public List<PlayerCountHourlyPoint> TodayHourly { get; set; } = new();
    public List<PlayerCountHourlyPoint> Last24Hours { get; set; } = new();
}

public sealed class PlayerCountDailyPoint
{
    public string Date { get; set; } = "";
    public int Low { get; set; }
    public int High { get; set; }
    public double Average { get; set; }
    public double ObservedMinutes { get; set; }
}

public sealed class PlayerCountHourlyPoint
{
    public string Date { get; set; } = "";
    public int Hour { get; set; }
    public string Label { get; set; } = "";
    public double? Average { get; set; }
    public int? Low { get; set; }
    public int? High { get; set; }
    public double ObservedMinutes { get; set; }
}

public abstract class PlayerCountAccumulator
{
    public bool HasObservation { get; set; }
    public int Low { get; set; }
    public int High { get; set; }
    public long ObservedMilliseconds { get; set; }
    public double WeightedPlayerMilliseconds { get; set; }
}

public sealed class PlayerCountDailyAccumulator : PlayerCountAccumulator
{
    public string Date { get; set; } = "";
}

public sealed class PlayerCountHourlyAccumulator : PlayerCountAccumulator
{
    public string Date { get; set; } = "";
    public int Hour { get; set; }
}

public sealed class PlayerCountAnalyticsState
{
    public int SchemaVersion { get; set; } = 1;
    public List<PlayerCountDailyAccumulator> Daily { get; set; } = new();
    public List<PlayerCountHourlyAccumulator> Hourly { get; set; } = new();

    public PlayerCountAnalyticsState Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Daily = Daily.Select(x => new PlayerCountDailyAccumulator
        {
            Date = x.Date,
            HasObservation = x.HasObservation,
            Low = x.Low,
            High = x.High,
            ObservedMilliseconds = x.ObservedMilliseconds,
            WeightedPlayerMilliseconds = x.WeightedPlayerMilliseconds
        }).ToList(),
        Hourly = Hourly.Select(x => new PlayerCountHourlyAccumulator
        {
            Date = x.Date,
            Hour = x.Hour,
            HasObservation = x.HasObservation,
            Low = x.Low,
            High = x.High,
            ObservedMilliseconds = x.ObservedMilliseconds,
            WeightedPlayerMilliseconds = x.WeightedPlayerMilliseconds
        }).ToList()
    };
}
