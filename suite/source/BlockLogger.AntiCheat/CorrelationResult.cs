using System;
using System.Collections.Generic;
using NewfiesAntiCheat.Shared;

namespace BlockLogger.AntiCheat;

internal readonly record struct CorrelationResult(bool Active, int RiskBonus, int Confidence, AntiCheatSeverity Severity, IReadOnlyList<string> Categories, IReadOnlyList<string> Domains, string Evidence)
{
	public static CorrelationResult None => new CorrelationResult(Active: false, 0, 0, AntiCheatSeverity.Info, Array.Empty<string>(), Array.Empty<string>(), "");
}
