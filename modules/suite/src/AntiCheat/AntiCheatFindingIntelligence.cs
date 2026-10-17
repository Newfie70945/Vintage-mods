using System;
using System.Collections.Generic;
using System.Linq;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Models;

namespace BlockLogger.AntiCheat;

internal sealed class AntiCheatFindingInsight
{
    public string Category { get; set; } = "Other";
    public string Confidence { get; set; } = "Low";
    public int ConfidenceScore { get; set; }
    public int CorroboratingSignals { get; set; }
    public string Why { get; set; } = "";
    public string RecommendedAction { get; set; } = "Review the evidence before taking action.";
}

internal static class AntiCheatFindingIntelligence
{
    public static AntiCheatFindingInsight Analyze(ScanFindingDto finding, IEnumerable<ScanFindingDto>? allFindings)
    {
        finding ??= new ScanFindingDto();
        var all = (allFindings ?? Array.Empty<ScanFindingDto>()).Where(x => x != null).ToList();
        string code = (finding.Code ?? "").Trim();
        string category = GetCategory(code, finding.Scanner);
        if (AntiCheatObservationPolicy.IsReviewOnly(code))
            return new AntiCheatFindingInsight
            {
                Category = "Movement",
                Confidence = "Low",
                ConfidenceScore = 30,
                Why = "Sustained unsupported, non-falling movement was sampled against loaded server terrain. Modded physics can produce similar observations; this is review context only.",
                RecommendedAction = "Review the recorded duration, terrain, movement and exclusions with the player's gameplay context. This observation adds no risk or automatic consequences."
            };
        int score = BaseConfidence(finding);

        // Player UID is commonly used as Subject by server-behavior findings, so Subject equality
        // is not semantic corroboration. Count only same-category non-correlation evidence, and
        // keep the synthetic incident's confidence completely separate from detector confidence.
        int corroborating = code.Equals("CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase)
            ? 0
            : all.Where(x => !ReferenceEquals(x, finding) && x.Severity >= 1 &&
                !string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase) &&
                !AntiCheatObservationPolicy.IsReviewOnly(x.Code) &&
                !string.Equals(x.Scanner, "CorrelationPolicy", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(x.Code, "CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetCategory(x.Code, x.Scanner), category, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Code ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count();
        score = Math.Clamp(score + Math.Min(15, corroborating * 5), 0, 99);

        return new AntiCheatFindingInsight
        {
            Category = category,
            ConfidenceScore = score,
            Confidence = score >= 90 ? "Critical" : score >= 75 ? "High" : score >= 55 ? "Medium" : score >= 30 ? "Low" : "Informational",
            CorroboratingSignals = corroborating,
            Why = Why(code, category),
            RecommendedAction = Recommendation(code, category, score, corroborating)
        };
    }

    private static int BaseConfidence(ScanFindingDto finding)
    {
        string code = finding.Code ?? "";
        string evidence = finding.Evidence ?? "";
        if (code.Equals("CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase)) return Math.Clamp(ParsePercent(evidence, "confidence="), 45, 99);
        if (code.Equals("FORBIDDEN_REPACK_CORRELATED", StringComparison.OrdinalIgnoreCase)) return 97;
        if (code is "FORBIDDEN_MOD" or "FORBIDDEN_MOD_FINGERPRINT" or "MODMENU_HARMONY_OWNER" or "CLIENT_BUILD_UNAPPROVED") return 96;
        if (code.Equals("FORBIDDEN_ASSEMBLY_FINGERPRINT", StringComparison.OrdinalIgnoreCase))
            return evidence.Contains("assembly-name", StringComparison.OrdinalIgnoreCase) ? 74 : 94;
        if (code.Equals("FORBIDDEN_ASSEMBLY_BEHAVIOR", StringComparison.OrdinalIgnoreCase)) return 84;
        if (code.Equals("FORBIDDEN_ASSEMBLY_BEHAVIOR_SIMILARITY", StringComparison.OrdinalIgnoreCase)) return Math.Clamp(ParsePercent(evidence, "behavior-similarity=") - 5, 55, 90);
        if (code.Equals("FORBIDDEN_ASSEMBLY_SIMILARITY", StringComparison.OrdinalIgnoreCase)) return Math.Clamp(ParsePercent(evidence, "similarity=") - 8, 50, 86);
        if (code.Equals("HOTKEY_BLACKLIST_OWNER_MATCH", StringComparison.OrdinalIgnoreCase)) return 68;
        if (code.Equals("HOTKEY_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase) || code.Equals("CLIENT_COMMAND_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase)) return 58;
        if (code.Equals("ASSEMBLY_SIZE_MISMATCH", StringComparison.OrdinalIgnoreCase) || code.Equals("MOD_DLL_SET_MISMATCH", StringComparison.OrdinalIgnoreCase)) return 66;
        if (code.Equals("FORBIDDEN_ASSEMBLY_SIZE_MATCH", StringComparison.OrdinalIgnoreCase)) return 48;
        if (code.Equals("MODDB_CONTENT_MISMATCH", StringComparison.OrdinalIgnoreCase)) return 72;
        if (code.Equals("MODDB_ASSEMBLY_MISMATCH", StringComparison.OrdinalIgnoreCase)) return 70;
        if (code.Equals("MODDB_DLL_SET_MISMATCH", StringComparison.OrdinalIgnoreCase)) return 30;
        if (code.Equals("MODDB_PACKAGE_MISMATCH", StringComparison.OrdinalIgnoreCase)) return 35;
        if (code.Equals("INVALID_REPORT", StringComparison.OrdinalIgnoreCase) || code.Contains("SELF_CHANGED", StringComparison.OrdinalIgnoreCase)) return 92;
        if (code.Contains("UNAUTHORIZED_MOVEMENT", StringComparison.OrdinalIgnoreCase))
            return evidence.Contains("extreme=true", StringComparison.OrdinalIgnoreCase) ? 96 : evidence.Contains("confirm", StringComparison.OrdinalIgnoreCase) ? 82 : 72;
        if (code.Contains("BLOCK_REACH", StringComparison.OrdinalIgnoreCase))
            return evidence.Contains("hard", StringComparison.OrdinalIgnoreCase) ? 93 : 78;
        if (code.Contains("CONNECTED_BREAK", StringComparison.OrdinalIgnoreCase)) return 82;
        if (code.Equals("RUNTIME_UNASSOCIATED_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("RUNTIME_DYNAMIC_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("DUPLICATE_ASSEMBLY_IDENTITY", StringComparison.OrdinalIgnoreCase)) return 15;
        if (code.Equals("RUNTIME_UNKNOWN_ASSEMBLY_CLUSTER", StringComparison.OrdinalIgnoreCase)) return 38;
        return ((AntiCheatSeverity)Math.Clamp(finding.Severity, 0, 4)) switch
        {
            AntiCheatSeverity.Critical => 88,
            AntiCheatSeverity.High => 78,
            AntiCheatSeverity.Moderate => 62,
            AntiCheatSeverity.Low => 42,
            _ => 25
        };
    }

    private static int ParsePercent(string evidence, string key)
    {
        int i = evidence.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return 0;
        i += key.Length;
        int end = i;
        while (end < evidence.Length && char.IsDigit(evidence[end])) end++;
        return int.TryParse(evidence[i..end], out int value) ? Math.Clamp(value, 0, 100) : 0;
    }

    public static string GetCategory(string? code, string? scanner)
    {
        if (AntiCheatObservationPolicy.IsReviewOnly(code)) return "Movement";
        string c = code ?? "";
        if (c.Equals("CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase)) return "Correlated Incident";
        if (c.Contains("REACH", StringComparison.OrdinalIgnoreCase)) return "Reach";
        if (c.Contains("MOVEMENT", StringComparison.OrdinalIgnoreCase) || c.Contains("TELEPORT", StringComparison.OrdinalIgnoreCase)) return "Movement";
        if (c.Contains("BREAK", StringComparison.OrdinalIgnoreCase) || c.Contains("MINING", StringComparison.OrdinalIgnoreCase)) return "Mining";
        if (c.Contains("FORBIDDEN", StringComparison.OrdinalIgnoreCase) || c.Contains("BLACKLIST", StringComparison.OrdinalIgnoreCase) || c.Contains("CANONICAL", StringComparison.OrdinalIgnoreCase) || c.Contains("FINGERPRINT", StringComparison.OrdinalIgnoreCase) || c.Contains("SIMILARITY", StringComparison.OrdinalIgnoreCase) || c.Contains("REPACK", StringComparison.OrdinalIgnoreCase) || c.Contains("SIZE_MISMATCH", StringComparison.OrdinalIgnoreCase) || c.Contains("DLL_SET", StringComparison.OrdinalIgnoreCase) || c.Contains("MODDB", StringComparison.OrdinalIgnoreCase)) return "Mod Integrity";
        if (c.Contains("HARMONY", StringComparison.OrdinalIgnoreCase)) return "Runtime Patching";
        if (c.Contains("RUNTIME", StringComparison.OrdinalIgnoreCase) || c.Contains("ASSEMBLY", StringComparison.OrdinalIgnoreCase)) return "Runtime / Assembly";
        if (c.Contains("CLIENT_", StringComparison.OrdinalIgnoreCase) || c.Contains("REPORT", StringComparison.OrdinalIgnoreCase) || c.Contains("PROTOCOL", StringComparison.OrdinalIgnoreCase)) return "Client Integrity";
        return string.Equals(scanner, "ServerBehavior", StringComparison.OrdinalIgnoreCase) ? "Server Behavior" : "Other";
    }

    public static string GetEvidenceDomain(string? code, string? scanner, string? category = null)
    {
        string c = code ?? "";
        string cat = category ?? GetCategory(c, scanner);
        if (cat is "Movement" or "Reach" or "Mining" || string.Equals(scanner, "ServerBehavior", StringComparison.OrdinalIgnoreCase)) return "ServerBehavior";
        if (cat == "Mod Integrity") return "StaticIntegrity";
        if (cat == "Runtime Patching") return "PatchOwnership";
        if (cat == "Runtime / Assembly") return "RuntimeIntegrity";
        if (cat == "Client Integrity") return "ClientIntegrity";
        return "Other";
    }

    private static string Why(string code, string category)
    {
        if (code.Equals("FORBIDDEN_REPACK_CORRELATED", StringComparison.OrdinalIgnoreCase)) return "Independent structure and identifier-free IL behavior fingerprints both resemble the same blacklisted mod, which is much stronger than a filename or mod-ID match.";
        if (code.Equals("FORBIDDEN_ASSEMBLY_BEHAVIOR", StringComparison.OrdinalIgnoreCase)) return "The assembly's normalized IL opcode behavior matches a blacklisted build even if names, MVID, package hash, or mod ID were changed.";
        if (code.Equals("FORBIDDEN_ASSEMBLY_BEHAVIOR_SIMILARITY", StringComparison.OrdinalIgnoreCase)) return "The assembly's identifier-free IL behavior is unusually similar to a blacklisted release. Treat this as supporting evidence unless another independent signal agrees.";
        if (code.Equals("HOTKEY_BLACKLIST_OWNER_MATCH", StringComparison.OrdinalIgnoreCase)) return "The registered hotkey handler assembly matches an alias derived from an active blacklisted mod/fingerprint. This is stronger than a generic hack-name match, but the client still controls this telemetry.";
        if (code.Equals("HOTKEY_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase) || code.Equals("CLIENT_COMMAND_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase)) return "The registered runtime name matches a mod ID or assembly alias derived from the active blacklist. It is supporting evidence and should agree with package, assembly, patch, asset, or server-behavior evidence.";
        if (code.Equals("ASSEMBLY_SIZE_MISMATCH", StringComparison.OrdinalIgnoreCase) || code.Equals("MOD_DLL_SET_MISMATCH", StringComparison.OrdinalIgnoreCase)) return "The client claims the same code-mod ID/version as the trusted server build, but its DLL byte layout differs from the baseline. Recompression cannot explain an uncompressed DLL-size difference.";
        if (code.Equals("FORBIDDEN_ASSEMBLY_SIZE_MATCH", StringComparison.OrdinalIgnoreCase)) return "The assembly name and exact uncompressed DLL byte length match a stored official Mod DB release of a blacklisted mod. Size is only supporting evidence because unrelated binaries can occasionally share a length.";
        if (code.Equals("MODDB_CONTENT_MISMATCH", StringComparison.OrdinalIgnoreCase)) return "The client reported the same ModID and version, but its normalized package-content SHA-256 differs from every successfully fingerprinted official Mod DB upload for that exact version. This ignores ZIP recompression metadata.";
        if (code.Equals("MODDB_ASSEMBLY_MISMATCH", StringComparison.OrdinalIgnoreCase)) return "A DLL with one unambiguous owning mod has a valid client SHA-256 that differs from every same-name DLL SHA-256 in the successfully fingerprinted official Mod DB uploads for that exact version. File size, structure similarity and behavior fingerprints are diagnostic only.";
        if (code.Equals("MODDB_DLL_SET_MISMATCH", StringComparison.OrdinalIgnoreCase)) return "The client mod's DLL count/bytes differ from every resolved official upload for the same version. This is supporting diagnostic context only and does not prove a modified DLL.";
        if (code.Equals("MODDB_PACKAGE_MISMATCH", StringComparison.OrdinalIgnoreCase)) return "The raw package SHA-256 differs from every resolved exact-version Mod DB upload while normalized content was unavailable. Repacking or compression changes can explain this, so it remains weak advisory evidence.";
        if (code.Equals("CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase)) return "Independent evidence units occurred inside the configured incident window. Server behavior modalities can corroborate each other, while client-side categories sharing one evidence domain are counted only once. The risk bonus remains bounded.";
        if (code.Equals("PACKET_IMPOSSIBLE_MINING_CADENCE", StringComparison.OrdinalIgnoreCase)) return "The server compared expected break time with observed adjacent-break cadence using block resistance, held tool/tier, material capability, mining speed, block modifiers, and expectation reliability. The evidence row includes expectedMs, observedAdjacentMs, ratio, tool/material eligibility, and reliability.";
        if (code.Equals("PACKET_MINING_CADENCE_UNCERTAIN", StringComparison.OrdinalIgnoreCase)) return "The cadence was unusual, but the block or held collectible overrides the normal breaking path. It is retained as informational context and is not punishment-grade evidence.";
        if (category == "Reach") return "The server measured a block interaction beyond the player's authoritative picking range after configured latency/history allowances.";
        if (category == "Movement") return "The server observed a position transition outside the detector-scoped trusted teleport/admin exceptions and movement confirmation rules.";
        if (category == "Mining") return "The server observed a connected break pattern that exceeded the configured distinct-block/time window after duplicate suppression.";
        if (category == "Runtime Patching") return "Runtime patch ownership/targets show code modifying game behavior inside the Vintage Story process.";
        if (category == "Mod Integrity") return "Loaded package or assembly evidence resembles an explicitly blacklisted client mod.";
        if (code.Equals("RUNTIME_UNASSOCIATED_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("RUNTIME_DYNAMIC_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("DUPLICATE_ASSEMBLY_IDENTITY", StringComparison.OrdinalIgnoreCase))
            return "Late-loaded assemblies are common in .NET and can be normal game/mod dependencies. This is supporting runtime telemetry only unless multiple unknown assemblies or stronger integrity signals corroborate it.";
        if (code.Equals("RUNTIME_UNKNOWN_ASSEMBLY_CLUSTER", StringComparison.OrdinalIgnoreCase))
            return "Several distinct unknown post-baseline assemblies appeared in the same scan. A cluster is more meaningful than one lazy-loaded dependency, but it is still only supporting evidence.";
        return "This finding is derived from anti-cheat telemetry and should be reviewed with its evidence and nearby signals.";
    }

    private static string Recommendation(string code, string category, int confidence, int corroborating)
    {
        if (code.Equals("CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase)) return confidence >= 75
            ? "Review the correlated detector timeline as one incident; multiple independent categories materially strengthen the evidence."
            : "Keep the correlated context for staff review and wait for stronger or repeated independent evidence before disruptive action.";
        if (confidence >= 90 && (category == "Mod Integrity" || category == "Client Integrity")) return "High-confidence integrity evidence: verify the matched fingerprint/source, then apply the configured consequence if policy requires it.";
        if (confidence >= 90 && category is "Reach" or "Movement" or "Mining") return "High-confidence server-authoritative behavior: review the exact evidence/position and nearby reports; enforcement is reasonable if it repeats or policy already requires it.";
        if (corroborating > 0) return $"Review together with the {corroborating} corroborating signal(s) from the same category/subject before escalating.";
        if (code.Equals("RUNTIME_UNASSOCIATED_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("RUNTIME_DYNAMIC_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("DUPLICATE_ASSEMBLY_IDENTITY", StringComparison.OrdinalIgnoreCase))
            return "Informational runtime telemetry. Do not punish for this alone; look for multiple distinct unknown assemblies or independent integrity/patching evidence.";
        if (code.Equals("RUNTIME_UNKNOWN_ASSEMBLY_CLUSTER", StringComparison.OrdinalIgnoreCase))
            return "Review the listed assemblies and nearby integrity/patching findings. Keep this review-only unless an independent signal corroborates it.";
        if (code.Contains("SIMILARITY", StringComparison.OrdinalIgnoreCase)) return "Similarity alone should normally stay review-only. Resolve more releases or capture a live/manual fingerprint for stronger confirmation.";
        return "Review the evidence and nearby reports; wait for repetition or corroboration before a disruptive consequence.";
    }
}
