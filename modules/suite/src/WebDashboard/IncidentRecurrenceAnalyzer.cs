using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.WebDashboard;

public sealed class IncidentRecurrenceFinding
{
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Confidence { get; set; } = "Low";
    public int Score { get; set; }
    public int IncidentCount { get; set; }
    public string Evidence { get; set; } = "";
    public string NextAction { get; set; } = "";
}

public sealed class IncidentRecurrenceReport
{
    public int AnalyzedIncidents { get; set; }
    public long OldestUnixMilliseconds { get; set; }
    public long NewestUnixMilliseconds { get; set; }
    public List<IncidentRecurrenceFinding> Findings { get; set; } = new();
    public string Note { get; set; } =
        "Cross-incident patterns are computed from retained archives only. Evicted or uncaptured incidents are invisible here, and a pattern shows what repeats, not why.";
}

// The per-incident verdict judges one spike at a time and the adaptive analysis only sees a few
// minutes. This looks across the retained archive for what keeps coming back: a steady interval
// (a scheduled job), a save overlap, one repeated assessment, one repeatedly measured mod, or a
// worsening trend. It never names a cause on its own; it says what repeats and what to compare.
public static class IncidentRecurrenceAnalyzer
{
    private const int MinIncidents = 3;
    private const long SameEpisodeGapMs = 30_000;
    private const long MinIntervalMs = 120_000;
    private const double IntervalTolerance = 0.20;
    private static readonly int[] CommonScheduleMinutes = { 5, 10, 15, 20, 30, 60, 120, 180, 240, 360, 720, 1440 };

    public static IncidentRecurrenceReport Analyze(IReadOnlyList<PerformanceIncidentArchive>? incidents)
    {
        var report = new IncidentRecurrenceReport();
        if (incidents == null || incidents.Count == 0) return report;

        var ordered = incidents.Where(x => x != null && x.StartUnixMilliseconds > 0)
            .OrderBy(x => x.StartUnixMilliseconds).ToList();
        report.AnalyzedIncidents = ordered.Count;
        if (ordered.Count == 0) return report;
        report.OldestUnixMilliseconds = ordered[0].StartUnixMilliseconds;
        report.NewestUnixMilliseconds = ordered[^1].StartUnixMilliseconds;
        if (ordered.Count < MinIncidents) return report;

        AddIntervalFinding(report, ordered);
        AddSaveFinding(report, ordered);
        AddCategoryFinding(report, ordered);
        AddModFinding(report, ordered);
        AddTrendFinding(report, ordered);

        report.Findings = report.Findings.OrderByDescending(x => x.Score).ThenBy(x => x.Kind, StringComparer.Ordinal).ToList();
        return report;
    }

    private static void AddIntervalFinding(IncidentRecurrenceReport report, List<PerformanceIncidentArchive> ordered)
    {
        // Gaps shorter than SameEpisodeGapMs belong to one slowdown that was split in two.
        var gaps = new List<double>();
        for (int i = 1; i < ordered.Count; i++)
        {
            long gap = ordered[i].StartUnixMilliseconds - ordered[i - 1].StartUnixMilliseconds;
            if (gap >= SameEpisodeGapMs) gaps.Add(gap);
        }
        if (gaps.Count < 3) return;

        double median = Median(gaps);
        if (median < MinIntervalMs) return;
        double tolerance = Math.Max(median * IntervalTolerance, 30_000);
        int consistent = gaps.Count(g => Math.Abs(g - median) <= tolerance);
        if (consistent < 3 || consistent < Math.Ceiling(gaps.Count * 0.7)) return;

        double share = consistent / (double)gaps.Count;
        double minutes = median / 60_000d;
        int? schedule = CommonScheduleMinutes.Select(m => (int?)m)
            .FirstOrDefault(m => Math.Abs(m!.Value - minutes) <= Math.Max(1, m.Value * 0.1));

        string confidence = consistent >= 6 && share >= 0.85 ? "High" : consistent >= 4 && share >= 0.75 ? "Medium" : "Low";
        report.Findings.Add(new IncidentRecurrenceFinding
        {
            Kind = "Interval",
            Title = $"Lag recurs about every {FormatMinutes(minutes)}",
            Confidence = confidence,
            Score = Math.Min(95, 40 + consistent * 7),
            IncidentCount = ordered.Count,
            Evidence = $"{consistent} of {gaps.Count} gaps between incident starts fall within ±{tolerance / 60_000d:0.#} min of the median {FormatMinutes(minutes)}." +
                (schedule.HasValue ? $" That is close to a {FormatMinutes(schedule.Value)} cycle." : "") +
                " Missed or evicted incidents would break this pattern, so a weaker match is not evidence against a schedule.",
            NextAction = "A steady interval usually points at a timed job: autosave or backup, a restart or announcement timer, a scheduled mod task, or an external process on the host. Line the incident start times up against those schedules."
        });
    }

