namespace BlockLogger.AntiCheat;

internal static class AntiCheatObservationPolicy
{
    // A terrain/movement heuristic is useful to investigators but cannot prove
    // cheating in a modded world. Keep it out of all automatic punishment paths.
    internal static bool IsReviewOnly(string? code) =>
        string.Equals(code, "PACKET_FLIGHT_REVIEW", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "PACKET_MINING_CADENCE_UNCERTAIN", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "PACKET_UNVERIFIED_TELEPORT_PATTERN", StringComparison.OrdinalIgnoreCase);

    // Resource rejection and punishment are separate decisions. Legacy observations
    // without confirmation metadata remain review-only after upgrading.
    internal static bool IsReviewOnly(string? code,string? evidence) => IsReviewOnly(code) ||
        (string.Equals(code,"PACKET_FLOOD",StringComparison.OrdinalIgnoreCase) && !HasValue(evidence,"floodConfirmed","True")) ||
        (string.Equals(code,"PACKET_BLOCK_REACH",StringComparison.OrdinalIgnoreCase) &&
            (!HasValue(evidence,"reachEnforcementEligible","True") || HasValue(evidence,"action","use") || HasValue(evidence,"timingRejectSuppressed","True")));

    internal static bool HasValue(string? evidence,string key,string value)
    {
        ReadOnlySpan<char> remaining=evidence.AsSpan();
        while(!remaining.IsEmpty)
        {
            int end=remaining.IndexOf(';');var part=end<0?remaining:remaining[..end];int equals=part.IndexOf('=');
            if(equals>=0 && part[..equals].Equals(key,StringComparison.OrdinalIgnoreCase) && part[(equals+1)..].Equals(value,StringComparison.OrdinalIgnoreCase))return true;
            if(end<0)break;remaining=remaining[(end+1)..];
        }
        return false;
    }
}
