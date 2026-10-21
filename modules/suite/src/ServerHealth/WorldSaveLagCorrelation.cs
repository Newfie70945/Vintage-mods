namespace BlockLogger.ServerHealth;

internal sealed record WorldSaveLagWindow(long StartUnixMilliseconds, double ObservedMilliseconds)
{
    internal long EndUnixMilliseconds => StartUnixMilliseconds + (long)ObservedMilliseconds;
}

internal static class WorldSaveLagCorrelation
{
    // A sample describes the main-thread interval preceding its timestamp. Match
    // measured save windows to that interval, never by counts or a broad ± window.
    internal static WorldSaveLagWindow? Match(IEnumerable<WorldSaveLagWindow> windows, long sampleUnix, double delayMs)
    {
        if (!double.IsFinite(delayMs) || delayMs < 150) return null;
        long intervalStart = sampleUnix - 1000 - (long)delayMs;
        return windows.Where(w => double.IsFinite(w.ObservedMilliseconds) && w.ObservedMilliseconds >= 150 &&
                w.StartUnixMilliseconds <= sampleUnix && w.EndUnixMilliseconds >= intervalStart)
            .OrderByDescending(w => w.ObservedMilliseconds).FirstOrDefault();
    }
}
