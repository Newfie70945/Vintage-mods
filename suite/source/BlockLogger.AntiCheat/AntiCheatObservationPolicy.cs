using System;

namespace BlockLogger.AntiCheat;

internal static class AntiCheatObservationPolicy
{
	internal static bool IsReviewOnly(string? code)
	{
		return string.Equals(code, "PACKET_FLIGHT_REVIEW", StringComparison.OrdinalIgnoreCase);
	}
}
