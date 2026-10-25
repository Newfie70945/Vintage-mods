using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.ServerHealth;

public sealed class SlowTickVerdictCount
{
    public string Verdict { get; set; } = "";
    public int Count { get; set; }
    public double Percent { get; set; }
}

public sealed class SlowTickOwnerRollup
{
    public string Owner { get; set; } = "";
    public string Kind { get; set; } = "";
    public string ModId { get; set; } = "";
    public int Ticks { get; set; }
    public double SharePercent { get; set; }
    public double TotalLeadingMs { get; set; }
    public double WorstTickMs { get; set; }
    public string TopMarker { get; set; } = "";
}

public sealed class SlowTickSummary
{
    public int TotalTicks { get; set; }
    public double TotalTickMs { get; set; }
    public double WorstTickMs { get; set; }
    public long WorstTickUnixMilliseconds { get; set; }
    public string Headline { get; set; } = "";
    public List<SlowTickVerdictCount> Verdicts { get; set; } = new();
    public List<SlowTickOwnerRollup> Owners { get; set; } = new();
}

// Turns a sampled slow tick (its duration, measured leaf markers and GC pause) into a judgement of
// what kind of cause it was and a plain sentence saying why. It only interprets measured values:
// a marker's owner is where profiled time was spent, not proof of a defect.
public static class SlowTickAnalyzer
{
    public const string Dominant = "Single dominant cause";
    public const string Several = "Several contributors";
    public const string GcPause = "GC pause";
    public const string Unmarked = "Mostly unmarked time";
    public const string Spread = "Spread across many markers";

    private const double DominantSharePercent = 60;
    private const double SeveralShare = 0.7;
    private const double UnmarkedShare = 0.5;
    private const double GcShare = 0.4;
    private const double GcMinimumMs = 20;

    public static void Annotate(ServerProfileSlowTick tick)
    {
        if (tick == null) return;
        double total = tick.TickMs;
        if (!double.IsFinite(total) || total <= 0)
        {
            tick.Verdict = "";
            tick.Why = "";
            return;
        }

        double instrumented = Math.Clamp(double.IsFinite(tick.InstrumentedLeafMs) ? tick.InstrumentedLeafMs : 0, 0, total);
        double leadingMs = double.IsFinite(tick.LeadingWorkMs) ? Math.Max(0, tick.LeadingWorkMs) : 0;
        double gcMs = double.IsFinite(tick.GcPauseMs) ? Math.Max(0, tick.GcPauseMs) : 0;
        tick.UnaccountedMs = Math.Max(0, total - instrumented);
        tick.LeadingSharePercent = Math.Clamp(leadingMs / total * 100d, 0, 100);

        if (tick.IsCore)
        {
            tick.OwnerKind = "Engine";
            tick.Owner = "Vintage Story engine";
        }
        else if (!string.IsNullOrWhiteSpace(tick.ModId) && tick.Confidence != "Low")
        {
            tick.OwnerKind = "Mod";
            tick.Owner = string.IsNullOrWhiteSpace(tick.ModName) ? tick.ModId : tick.ModName;
        }
        else
        {
            tick.OwnerKind = "Unattributed";
            tick.Owner = "Unattributed";
        }

        string ownerPhrase = tick.OwnerKind switch
        {
            "Engine" => "the Vintage Story engine",
            "Mod" => $"{tick.Owner} ({tick.Confidence} confidence)",
            _ => "no identified mod"
        };
        string marker = Shorten(tick.LeadingMarker);
        string lead = $"{marker} ({leadingMs:0.0} ms, {tick.LeadingSharePercent:0}% of the tick)";
        var contributors = tick.Contributors ?? new List<IncidentTickContributor>();
        double topThree = contributors.OrderByDescending(c => c.WorkMs).Take(3).Sum(c => Math.Max(0, c.WorkMs));

        if (gcMs >= GcMinimumMs && gcMs / total >= GcShare)
        {
            tick.Verdict = GcPause;
            tick.Why = $"A garbage-collection pause of {gcMs:0.0} ms ({Math.Min(1, gcMs / total):P0} of the tick) overlapped this tick." +
                (tick.LeadingSharePercent >= 50 && marker.Length > 0
                    ? $" The leading marker {lead} may be inflated by it, so do not blame {tick.Owner} for this tick on its own."
                    : $" Marked code accounts for only {instrumented:0.0} ms.");
        }
        else if (marker.Length > 0 && tick.LeadingSharePercent >= DominantSharePercent)
        {
            tick.Verdict = Dominant;
            tick.Why = $"{lead} dominated this tick, owned by {ownerPhrase}.";
        }
        else if (contributors.Count >= 2 && topThree / total >= SeveralShare)
        {
            tick.Verdict = Several;
            tick.Why = $"The top {Math.Min(3, contributors.Count)} markers made up {topThree / total:P0} of the tick: " +
                string.Join(", ", contributors.OrderByDescending(c => c.WorkMs).Take(3).Select(c => $"{Shorten(c.Marker)} ({c.WorkMs:0.0} ms)")) + ".";
        }
        else if (tick.UnaccountedMs / total >= UnmarkedShare)
        {
            tick.Verdict = Unmarked;
            tick.Why = $"{tick.UnaccountedMs:0.0} ms ({tick.UnaccountedMs / total:P0}) of this tick is not covered by any profiler marker; marked work was only {instrumented:0.0} ms. " +
                "That is typically engine work, waits (disk, locks, network) or a mod that adds no markers.";
        }
        else
        {
            tick.Verdict = Spread;
            tick.Why = marker.Length > 0
                ? $"Time was spread over many small markers; the largest was {lead}."
                : "No leaf profiler markers were exposed for this tick.";
        }
    }

