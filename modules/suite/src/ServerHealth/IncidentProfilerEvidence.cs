namespace BlockLogger.ServerHealth;

public sealed class ServerProfileSlowTick
{
    public long UnixMilliseconds { get; set; }
    public double TickMs { get; set; }
    public string TopCauseSummary { get; set; } = "";
    public string LeadingMarker { get; set; } = "";
    public double LeadingWorkMs { get; set; }
    public string ModId { get; set; } = "";
    public string ModName { get; set; } = "";
    public string Confidence { get; set; } = "Low";
    public bool IsCore { get; set; }
}

public sealed class IncidentProfilerEvidence
{
    public string State { get; set; } = "Not captured";
    public string Note { get; set; } = "No retained engine tick profile covers this spike. Lightweight counters cannot identify the executing code.";
    public long CompletedUnixMilliseconds { get; set; }
    public string CaptureKind { get; set; } = "";
    public List<ServerProfileSlowTick> Ticks { get; set; } = new();
    public bool HasMeasuredTicks => Ticks is { Count: > 0 };
}

internal static class IncidentProfilerMatcher
{
    public static IncidentProfilerEvidence Match(IEnumerable<ServerProfileSlowTick> ticks, long start, long end,
        double delayMs, long completed, string kind, long trigger)
    {
        var result = new IncidentProfilerEvidence();
        if (start <= 0 || end < start || !double.IsFinite(delayMs) || delayMs < 0 || completed <= 0) return result;
        // Scheduler samples describe the preceding interval, not just a point.
        // Tick timestamps are profiler-observation times; permit actual interval
        // overlap only, rather than matching any profile in a broad ±15s window.
        long first = start - 1000 - (long)Math.Min(delayMs, 3_600_000);
        result.Ticks = ticks.Where(t => double.IsFinite(t.TickMs) && t.TickMs >= 50 && t.TickMs < 60_000 &&
                t.UnixMilliseconds >= first && t.UnixMilliseconds - (long)t.TickMs <= end)
            .OrderByDescending(t => t.TickMs).Take(8).ToList();
        bool followup = kind == "Automatic lag incident" && trigger >= first && trigger <= end;
        if (result.HasMeasuredTicks || followup)
        {
            result.CompletedUnixMilliseconds = completed; result.CaptureKind = kind;
            result.State = result.HasMeasuredTicks ? "Measured tick overlap" : "After-spike capture";
            result.Note = result.HasMeasuredTicks
                ? "Sampled slow-tick intervals overlap this spike's scheduler interval. Leading markers show measured contributors, not proof that one mod caused the entire stall. Observation timestamps and sampling have limited precision."
                : "The automatic profile was triggered by this spike but sampled subsequent work. It did not capture the original slow tick and cannot establish its cause.";
        }
        return result;
    }

    internal static int GcScore(double pauseMs, int collections, double delayMs = 0)
    {
        if (delayMs > 0 && pauseMs < Math.Max(50, delayMs * .25)) return collections > 0 || pauseMs > 0 ? 15 : 0;
        return pauseMs >= 250 ? 75 : pauseMs >= 100 ? 60 : pauseMs >= 50 ? 40 : collections > 0 ? 15 : 0;
    }
}
