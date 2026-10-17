using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlockLogger.Services;
using Vintagestory.API.Server;

namespace BlockLogger.WebDashboard;

/// <summary>
/// Low-overhead tracker for Vintage Story's standard block-entity tick exception log line.
/// It consumes lines already read by LogIntelligenceService; it does not add a second log tail,
/// world scan, per-tick callback, or Harmony patch.
/// </summary>
public sealed class BadBlockEntityTracker
{
    private const int MaxEntries = 500;
    private const int MaxText = 600;

    private static readonly Regex TickErrorRegex = new(
        @"At position\s+(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s+for block\s+(\S+)\s+a\s+(\S+)\s+threw an error when ticked",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ExceptionRegex = new(
        @"Exception:\s*([^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex StackRegex = new(
        @"(?:^|\n)\s*at\s+([^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object sync = new();
    private readonly Dictionary<string, BadBlockEntityRecord> entries = new(StringComparer.Ordinal);
    private readonly string statePath;

    public BadBlockEntityTracker(ICoreServerAPI api)
    {
        string diagnosticsRoot = Path.Combine(SuiteDataPaths.GetWorldRoot(api), "diagnostics");
        Directory.CreateDirectory(diagnosticsRoot);
        statePath = Path.Combine(diagnosticsRoot, "bad-block-entities.json");
        LoadState();
    }

    public void Observe(string line, string context, bool archived, long unixMilliseconds)
    {
        // Archived log scans should never resurrect old world positions into the live action queue.
        if (archived || string.IsNullOrWhiteSpace(line)) return;

        var match = TickErrorRegex.Match(line);
        if (!match.Success) return;
        if (!int.TryParse(match.Groups[1].Value, out int x) ||
            !int.TryParse(match.Groups[2].Value, out int y) ||
            !int.TryParse(match.Groups[3].Value, out int z)) return;

        string blockCode = Trim(match.Groups[4].Value, 240);
        string blockEntityType = Trim(match.Groups[5].Value, 240).TrimEnd(':');
        const int dimension = 0; // The engine's standard tick-error log line does not include a dimension.
        string key = BuildKey(x, y, z, dimension, blockCode, blockEntityType);
        string exception = Extract(ExceptionRegex, context, "Unknown exception");
        string topFrame = Extract(StackRegex, context, "");

        lock (sync)
        {
            if (!entries.TryGetValue(key, out var entry))
            {
                EnsureCapacityUnsafe();
                entry = new BadBlockEntityRecord
                {
                    Key = key,
                    X = x,
                    Y = y,
                    Z = z,
                    Dimension = dimension,
                    BlockCode = blockCode,
                    BlockEntityType = blockEntityType,
                    FirstSeenUnixMilliseconds = unixMilliseconds,
                    LastSeenUnixMilliseconds = unixMilliseconds,
                    Tracking = true
                };
                entries[key] = entry;
            }

            // Stop Tracking deliberately freezes the entry until an administrator resumes it.
            if (!entry.Tracking) return;

            entry.Occurrences++;
            entry.FirstSeenUnixMilliseconds = entry.FirstSeenUnixMilliseconds <= 0
                ? unixMilliseconds
                : Math.Min(entry.FirstSeenUnixMilliseconds, unixMilliseconds);
            entry.LastSeenUnixMilliseconds = Math.Max(entry.LastSeenUnixMilliseconds, unixMilliseconds);
            entry.LastException = Trim(exception, MaxText);
            entry.LastTopFrame = Trim(topFrame, MaxText);
        }
    }

    public BadBlockEntitySnapshot GetSnapshot()
    {
        lock (sync)
        {
            var rows = new List<BadBlockEntityRecord>(entries.Count);
            int tracking = 0;
            long totalOccurrences = 0;
            foreach (var value in entries.Values)
            {
                rows.Add(Clone(value));
                if (value.Tracking) tracking++;
                totalOccurrences += value.Occurrences;
            }
            rows.Sort(static (a, b) =>
            {
                int c = b.Tracking.CompareTo(a.Tracking);
                if (c != 0) return c;
                c = b.LastSeenUnixMilliseconds.CompareTo(a.LastSeenUnixMilliseconds);
                return c != 0 ? c : b.Occurrences.CompareTo(a.Occurrences);
            });

            return new BadBlockEntitySnapshot
            {
                GeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Total = rows.Count,
                Tracking = tracking,
                Stopped = rows.Count - tracking,
                TotalOccurrences = totalOccurrences,
                Entries = rows
            };
        }
    }

    /// <summary>
    /// Returns only recently active repeated block-entity faults for the Web Console alert strip.
    /// This is a view over already-tracked records and adds no new scanner/timer.
    /// </summary>
    public BadBlockEntitySnapshot GetConsoleAlertSnapshot(int minimumOccurrences = 3, long recentWindowMilliseconds = 300_000)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long cutoff = now - Math.Max(15_000, recentWindowMilliseconds);
        minimumOccurrences = Math.Max(2, minimumOccurrences);

        lock (sync)
        {
            var rows = new List<BadBlockEntityRecord>();
            long totalOccurrences = 0;
            foreach (var value in entries.Values)
            {
                if (!value.Tracking || value.Occurrences < minimumOccurrences || value.LastSeenUnixMilliseconds < cutoff) continue;
                rows.Add(Clone(value));
                totalOccurrences += value.Occurrences;
            }
            rows.Sort(static (a, b) =>
            {
                int c = b.LastSeenUnixMilliseconds.CompareTo(a.LastSeenUnixMilliseconds);
                return c != 0 ? c : b.Occurrences.CompareTo(a.Occurrences);
            });

            return new BadBlockEntitySnapshot
            {
                GeneratedUnixMilliseconds = now,
                Total = rows.Count,
                Tracking = rows.Count,
                Stopped = 0,
                TotalOccurrences = totalOccurrences,
                Entries = rows
            };
        }
    }

    public bool TryGet(string key, out BadBlockEntityRecord record)
    {
        lock (sync)
        {
            if (entries.TryGetValue(key ?? "", out var found))
            {
                record = Clone(found);
                return true;
            }
        }

        record = new BadBlockEntityRecord();
        return false;
    }

    public bool SetTracking(string key, bool tracking, string actor, out string message)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(key ?? "", out var entry))
            {
                message = "Tracked block entity was not found.";
                return false;
            }