    private static void AddSaveFinding(IncidentRecurrenceReport report, List<PerformanceIncidentArchive> ordered)
    {
        var overlapping = ordered.Where(x => x.WorldSaveWindowMs > 0).ToList();
        double ratio = overlapping.Count / (double)ordered.Count;
        if (overlapping.Count < MinIncidents || ratio < 0.6) return;

        report.Findings.Add(new IncidentRecurrenceFinding
        {
            Kind = "Save",
            Title = "Lag keeps overlapping world saves",
            Confidence = ratio >= 0.8 && overlapping.Count >= 5 ? "Medium" : "Low",
            Score = Math.Min(85, 30 + (int)Math.Round(ratio * 40) + Math.Min(15, overlapping.Count * 2)),
            IncidentCount = overlapping.Count,
            Evidence = $"{overlapping.Count} of {ordered.Count} incidents overlapped a measured world-save window (median {Median(overlapping.Select(x => x.WorldSaveWindowMs)):0} ms). " +
                "The window includes main-thread callback scheduling, so overlap does not isolate serialization, disk writes or save listeners.",
            NextAction = "Compare lag length with save length across saves, check disk latency during saves, and look for mods that do heavy work in save listeners."
        });
    }

    private static void AddCategoryFinding(IncidentRecurrenceReport report, List<PerformanceIncidentArchive> ordered)
    {
        var top = ordered
            .Where(x => !string.IsNullOrWhiteSpace(x.PrimaryCategory) &&
                        !string.Equals(x.PrimaryCategory, "Insufficient evidence", StringComparison.Ordinal))
            .GroupBy(x => x.PrimaryCategory, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (top == null) return;
        int count = top.Count();
        double share = count / (double)ordered.Count;
        if (count < MinIncidents || share < 0.5) return;

        var evidenceKinds = top.Select(x => x.AssessmentStatus).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        report.Findings.Add(new IncidentRecurrenceFinding
        {
            Kind = "Category",
            Title = $"The same assessment keeps recurring: {top.Key}",
            Confidence = share >= 0.7 && count >= 5 ? "Medium" : "Low",
            Score = Math.Min(80, 25 + (int)Math.Round(share * 35) + Math.Min(15, count * 2)),
            IncidentCount = count,
            Evidence = $"{count} of {ordered.Count} retained incidents ({share:P0}) were assessed as \"{top.Key}\"" +
                (evidenceKinds.Count > 0 ? $" (assessment status: {string.Join(", ", evidenceKinds)})." : ".") +
                " A repeated assessment is a pattern, not proof of one shared cause.",
            NextAction = "Open two of these incidents side by side and compare their intervals, tick markers and context; if they agree, capture an Incident + Profile during the next one."
        });
    }

    private static void AddModFinding(IncidentRecurrenceReport report, List<PerformanceIncidentArchive> ordered)
    {
        // Only profiler-measured ownership (Medium/High) counts; unowned or guessed rows are ignored.
        var top = ordered
            .Where(x => !string.IsNullOrWhiteSpace(x.LikelyModId) &&
                        (x.ModConfidence == "Medium" || x.ModConfidence == "High") &&
                        x.ProfileEvidence?.Ticks?.Any(t => !t.IsCore &&
                            string.Equals(t.ModId, x.LikelyModId, StringComparison.OrdinalIgnoreCase) &&
                            (t.Confidence == "Medium" || t.Confidence == "High") &&
                            double.IsFinite(t.LeadingWorkMs) &&
                            BlockLogger.ServerHealth.IncidentProfilerMatcher.WorkScore(t.LeadingWorkMs, x.WorstDelayMs) >= 40) == true)
            .GroupBy(x => x.LikelyModId, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (top == null) return;
        int count = top.Count();
        if (count < 2) return;

        double share = count / (double)ordered.Count;
        string name = top.Select(x => x.LikelyModName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? top.Key;
        report.Findings.Add(new IncidentRecurrenceFinding
        {
            Kind = "Mod",
            Title = $"{name} keeps leading measured tick work",
            Confidence = count >= 3 && share >= 0.4 ? "High" : "Medium",
            Score = Math.Min(98, 50 + count * 8 + (int)Math.Round(share * 15)),
            IncidentCount = count,
            Evidence = $"{count} of {ordered.Count} incidents had engine-profiler markers owned by \"{top.Key}\" at Medium or High confidence (worst delay across them {top.Max(x => x.WorstDelayMs):0} ms). " +
                "Marker ownership shows where profiled time went; it does not prove a defect in the mod.",
            NextAction = $"Check {name} for updates and known issues, review its config for heavy periodic work, and compare incident times with what that mod does on a timer."
        });
    }

    private static void AddTrendFinding(IncidentRecurrenceReport report, List<PerformanceIncidentArchive> ordered)
    {
        if (ordered.Count < 6) return;
        int half = ordered.Count / 2;
        var older = ordered.Take(half).Select(x => x.WorstDelayMs).ToList();
        var newer = ordered.Skip(ordered.Count - half).Select(x => x.WorstDelayMs).ToList();
        double olderMedian = Median(older), newerMedian = Median(newer);
        if (newerMedian < 100 || olderMedian <= 0 || newerMedian < olderMedian * 1.5) return;

        // Frequency over the same two halves: a faster cadence is as telling as a bigger spike.
        long olderSpan = Math.Max(1, ordered[half - 1].StartUnixMilliseconds - ordered[0].StartUnixMilliseconds);
        long newerSpan = Math.Max(1, ordered[^1].StartUnixMilliseconds - ordered[ordered.Count - half].StartUnixMilliseconds);
        bool morefrequent = newerSpan < olderSpan * 0.75;

        report.Findings.Add(new IncidentRecurrenceFinding
        {
            Kind = "Trend",
            Title = "Lag spikes are getting worse",
            Confidence = ordered.Count >= 10 ? "Medium" : "Low",
            Score = Math.Min(90, 35 + (int)Math.Min(40, (newerMedian / olderMedian - 1) * 25) + Math.Min(15, ordered.Count)),
            IncidentCount = ordered.Count,
            Evidence = $"Median worst delay rose from {olderMedian:0} ms (older {older.Count} incidents) to {newerMedian:0} ms (newest {newer.Count})" +
                (morefrequent ? ", and the newest incidents arrive closer together." : "."),
            NextAction = "Check what changed over this period: new or updated mods, world growth (entities, chunks), player count, or rising memory use."
        });
    }

    private static string FormatMinutes(double minutes) =>
        minutes >= 120 ? $"{minutes / 60d:0.#} h" : $"{minutes:0.#} min";

    private static double Median(IEnumerable<double> source)
    {
        var values = source.OrderBy(x => x).ToArray();
        if (values.Length == 0) return 0;
        int mid = values.Length / 2;
        return values.Length % 2 == 0 ? (values[mid - 1] + values[mid]) / 2d : values[mid];
    }
}