    public static SlowTickSummary Summarize(IReadOnlyList<ServerProfileSlowTick>? ticks)
    {
        var summary = new SlowTickSummary();
        var list = (ticks ?? Array.Empty<ServerProfileSlowTick>())
            .Where(t => t != null && double.IsFinite(t.TickMs) && t.TickMs > 0).ToList();
        if (list.Count == 0)
        {
            summary.Headline = "No slow ticks have been sampled yet. Run Incident Capture or a manual profile during a slowdown.";
            return summary;
        }

        foreach (var tick in list.Where(t => string.IsNullOrEmpty(t.Verdict))) Annotate(tick);

        var worst = list.MaxBy(t => t.TickMs)!;
        summary.TotalTicks = list.Count;
        summary.TotalTickMs = list.Sum(t => t.TickMs);
        summary.WorstTickMs = worst.TickMs;
        summary.WorstTickUnixMilliseconds = worst.UnixMilliseconds;

        summary.Verdicts = list.GroupBy(t => t.Verdict, StringComparer.Ordinal)
            .Select(g => new SlowTickVerdictCount { Verdict = g.Key, Count = g.Count(), Percent = g.Count() * 100d / list.Count })
            .OrderByDescending(v => v.Count).ThenBy(v => v.Verdict, StringComparer.Ordinal).ToList();

        summary.Owners = list.GroupBy(t => (t.OwnerKind ?? "", t.OwnerKind == "Mod" ? t.ModId ?? "" : ""))
            .Select(g =>
            {
                var first = g.First();
                return new SlowTickOwnerRollup
                {
                    Owner = first.Owner,
                    Kind = g.Key.Item1,
                    ModId = g.Key.Item2,
                    Ticks = g.Count(),
                    SharePercent = g.Count() * 100d / list.Count,
                    TotalLeadingMs = g.Sum(t => Math.Max(0, t.LeadingWorkMs)),
                    WorstTickMs = g.Max(t => t.TickMs),
                    TopMarker = Shorten(g.Where(t => !string.IsNullOrEmpty(t.LeadingMarker))
                        .GroupBy(t => t.LeadingMarker, StringComparer.Ordinal)
                        .OrderByDescending(m => m.Count()).ThenByDescending(m => m.Sum(t => t.LeadingWorkMs))
                        .Select(m => m.Key).FirstOrDefault() ?? "")
                };
            })
            .OrderByDescending(o => o.Ticks).ThenByDescending(o => o.TotalLeadingMs).ToList();

        var headline = new List<string>
        {
            $"{list.Count} slow tick(s) were sampled; the worst took {worst.TickMs:0} ms."
        };
        var topOwner = summary.Owners[0];
        headline.Add(topOwner.Kind == "Unattributed"
            ? $"Most could not be tied to a specific owner ({topOwner.Ticks} of {list.Count})."
            : $"{topOwner.Owner} led {topOwner.Ticks} of them ({topOwner.SharePercent:0}%).");
        var gc = summary.Verdicts.FirstOrDefault(v => v.Verdict == GcPause);
        if (gc != null) headline.Add($"{gc.Count} overlapped a GC pause.");
        if (!string.IsNullOrEmpty(worst.Why)) headline.Add("Worst tick: " + worst.Why);
        summary.Headline = string.Join(" ", headline);
        return summary;
    }

    private static string Shorten(string? value)
    {
        value = (value ?? "").Trim();
        return value.Length <= 72 ? value : value[..71] + "…";
    }
}
