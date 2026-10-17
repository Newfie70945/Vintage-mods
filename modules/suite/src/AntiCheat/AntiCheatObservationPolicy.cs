namespace BlockLogger.AntiCheat;

internal static class AntiCheatObservationPolicy
{
    // A terrain/movement heuristic is useful to investigators but cannot prove
    // cheating in a modded world. Keep it out of all automatic punishment paths.
    internal static bool IsReviewOnly(string? code) =>
        string.Equals(code, "PACKET_FLIGHT_REVIEW", StringComparison.OrdinalIgnoreCase);
}
