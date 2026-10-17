using System;
using System.Collections.Generic;
using System.Linq;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Models;

namespace BlockLogger.AntiCheat;

/// <summary>
/// Keeps bounded timestamped anti-cheat occurrences and converts genuinely independent evidence
/// units into a bounded correlation bonus. Occurrences are never refreshed in place: an old high
/// severity therefore expires when its own timestamp leaves the incident window.
/// </summary>
internal sealed class AntiCheatIncidentCorrelator
{
    private readonly Dictionary<string, List<Signal>> byPlayer = new(StringComparer.OrdinalIgnoreCase);

    public void Clear(string? playerUid)
    {
        if (!string.IsNullOrWhiteSpace(playerUid)) byPlayer.Remove(playerUid);
    }

    public void ClearAll() => byPlayer.Clear();

    public void Observe(string playerUid, ScanFindingDto finding, long nowUtc)
    {
        if (string.IsNullOrWhiteSpace(playerUid) || finding == null || finding.Severity <= 0) return;
        if (AntiCheatObservationPolicy.IsReviewOnly(finding.Code)) return;
        if (string.Equals(finding.Scanner, "CorrelationPolicy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(finding.Code, "CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase)) return;

        string category = AntiCheatFindingIntelligence.GetCategory(finding.Code, finding.Scanner);
        if (category is "Other" or "Server Behavior" or "Correlated Incident") return;
        string domain = AntiCheatFindingIntelligence.GetEvidenceDomain(finding.Code, finding.Scanner, category);
        if (string.Equals(domain, "Other", StringComparison.OrdinalIgnoreCase)) return;

        if (!byPlayer.TryGetValue(playerUid, out List<Signal>? list))
        {
            list = new List<Signal>();
            byPlayer[playerUid] = list;
        }

        // Keep each occurrence at its real timestamp. This fixes the old sticky-severity behavior
        // where a later weak repeat could refresh an earlier strong signal indefinitely.
        list.Add(new Signal
        {
            Utc = nowUtc,
            Category = category,
            Domain = domain,
            Code = finding.Code ?? "",
            Scanner = finding.Scanner ?? "",
            Severity = Math.Clamp(finding.Severity, 0, 4),
            Subject = finding.Subject ?? ""
        });

        // A 90-second window normally contains only a handful of rows. This hard cap protects the
        // server from a noisy detector without allowing one category to manufacture independence.
        if (list.Count > 192)
            list.RemoveRange(0, list.Count - 192);
    }

    public CorrelationResult Evaluate(string playerUid, long nowUtc, AntiCheatConfig config)
    {
        if (!config.IncidentCorrelationEnabled || string.IsNullOrWhiteSpace(playerUid)) return CorrelationResult.None;
        if (!byPlayer.TryGetValue(playerUid, out List<Signal>? list)) return CorrelationResult.None;

        long cutoff = nowUtc - config.IncidentCorrelationWindowSeconds * 1000L;
        list.RemoveAll(x => x.Utc < cutoff);
        if (list.Count == 0)
        {
            byPlayer.Remove(playerUid);
            return CorrelationResult.None;
        }

        var latestByCategory = list
            .GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Severity).ThenByDescending(x => x.Utc).First())
            .OrderByDescending(x => x.Severity)
            .ThenBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (latestByCategory.Count < config.IncidentCorrelationRequiredCategories) return CorrelationResult.None;

        // Server-authoritative behavior modalities are independently measured, so Movement, Reach
        // and Mining can each count as one unit. Client-side categories from the same underlying
        // evidence domain count only once (for example multiple static repack/fingerprint labels).
        var independentUnits = latestByCategory
            .GroupBy(x => IndependentUnitKey(x), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Severity).ThenByDescending(x => x.Utc).First())
            .OrderByDescending(x => x.Severity)
            .ToList();

        if (independentUnits.Count < config.IncidentCorrelationRequiredCategories) return CorrelationResult.None;

        bool hasServerBehavior = latestByCategory.Any(x => x.Category is "Movement" or "Reach" or "Mining");
        bool hasStrongIntegrity = latestByCategory.Any(x =>
            (x.Domain is "StaticIntegrity" or "ClientIntegrity" or "RuntimeIntegrity" or "PatchOwnership") &&
            x.Severity >= (int)AntiCheatSeverity.High);

        // Two client-side categories alone are still review context rather than an automatic
        // correlated incident. With only two categories require one server-authoritative behavior
        // signal; broader three+ evidence can correlate when it also spans enough independent domains.
        if (latestByCategory.Count == 2 && !hasServerBehavior) return CorrelationResult.None;

        int independentCount = independentUnits.Count;
        int bonus = independentCount switch
        {
            >= 5 => 20,
            4 => 16,
            3 => 11,
            _ => 6
        };
        if (hasServerBehavior && hasStrongIntegrity) bonus += 4;
        bonus = Math.Clamp(bonus, 0, config.IncidentCorrelationMaxRiskBonus);
        if (bonus <= 0) return CorrelationResult.None;

        int confidence = 45 + (independentCount - 2) * 14 + independentUnits.Sum(x => Math.Max(0, x.Severity - 1) * 4);
        if (hasServerBehavior && hasStrongIntegrity) confidence += 10;
        confidence = Math.Clamp(confidence, 0, 99);

        string[] categoryNames = latestByCategory.Select(x => x.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] domainNames = independentUnits.Select(x => x.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string evidence = $"windowSeconds={config.IncidentCorrelationWindowSeconds};categories={string.Join(",", categoryNames)};" +
                          $"categoryCount={latestByCategory.Count};independentUnits={independentCount};domains={string.Join(",", domainNames)};" +
                          $"riskBonus={bonus};confidence={confidence};" +
                          $"signals={string.Join("|", latestByCategory.Select(x => $"{x.Category}/{x.Domain}:{x.Code}:{x.Severity}"))}";

        AntiCheatSeverity severity = confidence >= 90 ? AntiCheatSeverity.Critical
            : confidence >= 75 ? AntiCheatSeverity.High
            : confidence >= 55 ? AntiCheatSeverity.Moderate
            : AntiCheatSeverity.Low;

        return new CorrelationResult(true, bonus, confidence, severity, categoryNames, domainNames, evidence);
    }

    private static string IndependentUnitKey(Signal signal) =>
        string.Equals(signal.Domain, "ServerBehavior", StringComparison.OrdinalIgnoreCase)
            ? $"ServerBehavior:{signal.Category}"
            : signal.Domain;

    private sealed class Signal
    {
        public long Utc;
        public string Category = "Other";
        public string Domain = "Other";
        public string Code = "";
        public string Scanner = "";
        public int Severity;
        public string Subject = "";
    }
}

internal readonly record struct CorrelationResult(
    bool Active,
    int RiskBonus,
    int Confidence,
    AntiCheatSeverity Severity,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Domains,
    string Evidence)
{
    public static CorrelationResult None => new(false, 0, 0, AntiCheatSeverity.Info, Array.Empty<string>(), Array.Empty<string>(), "");
}
