using System;
using System.Collections.Generic;
using System.Linq;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Models;

namespace BlockLogger.AntiCheat;

internal static class AntiCheatFindingIntelligence
{
	public static AntiCheatFindingInsight Analyze(ScanFindingDto finding, IEnumerable<ScanFindingDto>? allFindings)
	{
		if (finding == null)
		{
			finding = new ScanFindingDto();
		}
		List<ScanFindingDto> source = (allFindings ?? Array.Empty<ScanFindingDto>()).Where((ScanFindingDto x) => x != null).ToList();
		string code = (finding.Code ?? "").Trim();
		string category = GetCategory(code, finding.Scanner);
		if (AntiCheatObservationPolicy.IsReviewOnly(code))
		{
			return new AntiCheatFindingInsight
			{
				Category = "Movement",
				Confidence = "Low",
				ConfidenceScore = 30,
				Why = "Sustained unsupported, non-falling movement was sampled against loaded server terrain. Modded physics can produce similar observations; this is review context only.",
				RecommendedAction = "Review the recorded duration, terrain, movement and exclusions with the player's gameplay context. This observation adds no risk or automatic consequences."
			};
		}
		int num = BaseConfidence(finding);
		int num2 = ((!code.Equals("CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase)) ? (from x in source
			where x != finding && x.Severity >= 1 && !string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase) && !AntiCheatObservationPolicy.IsReviewOnly(x.Code) && !string.Equals(x.Scanner, "CorrelationPolicy", StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Code, "CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase) && string.Equals(GetCategory(x.Code, x.Scanner), category, StringComparison.OrdinalIgnoreCase)
			select x.Code ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() : 0);
		num = Math.Clamp(num + Math.Min(15, num2 * 5), 0, 99);
		return new AntiCheatFindingInsight
		{
			Category = category,
			ConfidenceScore = num,
			Confidence = ((num >= 90) ? "Critical" : ((num >= 75) ? "High" : ((num >= 55) ? "Medium" : ((num >= 30) ? "Low" : "Informational")))),
			CorroboratingSignals = num2,
			Why = Why(code, category),
			RecommendedAction = Recommendation(code, category, num, num2)
		};
	}

	private static int BaseConfidence(ScanFindingDto finding)
	{
		string text = finding.Code ?? "";
		string text2 = finding.Evidence ?? "";
		if (text.Equals("CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase))
		{
			return Math.Clamp(ParsePercent(text2, "confidence="), 45, 99);
		}
		if (text.Equals("FORBIDDEN_REPACK_CORRELATED", StringComparison.OrdinalIgnoreCase))
		{
			return 97;
		}
		bool flag;
		switch (text)
		{
		case "FORBIDDEN_MOD":
		case "FORBIDDEN_MOD_FINGERPRINT":
		case "MODMENU_HARMONY_OWNER":
		case "CLIENT_BUILD_UNAPPROVED":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			return 96;
		}
		if (text.Equals("FORBIDDEN_ASSEMBLY_FINGERPRINT", StringComparison.OrdinalIgnoreCase))
		{
			return text2.Contains("assembly-name", StringComparison.OrdinalIgnoreCase) ? 74 : 94;
		}
		if (text.Equals("FORBIDDEN_ASSEMBLY_BEHAVIOR", StringComparison.OrdinalIgnoreCase))
		{
			return 84;
		}
		if (text.Equals("FORBIDDEN_ASSEMBLY_BEHAVIOR_SIMILARITY", StringComparison.OrdinalIgnoreCase))
		{
			return Math.Clamp(ParsePercent(text2, "behavior-similarity=") - 5, 55, 90);
		}
		if (text.Equals("FORBIDDEN_ASSEMBLY_SIMILARITY", StringComparison.OrdinalIgnoreCase))
		{
			return Math.Clamp(ParsePercent(text2, "similarity=") - 8, 50, 86);
		}
		if (text.Equals("HOTKEY_BLACKLIST_OWNER_MATCH", StringComparison.OrdinalIgnoreCase))
		{
			return 68;
		}
		if (text.Equals("HOTKEY_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase) || text.Equals("CLIENT_COMMAND_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase))
		{
			return 58;
		}
		if (text.Equals("ASSEMBLY_SIZE_MISMATCH", StringComparison.OrdinalIgnoreCase) || text.Equals("MOD_DLL_SET_MISMATCH", StringComparison.OrdinalIgnoreCase))
		{
			return 66;
		}
		if (text.Equals("FORBIDDEN_ASSEMBLY_SIZE_MATCH", StringComparison.OrdinalIgnoreCase))
		{
			return 48;
		}
		if (text.Equals("MODDB_CONTENT_MISMATCH", StringComparison.OrdinalIgnoreCase))
		{
			return 72;
		}
		if (text.Equals("MODDB_ASSEMBLY_MISMATCH", StringComparison.OrdinalIgnoreCase))
		{
			return 70;
		}
		if (text.Equals("MODDB_DLL_SET_MISMATCH", StringComparison.OrdinalIgnoreCase))
		{
			return 30;
		}
		if (text.Equals("MODDB_PACKAGE_MISMATCH", StringComparison.OrdinalIgnoreCase))
		{
			return 35;
		}
		if (text.Equals("INVALID_REPORT", StringComparison.OrdinalIgnoreCase) || text.Contains("SELF_CHANGED", StringComparison.OrdinalIgnoreCase))
		{
			return 92;
		}
		if (text.Contains("UNAUTHORIZED_MOVEMENT", StringComparison.OrdinalIgnoreCase))
		{
			int result = (text2.Contains("extreme=true", StringComparison.OrdinalIgnoreCase) ? 96 : (text2.Contains("confirm", StringComparison.OrdinalIgnoreCase) ? 82 : 72));
			return result;
		}
		if (text.Contains("BLOCK_REACH", StringComparison.OrdinalIgnoreCase))
		{
			return text2.Contains("hard", StringComparison.OrdinalIgnoreCase) ? 93 : 78;
		}
		if (text.Contains("CONNECTED_BREAK", StringComparison.OrdinalIgnoreCase))
		{
			return 82;
		}
		if (text.Equals("RUNTIME_UNASSOCIATED_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) || text.Equals("RUNTIME_DYNAMIC_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) || text.Equals("DUPLICATE_ASSEMBLY_IDENTITY", StringComparison.OrdinalIgnoreCase))
		{
			return 15;
		}
		if (text.Equals("RUNTIME_UNKNOWN_ASSEMBLY_CLUSTER", StringComparison.OrdinalIgnoreCase))
		{
			return 38;
		}
		AntiCheatSeverity antiCheatSeverity = (AntiCheatSeverity)Math.Clamp(finding.Severity, 0, 4);
		if (1 == 0)
		{
		}
		int result2 = antiCheatSeverity switch
		{
			AntiCheatSeverity.Critical => 88, 
			AntiCheatSeverity.High => 78, 
			AntiCheatSeverity.Moderate => 62, 
			AntiCheatSeverity.Low => 42, 
			_ => 25, 
		};
		if (1 == 0)
		{
		}
		return result2;
	}

	private static int ParsePercent(string evidence, string key)
	{
		int num = evidence.IndexOf(key, StringComparison.OrdinalIgnoreCase);
		if (num < 0)
		{
			return 0;
		}
		num += key.Length;
		int i;
		for (i = num; i < evidence.Length && char.IsDigit(evidence[i]); i++)
		{
		}
		int num2 = num;
		int result;
		return int.TryParse(evidence.Substring(num2, i - num2), out result) ? Math.Clamp(result, 0, 100) : 0;
	}

	public static string GetCategory(string? code, string? scanner)
	{
		if (AntiCheatObservationPolicy.IsReviewOnly(code))
		{
			return "Movement";
		}
		string text = code ?? "";
		if (text.Equals("CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase))
		{
			return "Correlated Incident";
		}
		if (text.Contains("REACH", StringComparison.OrdinalIgnoreCase))
		{
			return "Reach";
		}
		if (text.Contains("MOVEMENT", StringComparison.OrdinalIgnoreCase) || text.Contains("TELEPORT", StringComparison.OrdinalIgnoreCase))
		{
			return "Movement";
		}
		if (text.Contains("BREAK", StringComparison.OrdinalIgnoreCase) || text.Contains("MINING", StringComparison.OrdinalIgnoreCase))
		{
			return "Mining";
		}
		if (text.Contains("FORBIDDEN", StringComparison.OrdinalIgnoreCase) || text.Contains("BLACKLIST", StringComparison.OrdinalIgnoreCase) || text.Contains("CANONICAL", StringComparison.OrdinalIgnoreCase) || text.Contains("FINGERPRINT", StringComparison.OrdinalIgnoreCase) || text.Contains("SIMILARITY", StringComparison.OrdinalIgnoreCase) || text.Contains("REPACK", StringComparison.OrdinalIgnoreCase) || text.Contains("SIZE_MISMATCH", StringComparison.OrdinalIgnoreCase) || text.Contains("DLL_SET", StringComparison.OrdinalIgnoreCase) || text.Contains("MODDB", StringComparison.OrdinalIgnoreCase))
		{
			return "Mod Integrity";
		}
		if (text.Contains("HARMONY", StringComparison.OrdinalIgnoreCase))
		{
			return "Runtime Patching";
		}
		if (text.Contains("RUNTIME", StringComparison.OrdinalIgnoreCase) || text.Contains("ASSEMBLY", StringComparison.OrdinalIgnoreCase))
		{
			return "Runtime / Assembly";
		}
		if (text.Contains("CLIENT_", StringComparison.OrdinalIgnoreCase) || text.Contains("REPORT", StringComparison.OrdinalIgnoreCase) || text.Contains("PROTOCOL", StringComparison.OrdinalIgnoreCase))
		{
			return "Client Integrity";
		}
		return string.Equals(scanner, "ServerBehavior", StringComparison.OrdinalIgnoreCase) ? "Server Behavior" : "Other";
	}

	public static string GetEvidenceDomain(string? code, string? scanner, string? category = null)
	{
		string code2 = code ?? "";
		string text = category ?? GetCategory(code2, scanner);
		bool flag;
		switch (text)
		{
		case "Movement":
		case "Reach":
		case "Mining":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag || string.Equals(scanner, "ServerBehavior", StringComparison.OrdinalIgnoreCase))
		{
			return "ServerBehavior";
		}
		return text switch
		{
			"Mod Integrity" => "StaticIntegrity", 
			"Runtime Patching" => "PatchOwnership", 
			"Runtime / Assembly" => "RuntimeIntegrity", 
			"Client Integrity" => "ClientIntegrity", 
			_ => "Other", 
		};
	}

	private static string Why(string code, string category)
	{
		if (code.Equals("FORBIDDEN_REPACK_CORRELATED", StringComparison.OrdinalIgnoreCase))
		{
			return "Independent structure and identifier-free IL behavior fingerprints both resemble the same blacklisted mod, which is much stronger than a filename or mod-ID match.";
		}
		if (code.Equals("FORBIDDEN_ASSEMBLY_BEHAVIOR", StringComparison.OrdinalIgnoreCase))
		{
			return "The assembly's normalized IL opcode behavior matches a blacklisted build even if names, MVID, package hash, or mod ID were changed.";
		}
		if (code.Equals("FORBIDDEN_ASSEMBLY_BEHAVIOR_SIMILARITY", StringComparison.OrdinalIgnoreCase))
		{
			return "The assembly's identifier-free IL behavior is unusually similar to a blacklisted release. Treat this as supporting evidence unless another independent signal agrees.";
		}
		if (code.Equals("HOTKEY_BLACKLIST_OWNER_MATCH", StringComparison.OrdinalIgnoreCase))
		{
			return "The registered hotkey handler assembly matches an alias derived from an active blacklisted mod/fingerprint. This is stronger than a generic hack-name match, but the client still controls this telemetry.";
		}
		if (code.Equals("HOTKEY_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase) || code.Equals("CLIENT_COMMAND_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase))
		{
			return "The registered runtime name matches a mod ID or assembly alias derived from the active blacklist. It is supporting evidence and should agree with package, assembly, patch, asset, or server-behavior evidence.";
		}
		if (code.Equals("ASSEMBLY_SIZE_MISMATCH", StringComparison.OrdinalIgnoreCase) || code.Equals("MOD_DLL_SET_MISMATCH", StringComparison.OrdinalIgnoreCase))
		{
			return "The client claims the same code-mod ID/version as the trusted server build, but its DLL byte layout differs from the baseline. Recompression cannot explain an uncompressed DLL-size difference.";
		}
		if (code.Equals("FORBIDDEN_ASSEMBLY_SIZE_MATCH", StringComparison.OrdinalIgnoreCase))
		{
			return "The assembly name and exact uncompressed DLL byte length match a stored official Mod DB release of a blacklisted mod. Size is only supporting evidence because unrelated binaries can occasionally share a length.";
		}
		if (code.Equals("MODDB_CONTENT_MISMATCH", StringComparison.OrdinalIgnoreCase))
		{
			return "The client reported the same ModID and version, but its normalized package-content SHA-256 differs from every successfully fingerprinted official Mod DB upload for that exact version. This ignores ZIP recompression metadata.";
		}
		if (code.Equals("MODDB_ASSEMBLY_MISMATCH", StringComparison.OrdinalIgnoreCase))
		{
			return "A DLL with one unambiguous owning mod has a valid client SHA-256 that differs from every same-name DLL SHA-256 in the successfully fingerprinted official Mod DB uploads for that exact version. File size, structure similarity and behavior fingerprints are diagnostic only.";
		}
		if (code.Equals("MODDB_DLL_SET_MISMATCH", StringComparison.OrdinalIgnoreCase))
		{
			return "The client mod's DLL count/bytes differ from every resolved official upload for the same version. This is supporting diagnostic context only and does not prove a modified DLL.";
		}
		if (code.Equals("MODDB_PACKAGE_MISMATCH", StringComparison.OrdinalIgnoreCase))
		{
			return "The raw package SHA-256 differs from every resolved exact-version Mod DB upload while normalized content was unavailable. Repacking or compression changes can explain this, so it remains weak advisory evidence.";
		}
		if (code.Equals("CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase))
		{
			return "Independent evidence units occurred inside the configured incident window. Server behavior modalities can corroborate each other, while client-side categories sharing one evidence domain are counted only once. The risk bonus remains bounded.";
		}
		if (code.Equals("PACKET_IMPOSSIBLE_MINING_CADENCE", StringComparison.OrdinalIgnoreCase))
		{
			return "The server compared expected break time with observed adjacent-break cadence using block resistance, held tool/tier, material capability, mining speed, block modifiers, and expectation reliability. The evidence row includes expectedMs, observedAdjacentMs, ratio, tool/material eligibility, and reliability.";
		}
		if (code.Equals("PACKET_MINING_CADENCE_UNCERTAIN", StringComparison.OrdinalIgnoreCase))
		{
			return "The cadence was unusual, but the block or held collectible overrides the normal breaking path. It is retained as informational context and is not punishment-grade evidence.";
		}
		switch (category)
		{
		case "Reach":
			return "The server measured a block interaction beyond the player's authoritative picking range after configured latency/history allowances.";
		case "Movement":
			return "The server observed a position transition outside the detector-scoped trusted teleport/admin exceptions and movement confirmation rules.";
		case "Mining":
			return "The server observed a connected break pattern that exceeded the configured distinct-block/time window after duplicate suppression.";
		case "Runtime Patching":
			return "Runtime patch ownership/targets show code modifying game behavior inside the Vintage Story process.";
		case "Mod Integrity":
			return "Loaded package or assembly evidence resembles an explicitly blacklisted client mod.";
		default:
			if (code.Equals("RUNTIME_UNASSOCIATED_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) || code.Equals("RUNTIME_DYNAMIC_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) || code.Equals("DUPLICATE_ASSEMBLY_IDENTITY", StringComparison.OrdinalIgnoreCase))
			{
				return "Late-loaded assemblies are common in .NET and can be normal game/mod dependencies. This is supporting runtime telemetry only unless multiple unknown assemblies or stronger integrity signals corroborate it.";
			}
			if (code.Equals("RUNTIME_UNKNOWN_ASSEMBLY_CLUSTER", StringComparison.OrdinalIgnoreCase))
			{
				return "Several distinct unknown post-baseline assemblies appeared in the same scan. A cluster is more meaningful than one lazy-loaded dependency, but it is still only supporting evidence.";
			}
			return "This finding is derived from anti-cheat telemetry and should be reviewed with its evidence and nearby signals.";
		}
	}

	private static string Recommendation(string code, string category, int confidence, int corroborating)
	{
		if (code.Equals("CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase))
		{
			return (confidence >= 75) ? "Review the correlated detector timeline as one incident; multiple independent categories materially strengthen the evidence." : "Keep the correlated context for staff review and wait for stronger or repeated independent evidence before disruptive action.";
		}
		if (confidence >= 90 && (category == "Mod Integrity" || category == "Client Integrity"))
		{
			return "High-confidence integrity evidence: verify the matched fingerprint/source, then apply the configured consequence if policy requires it.";
		}
		bool flag = confidence >= 90;
		bool flag2 = flag;
		if (flag2)
		{
			bool flag3;
			switch (category)
			{
			case "Reach":
			case "Movement":
			case "Mining":
				flag3 = true;
				break;
			default:
				flag3 = false;
				break;
			}
			flag2 = flag3;
		}
		if (flag2)
		{
			return "High-confidence server-authoritative behavior: review the exact evidence/position and nearby reports; enforcement is reasonable if it repeats or policy already requires it.";
		}
		if (corroborating > 0)
		{
			return $"Review together with the {corroborating} corroborating signal(s) from the same category/subject before escalating.";
		}
		if (code.Equals("RUNTIME_UNASSOCIATED_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) || code.Equals("RUNTIME_DYNAMIC_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) || code.Equals("DUPLICATE_ASSEMBLY_IDENTITY", StringComparison.OrdinalIgnoreCase))
		{
			return "Informational runtime telemetry. Do not punish for this alone; look for multiple distinct unknown assemblies or independent integrity/patching evidence.";
		}
		if (code.Equals("RUNTIME_UNKNOWN_ASSEMBLY_CLUSTER", StringComparison.OrdinalIgnoreCase))
		{
			return "Review the listed assemblies and nearby integrity/patching findings. Keep this review-only unless an independent signal corroborates it.";
		}
		if (code.Contains("SIMILARITY", StringComparison.OrdinalIgnoreCase))
		{
			return "Similarity alone should normally stay review-only. Resolve more releases or capture a live/manual fingerprint for stronger confirmation.";
		}
		return "Review the evidence and nearby reports; wait for repetition or corroboration before a disruptive consequence.";
	}
}