            entry.Tracking = tracking;
            entry.LastAction = tracking ? "Tracking resumed" : "Tracking stopped";
            entry.LastActionBy = Trim(actor, 120);
            entry.LastActionUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            message = tracking ? "Block entity tracking resumed." : "Block entity tracking stopped.";
        }

        SaveState();
        return true;
    }

    public void RecordAction(string key, string actor, string action, string detail)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(key ?? "", out var entry)) return;
            entry.LastAction = Trim(string.IsNullOrWhiteSpace(detail) ? action : $"{action}: {detail}", MaxText);
            entry.LastActionBy = Trim(actor, 120);
            entry.LastActionUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        SaveState();
    }

    public int ClearAll(string actor, out string message)
    {
        int removed;
        lock (sync)
        {
            removed = entries.Count;
            entries.Clear();
        }

        SaveState();
        message = removed == 0
            ? "Bad Block Entity Tracker is already empty."
            : $"Removed {removed} tracked block-entity record(s). World blocks and block entities were not modified.";
        return removed;
    }

    public void SaveState()
    {
        try
        {
            BadBlockEntityPersistedState state;
            lock (sync)
            {
                state = new BadBlockEntityPersistedState
                {
                    Entries = entries.Values
                        .OrderByDescending(x => x.LastSeenUnixMilliseconds)
                        .Take(MaxEntries)
                        .Select(Clone)
                        .ToList()
                };
            }

            string temp = statePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, statePath, true);
        }
        catch
        {
            // Diagnostics persistence must never destabilize the game server.
        }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(statePath)) return;
            var state = JsonSerializer.Deserialize<BadBlockEntityPersistedState>(File.ReadAllText(statePath), JsonOptions);
            if (state == null) return;

            lock (sync)
            {
                entries.Clear();
                foreach (var entry in state.Entries
                             .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                             .OrderByDescending(x => x.LastSeenUnixMilliseconds)
                             .Take(MaxEntries))
                    entries[entry.Key] = Clone(entry);
            }
        }
        catch
        {
            // A corrupt diagnostics cache should simply start fresh.
        }
    }

    private void EnsureCapacityUnsafe()
    {
        if (entries.Count < MaxEntries) return;

        string? evict = entries.Values
            .OrderBy(x => x.Tracking ? 1 : 0)
            .ThenBy(x => x.LastSeenUnixMilliseconds)
            .Select(x => x.Key)
            .FirstOrDefault();
        if (evict != null) entries.Remove(evict);
    }

    private static string BuildKey(int x, int y, int z, int dimension, string blockCode, string blockEntityType) =>
        $"{dimension}:{x},{y},{z}|{blockCode}|{blockEntityType}";

    private static string Extract(Regex regex, string text, string fallback)
    {
        var match = regex.Match(text ?? "");
        return match.Success ? match.Groups[1].Value.Trim() : fallback;
    }

    private static string Trim(string? value, int max)
    {
        string text = (value ?? "").Trim();
        return text.Length <= max ? text : text[..max];
    }

    private static BadBlockEntityRecord Clone(BadBlockEntityRecord x) => new()
    {
        Key = x.Key,
        X = x.X,
        Y = x.Y,
        Z = x.Z,
        Dimension = x.Dimension,
        BlockCode = x.BlockCode,
        BlockEntityType = x.BlockEntityType,
        Occurrences = x.Occurrences,
        FirstSeenUnixMilliseconds = x.FirstSeenUnixMilliseconds,
        LastSeenUnixMilliseconds = x.LastSeenUnixMilliseconds,
        LastException = x.LastException,
        LastTopFrame = x.LastTopFrame,
        Tracking = x.Tracking,
        LastAction = x.LastAction,
        LastActionBy = x.LastActionBy,
        LastActionUnixMilliseconds = x.LastActionUnixMilliseconds
    };
}

public sealed class BadBlockEntitySnapshot
{
    public long GeneratedUnixMilliseconds { get; set; }
    public int Total { get; set; }
    public int Tracking { get; set; }
    public int Stopped { get; set; }
    public long TotalOccurrences { get; set; }
    public List<BadBlockEntityRecord> Entries { get; set; } = new();
}

public sealed class BadBlockEntityRecord
{
    public string Key { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public int Dimension { get; set; }
    public string BlockCode { get; set; } = "";
    public string BlockEntityType { get; set; } = "";
    public long Occurrences { get; set; }
    public long FirstSeenUnixMilliseconds { get; set; }
    public long LastSeenUnixMilliseconds { get; set; }
    public string LastException { get; set; } = "";
    public string LastTopFrame { get; set; } = "";
    public bool Tracking { get; set; } = true;
    public string LastAction { get; set; } = "";
    public string LastActionBy { get; set; } = "";
    public long LastActionUnixMilliseconds { get; set; }
}

public sealed class BadBlockEntityPersistedState
{
    public List<BadBlockEntityRecord> Entries { get; set; } = new();
}

public sealed class BadBlockEntityActionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public BadBlockEntitySnapshot Snapshot { get; set; } = new();
    public object? Detail { get; set; }
}
