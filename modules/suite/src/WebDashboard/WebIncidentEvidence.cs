using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.WebDashboard;

internal static class WebIncidentEvidence
{
    // The incident timestamp is the delayed sampler's observation. Include its
    // preceding stall interval, not an arbitrary request-start-only proximity.
    internal static long WindowStart(long start, double delay) =>
        start - (long)Math.Clamp(double.IsFinite(delay) ? delay : 0, 0, 3_600_000) - 1000;

    internal static List<WebRequestDiagnostic> Select(IEnumerable<WebRequestDiagnostic> source,
        long start, long end, double delay)
    {
        long from = WindowStart(start, delay);
        return source.Where(x => x.UnixMilliseconds <= end &&
                (x.EndUnixMilliseconds > 0 ? x.EndUnixMilliseconds :
                    x.UnixMilliseconds + (long)Math.Max(0, x.TotalMilliseconds)) >= from)
            .DistinctBy(x => (x.RequestId, x.UnixMilliseconds, x.EndUnixMilliseconds, x.Method, x.Path))
            .OrderByDescending(x => WorkMilliseconds(x, start, end, delay))
            .ThenByDescending(x => x.TotalMilliseconds).Take(8).ToList();
    }

    internal static double WorkMilliseconds(WebRequestDiagnostic request, long start, long end, double delay)
    {
        long from = WindowStart(start, delay);
        // Use the largest recorded callback, discounted for coincident process GC.
        // Do not add overlapping measurements or treat queue time as executing work.
        return (request.WorkWindows ?? new()).Where(w => w.StartUnixMilliseconds <= end && w.EndUnixMilliseconds >= from)
            .Select(w => Math.Max(0, Math.Min(w.WallMilliseconds, Math.Max(0, Math.Min(w.EndUnixMilliseconds, end) - Math.Max(w.StartUnixMilliseconds, from))) - Math.Max(0, w.GcPauseMilliseconds))).DefaultIfEmpty().Max();
    }
}
