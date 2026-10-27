namespace BlockLogger.AntiCheat;

// One confirmed detector report per ten seconds, three in a rolling minute.
// Multiple detectors, packet count, mounts and low-TPS observations cannot prime it.
internal sealed class ConfirmedSpeedRisk
{
    internal static bool Eligible(string? code, int severity, string? evidence) =>
        string.Equals(code,"PACKET_SPEED_HACK", StringComparison.OrdinalIgnoreCase) && severity == 4 &&
        (";" + evidence + ";").Contains(";speedEscalationEligible=True;", StringComparison.Ordinal);
    private readonly Queue<long> observations = new();
    internal int Observe(long now)
    {
        Prune(now);
        if (observations.Count == 0 || now - observations.Last() >= 10_000)
        {
            if (observations.Count == 3) observations.Dequeue();
            observations.Enqueue(now);
        }
        return observations.Count;
    }
    internal int Count(long now) { Prune(now); return observations.Count; }
    internal int Bonus(long now, int increment) => Math.Clamp((Count(now) - 1) * increment, 0, 50);
    private void Prune(long now)
    {
        if (observations.Count > 0 && now < observations.Last()) observations.Clear();
        while (observations.Count > 0 && now - observations.Peek() >= 60_000) observations.Dequeue();
    }
}
