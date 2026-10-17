using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Models;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatEvaluator
{
	private readonly AntiCheatConfig config;

	public AntiCheatEvaluator(AntiCheatConfig config)
	{
		this.config = config;
	}

	public (AntiCheatPlayerStatus Status, int Score, List<ScanFindingDto> Findings) Evaluate(IReadOnlyList<ClientModDto> mods, IReadOnlyList<ScanFindingDto> clientFindings, IReadOnlyList<ClientAssemblyDto>? assemblies = null, ClientIntegrityDto? integrity = null, ClientHarmonyDto? harmony = null, IReadOnlyDictionary<string, AntiCheatCanonicalModFingerprint>? canonicalMods = null, IReadOnlyDictionary<string, AntiCheatCoreAssemblyFingerprint>? coreAssemblies = null)
	{
		if (assemblies == null)
		{
			assemblies = Array.Empty<ClientAssemblyDto>();
		}
		List<ScanFindingDto> list = (clientFindings ?? Array.Empty<ScanFindingDto>()).Select(NormalizeClientFinding).ToList();
		Dictionary<string, ClientModDto> dictionary = mods.Where((ClientModDto m) => !string.IsNullOrWhiteSpace(m.ModId)).GroupBy((ClientModDto m) => m.ModId, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, ClientModDto> g) => g.Key, (IGrouping<string, ClientModDto> g) => g.First(), StringComparer.OrdinalIgnoreCase);
		foreach (string item2 in config.RequiredClientModIds.Where((string x) => !string.IsNullOrWhiteSpace(x)))
		{
			if (!dictionary.ContainsKey(item2))
			{
				AddUnique(list, Finding("ServerPolicy", "REQUIRED_MOD_MISSING", AntiCheatSeverity.High, "A server-required client mod is missing.", item2, "required-mod-id"));
			}
		}
		foreach (string item3 in config.ForbiddenModIds.Where((string x) => !string.IsNullOrWhiteSpace(x)))
		{
			if (dictionary.ContainsKey(item3))
			{
				AddUnique(list, Finding("ServerPolicy", "FORBIDDEN_MOD", AntiCheatSeverity.Critical, "A forbidden client mod is loaded.", item3, "mod-id"));
			}
		}
		EvaluateFingerprintBlacklist(mods, assemblies, list);
		EvaluateCanonicalModConsistency(mods, assemblies, canonicalMods, list);
		EvaluateCoreAssemblyIntegrity(assemblies, coreAssemblies, list);
		EvaluateAssemblyConsistency(mods, assemblies, list);
		EvaluateClientIntegrity(assemblies, integrity, list);
		EvaluateHarmony(harmony, list);
		EvaluateRuntimeAssemblyCluster(list);
		list.RemoveAll(IsKnownBenignRuntimeObservation);
		list.RemoveAll(IsKnownVanillaClientCommandObservation);
		list.RemoveAll((ScanFindingDto f) => string.Equals(f.Code, "RUNTIME_BLACKLIST_ALIAS_IGNORED", StringComparison.OrdinalIgnoreCase) || string.Equals(f.Code, "PROTECTED_ASSET_BASELINE_UNAVAILABLE", StringComparison.OrdinalIgnoreCase));
		int num = ComputeRiskScore(list);
		AntiCheatPlayerStatus item;
		if (num >= 50)
		{
			item = AntiCheatPlayerStatus.Suspicious;
		}
		else
		{
			item = ((num >= 10) ? AntiCheatPlayerStatus.Review : AntiCheatPlayerStatus.Clean);
		}
		return (Status: item, Score: num, Findings: list);
	}

	private static ScanFindingDto NormalizeClientFinding(ScanFindingDto finding)
	{
		ScanFindingDto scanFindingDto = new ScanFindingDto
		{
			Scanner = (finding?.Scanner ?? ""),
			Code = (finding?.Code ?? ""),
			Severity = (finding?.Severity ?? 0),
			Summary = (finding?.Summary ?? ""),
			Evidence = (finding?.Evidence ?? ""),
			Subject = (finding?.Subject ?? "")
		};
		if ((scanFindingDto.Code.Equals("HOTKEY_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase) || scanFindingDto.Code.Equals("CLIENT_COMMAND_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase)) && !IsStrictBlacklistRuntimeMatch(scanFindingDto))
		{
			scanFindingDto.Code = "RUNTIME_BLACKLIST_ALIAS_IGNORED";
			scanFindingDto.Severity = 0;
			scanFindingDto.Summary = "A broad blacklist runtime-name match from the client was ignored because the alias was not a complete identifier/token match.";
			return scanFindingDto;
		}
		if (scanFindingDto.Code.Equals("PROTECTED_ASSET_SET_MISMATCH", StringComparison.OrdinalIgnoreCase) && EvidenceField(scanFindingDto.Evidence, "expectedCount").Equals("0", StringComparison.OrdinalIgnoreCase))
		{
			scanFindingDto.Code = "PROTECTED_ASSET_BASELINE_UNAVAILABLE";
			scanFindingDto.Severity = 0;
			scanFindingDto.Summary = "Protected-asset comparison was skipped because the server baseline was empty/unavailable.";
			return scanFindingDto;
		}
		if (IsRuntimeAssemblyObservation(scanFindingDto.Code) && IsFrameworkAssembly(scanFindingDto.Subject, scanFindingDto.Evidence))
		{
			scanFindingDto.Severity = 0;
			scanFindingDto.Summary = "Normal framework/runtime assembly observation (informational only).";
			return scanFindingDto;
		}
		if (IsRuntimeAssemblyObservation(scanFindingDto.Code))
		{
			scanFindingDto.Severity = Math.Min(scanFindingDto.Severity, 1);
		}
		if (scanFindingDto.Code.Equals("HOTKEY_SUSPICIOUS_NAME", StringComparison.OrdinalIgnoreCase) || scanFindingDto.Code.Equals("CLIENT_COMMAND_SUSPICIOUS", StringComparison.OrdinalIgnoreCase) || scanFindingDto.Code.Equals("RUNTIME_SURFACE_CHANGED", StringComparison.OrdinalIgnoreCase))
		{
			scanFindingDto.Severity = Math.Min(scanFindingDto.Severity, 1);
		}
		return scanFindingDto;
	}

	private static bool IsStrictBlacklistRuntimeMatch(ScanFindingDto finding)
	{
		string text = NormalizeIdentifier(EvidenceField(finding.Evidence, "blacklistTerm"));
		if (text.Length < 3)
		{
			return false;
		}
		if (finding.Code.Equals("HOTKEY_BLACKLIST_OWNER_MATCH", StringComparison.OrdinalIgnoreCase) && EvidenceField(finding.Evidence, "ownerMatch").Equals("true", StringComparison.OrdinalIgnoreCase))
		{
			return IdentifierMatches(EvidenceField(finding.Evidence, "handlerAssembly"), text);
		}
		if (IdentifierMatches(finding.Subject, text))
		{
			return true;
		}
		if (IdentifierMatches(EvidenceField(finding.Evidence, "name"), text))
		{
			return true;
		}
		return false;
	}

	private static string EvidenceField(string? evidence, string key)
	{
		string[] array = (evidence ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
		foreach (string text in array)
		{
			int num = text.IndexOf('=');
			if (num > 0 && text.Substring(0, num).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
			{
				return text.Substring(num + 1).Trim();
			}
		}
		return "";
	}

	private static bool IdentifierMatches(string? value, string normalizedTerm)
	{
		if (normalizedTerm.Length < 3 || string.IsNullOrWhiteSpace(value))
		{
			return false;
		}
		if (NormalizeIdentifier(value).Equals(normalizedTerm, StringComparison.Ordinal))
		{
			return true;
		}
		StringBuilder stringBuilder = new StringBuilder();
		string text = value.ToLowerInvariant();
		foreach (char c in text)
		{
			if (char.IsLetterOrDigit(c))
			{
				stringBuilder.Append(c);
			}
			else if (stringBuilder.Length > 0)
			{
				if (stringBuilder.ToString().Equals(normalizedTerm, StringComparison.Ordinal))
				{
					return true;
				}
				stringBuilder.Clear();
			}
		}
		return stringBuilder.Length > 0 && stringBuilder.ToString().Equals(normalizedTerm, StringComparison.Ordinal);
	}

	private static string NormalizeIdentifier(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "";
		}
		StringBuilder stringBuilder = new StringBuilder(value.Length);
		string text = value.ToLowerInvariant();
		foreach (char c in text)
		{
			if (char.IsLetterOrDigit(c))
			{
				stringBuilder.Append(c);
			}
		}
		return stringBuilder.ToString();
	}

	private static void EvaluateRuntimeAssemblyCluster(List<ScanFindingDto> findings)
	{
		List<string> list = (from f in findings
			where IsRuntimeAssemblyObservation(f.Code) && !IsFrameworkAssembly(f.Subject, f.Evidence) && !IsKnownBenignRuntimeObservation(f)
			select (f.Subject ?? "").Trim() into x
			where x.Length > 0
			select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
		if (list.Count >= 3)
		{
			AddUnique(findings, Finding("Runtime", "RUNTIME_UNKNOWN_ASSEMBLY_CLUSTER", AntiCheatSeverity.Low, "Several distinct post-baseline assemblies could not be associated with the game runtime or a reported loaded mod. This is supporting evidence only.", list.Count.ToString(), "assemblies=" + string.Join(",", list)));
		}
	}

	internal static int ComputeRiskScore(IEnumerable<ScanFindingDto> findings)
	{
		Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		int num = 0;
		foreach (ScanFindingDto finding in findings)
		{
			string text = finding.Code ?? "";
			int num2 = RiskPoints(finding);
			if (num2 > 0)
			{
				int value;
				if (IsPacketGuardMovementRiskFamily(text))
				{
					num = Math.Max(num, num2);
				}
				else if (!dictionary.TryGetValue(text, out value) || num2 > value)
				{
					dictionary[text] = num2;
				}
			}
		}
		return Math.Clamp(dictionary.Values.Sum() + num, 0, 100);
	}

	internal static int RiskPoints(ScanFindingDto finding)
	{
		string text = finding.Code ?? "";
		if (AntiCheatObservationPolicy.IsReviewOnly(text))
		{
			return 0;
		}
		if (text.Equals("CLIENT_MISSING", StringComparison.OrdinalIgnoreCase) || text.Equals("SCAN_TIMEOUT", StringComparison.OrdinalIgnoreCase) || text.Equals("HEARTBEAT_TIMEOUT", StringComparison.OrdinalIgnoreCase) || text.Equals("PROTOCOL_MISMATCH", StringComparison.OrdinalIgnoreCase) || text.Equals("SCANNER_CAPABILITY_MISSING", StringComparison.OrdinalIgnoreCase) || text.Equals("SCAN_RETRY", StringComparison.OrdinalIgnoreCase))
		{
			return 0;
		}
		if (IsRuntimeAssemblyObservation(text))
		{
			return 0;
		}
		if (text.Equals("PACKET_UNVERIFIED_TELEPORT_PATTERN", StringComparison.OrdinalIgnoreCase))
		{
			return 0;
		}
		if (text.Equals("RUNTIME_DIGEST_CHANGED", StringComparison.OrdinalIgnoreCase) || text.Equals("RUNTIME_DIGEST_CHANGED_WITHOUT_GENERATION", StringComparison.OrdinalIgnoreCase))
		{
			return 2;
		}
		if (text.Equals("HOTKEY_SUSPICIOUS_NAME", StringComparison.OrdinalIgnoreCase) || text.Equals("CLIENT_COMMAND_SUSPICIOUS", StringComparison.OrdinalIgnoreCase) || text.Equals("RUNTIME_SURFACE_CHANGED", StringComparison.OrdinalIgnoreCase))
		{
			return 1;
		}
		if (text.Equals("HOTKEY_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase) || text.Equals("CLIENT_COMMAND_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase))
		{
			return 5;
		}
		if (text.Equals("HOTKEY_BLACKLIST_OWNER_MATCH", StringComparison.OrdinalIgnoreCase))
		{
			return 8;
		}
		if (text.Equals("MOD_DLL_SET_MISMATCH", StringComparison.OrdinalIgnoreCase) || text.Equals("ASSEMBLY_SIZE_MISMATCH", StringComparison.OrdinalIgnoreCase))
		{
			return 5;
		}
		if (text.Equals("FORBIDDEN_ASSEMBLY_SIZE_MATCH", StringComparison.OrdinalIgnoreCase))
		{
			return 2;
		}
		if (text.Equals("MODDB_PACKAGE_MISMATCH", StringComparison.OrdinalIgnoreCase) || text.Equals("MODDB_DLL_SET_MISMATCH", StringComparison.OrdinalIgnoreCase) || text.Equals("MODDB_ASSEMBLY_MISMATCH", StringComparison.OrdinalIgnoreCase) || text.Equals("MODDB_CONTENT_MISMATCH", StringComparison.OrdinalIgnoreCase))
		{
			return 0;
		}
		if (text.Equals("PROTECTED_ASSET_SET_MISMATCH", StringComparison.OrdinalIgnoreCase) || text.Equals("PROTECTED_ASSET_DIGEST_MISMATCH", StringComparison.OrdinalIgnoreCase) || text.Equals("PROTECTED_ASSET_MISSING", StringComparison.OrdinalIgnoreCase))
		{
			return 8;
		}
		AntiCheatSeverity antiCheatSeverity = (AntiCheatSeverity)Math.Clamp(finding.Severity, 0, 4);
		if (1 == 0)
		{
		}
		int result = antiCheatSeverity switch
		{
			AntiCheatSeverity.Critical => 50, 
			AntiCheatSeverity.High => 25, 
			AntiCheatSeverity.Moderate => 10, 
			AntiCheatSeverity.Low => 3, 
			_ => 0, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	internal static bool IsPacketGuardMovementRiskFamily(string? code)
	{
		return string.Equals(code, "PACKET_UNAUTHORIZED_MOVEMENT", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "PACKET_SPEED_HACK", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "PACKET_ACCELERATION_HACK", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "PACKET_NOCLIP", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "PACKET_MOUNT_MOVEMENT_HACK", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsKnownVanillaClientCommandObservation(ScanFindingDto? finding)
	{
		if (finding == null)
		{
			return false;
		}
		if (!string.Equals(finding.Code, "CLIENT_COMMAND_SUSPICIOUS", StringComparison.OrdinalIgnoreCase) && !string.Equals(finding.Code, "CLIENT_COMMAND_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string text = (finding.Subject ?? "").Trim();
		if (text.Length == 0)
		{
			return false;
		}
		string text2 = text.Split(new char[4] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
		text2 = text2.TrimStart(new char[2] { '.', '/' }).Trim();
		return text2.Equals("noclip", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsKnownBenignRuntimeObservation(ScanFindingDto? finding)
	{
		if (finding == null || !string.Equals(finding.Code, "RUNTIME_UNASSOCIATED_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		return IsKnownBenignRuntimeAssembly(finding.Subject, finding.Evidence);
	}

	private static bool IsKnownBenignRuntimeAssembly(string? subject, string? evidence)
	{
		if (CleanAssemblyName(subject).Equals("AnimatedGif", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string[] array = (evidence ?? "").Split(new char[5] { ';', ',', '|', ' ', '=' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string value in array)
		{
			if (CleanAssemblyName(value).Equals("AnimatedGif", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
		static string CleanAssemblyName(string? text2)
		{
			string text = (text2 ?? "").Trim();
			int num = Math.Max(text.LastIndexOf('/'), text.LastIndexOf('\\'));
			if (num >= 0 && num + 1 < text.Length)
			{
				text = text.Substring(num + 1);
			}
			if (text.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
			{
				string text3 = text;
				text = text3.Substring(0, text3.Length - 4);
			}
			return text;
		}
	}

	private static bool IsRuntimeAssemblyObservation(string? code)
	{
		return string.Equals(code, "RUNTIME_UNASSOCIATED_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "RUNTIME_DYNAMIC_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "DUPLICATE_ASSEMBLY_IDENTITY", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsFrameworkAssembly(string? subject, string? evidence)
	{
		string name = Clean(subject);
		if (LooksFramework(name))
		{
			return true;
		}
		string[] array = (evidence ?? "").Split(new char[5] { ';', ',', '|', ' ', '=' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string value in array)
		{
			if (LooksFramework(Clean(value)))
			{
				return true;
			}
		}
		return false;
		static string Clean(string? text2)
		{
			string text = (text2 ?? "").Trim();
			int num = Math.Max(text.LastIndexOf('/'), text.LastIndexOf('\\'));
			if (num >= 0 && num + 1 < text.Length)
			{
				text = text.Substring(num + 1);
			}
			if (text.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
			{
				string text3 = text;
				text = text3.Substring(0, text3.Length - 4);
			}
			return text;
		}
		static bool LooksFramework(string text)
		{
			if (text.Length == 0)
			{
				return false;
			}
			return text.Equals("System", StringComparison.OrdinalIgnoreCase) || text.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) || text.StartsWith("SQLitePCLRaw.", StringComparison.OrdinalIgnoreCase) || text.Equals("SQLitePCLRaw", StringComparison.OrdinalIgnoreCase) || text.Equals("Accessibility", StringComparison.OrdinalIgnoreCase) || text.Equals("netstandard", StringComparison.OrdinalIgnoreCase) || text.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) || text.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase) || text.Equals("PresentationCore", StringComparison.OrdinalIgnoreCase) || text.Equals("PresentationFramework", StringComparison.OrdinalIgnoreCase) || text.Equals("Anonymously Hosted DynamicMethods Assembly", StringComparison.OrdinalIgnoreCase);
		}
	}

	private void EvaluateFingerprintBlacklist(IReadOnlyList<ClientModDto> mods, IReadOnlyList<ClientAssemblyDto> assemblies, List<ScanFindingDto> findings)
	{
		HashSet<string> activeForbiddenIds = config.ForbiddenModIds.Where((string x) => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (AntiCheatForbiddenFingerprint item in config.ForbiddenFingerprints.Where((AntiCheatForbiddenFingerprint x) => activeForbiddenIds.Contains(x.ModId ?? "")))
		{
			string originalId = item.ModId ?? "";
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (!string.IsNullOrWhiteSpace(item.PackageSha256))
			{
				hashSet.Add(item.PackageSha256);
			}
			if (!string.IsNullOrWhiteSpace(item.ContentSha256))
			{
				hashSet2.Add(item.ContentSha256);
			}
			foreach (AntiCheatManualFingerprintEntry item2 in item.ManualEntries ?? new List<AntiCheatManualFingerprintEntry>())
			{
				if (!string.IsNullOrWhiteSpace(item2.PackageSha256))
				{
					hashSet.Add(item2.PackageSha256);
				}
				if (!string.IsNullOrWhiteSpace(item2.ContentSha256))
				{
					hashSet2.Add(item2.ContentSha256);
				}
			}
			foreach (AntiCheatReleaseFingerprint item3 in item.Releases ?? new List<AntiCheatReleaseFingerprint>())
			{
				if (!string.IsNullOrWhiteSpace(item3.PackageSha256))
				{
					hashSet.Add(item3.PackageSha256);
				}
				if (!string.IsNullOrWhiteSpace(item3.ContentSha256))
				{
					hashSet2.Add(item3.ContentSha256);
				}
			}
			foreach (ClientModDto mod in mods.Where((ClientModDto m) => !string.Equals(m.ModId, originalId, StringComparison.OrdinalIgnoreCase)))
			{
				bool content = hashSet2.Any((string x) => HashEq(mod.ContentSha256, x));
				bool package = hashSet.Any((string x) => HashEq(mod.Sha256, x));
				if (content || package)
				{
					string value = (content ? "normalized-content-sha256" : "package-sha256");
					AntiCheatManualFingerprintEntry antiCheatManualFingerprintEntry = (item.ManualEntries ?? new List<AntiCheatManualFingerprintEntry>()).FirstOrDefault((AntiCheatManualFingerprintEntry entry) => (content && HashEq(mod.ContentSha256, entry.ContentSha256)) || (package && HashEq(mod.Sha256, entry.PackageSha256)));
					string value2 = ((antiCheatManualFingerprintEntry == null) ? "" : (";source=manual;entry=" + antiCheatManualFingerprintEntry.Label));
					AddUnique(findings, Finding("FingerprintPolicy", "FORBIDDEN_MOD_FINGERPRINT", AntiCheatSeverity.Critical, "A loaded mod package matches a blacklisted fingerprint under a different mod ID.", mod.ModId, $"blacklisted-id={originalId};match={value}{value2}"));
				}
			}
			List<string> source = (from a in (item.Releases ?? new List<AntiCheatReleaseFingerprint>()).SelectMany((AntiCheatReleaseFingerprint r) => r.Assemblies ?? new List<AntiCheatReleaseAssemblyFingerprint>())
				select a.Sha256 into x
				where !string.IsNullOrWhiteSpace(x)
				select x).ToList();
			List<string> source2 = (from x in (item.ManualEntries ?? new List<AntiCheatManualFingerprintEntry>()).SelectMany((AntiCheatManualFingerprintEntry x) => x.AssemblySha256s ?? new List<string>())
				where !string.IsNullOrWhiteSpace(x)
				select x).ToList();
			List<string> source3 = (from x in (item.ManualEntries ?? new List<AntiCheatManualFingerprintEntry>()).SelectMany((AntiCheatManualFingerprintEntry x) => x.AssemblyMvids ?? new List<string>())
				where !string.IsNullOrWhiteSpace(x)
				select x).ToList();
			List<string> source4 = (from x in (item.ManualEntries ?? new List<AntiCheatManualFingerprintEntry>()).SelectMany((AntiCheatManualFingerprintEntry x) => x.AssemblyStructureSha256s ?? new List<string>())
				where !string.IsNullOrWhiteSpace(x)
				select x).ToList();
			List<string> source5 = (from x in (item.ManualEntries ?? new List<AntiCheatManualFingerprintEntry>()).SelectMany((AntiCheatManualFingerprintEntry x) => x.AssemblyBehaviorSha256s ?? new List<string>())
				where !string.IsNullOrWhiteSpace(x)
				select x).ToList();
			foreach (ClientAssemblyDto asm in assemblies)
			{
				bool flag = item.AssemblySha256s.Any((string x) => HashEq(x, asm.Sha256)) || source.Any((string x) => HashEq(x, asm.Sha256)) || source2.Any((string x) => HashEq(x, asm.Sha256));
				bool flag2 = item.AssemblyMvids.Any((string x) => string.Equals(x, asm.ModuleVersionId, StringComparison.OrdinalIgnoreCase)) || source3.Any((string x) => string.Equals(x, asm.ModuleVersionId, StringComparison.OrdinalIgnoreCase));
				bool flag3 = item.AssemblyStructureSha256s.Any((string x) => HashEq(x, asm.StructureSha256)) || source4.Any((string x) => HashEq(x, asm.StructureSha256));
				bool flag4 = item.ModSystemTypeNames.Any((string x) => (asm.ModSystemTypeNames ?? new List<string>()).Contains(x, StringComparer.Ordinal));
				bool flag5 = item.AssemblyNames.Any((string x) => string.Equals(x, asm.AssemblyName, StringComparison.OrdinalIgnoreCase));
				bool flag6 = (asm.AssociatedModIds ?? new List<string>()).Any((string x) => string.Equals(x, originalId, StringComparison.OrdinalIgnoreCase));
				if (!flag6 && asm.FileSizeBytes > 0 && (item.Source ?? "").Contains("ModDB", StringComparison.OrdinalIgnoreCase))
				{
					var anon = (item.Releases ?? new List<AntiCheatReleaseFingerprint>()).SelectMany((AntiCheatReleaseFingerprint release) => (release.Assemblies ?? new List<AntiCheatReleaseAssemblyFingerprint>()).Select((AntiCheatReleaseAssemblyFingerprint reference) => new
					{
						Release = release,
						Reference = reference
					})).FirstOrDefault(x => x.Reference != null && x.Reference.FileSizeBytes > 0 && string.Equals(x.Reference.AssemblyName, asm.AssemblyName, StringComparison.OrdinalIgnoreCase) && x.Reference.FileSizeBytes == asm.FileSizeBytes);
					if (anon != null)
					{
						AddUnique(findings, Finding("FingerprintPolicy", "FORBIDDEN_ASSEMBLY_SIZE_MATCH", AntiCheatSeverity.Low, "A loaded assembly name and exact byte length match an official stored Mod DB release of a blacklisted mod. Size is supporting evidence only and is not sufficient by itself.", asm.AssemblyName, $"blacklisted-id={originalId};release={anon.Release.Version};reference-assembly={anon.Reference.AssemblyName};bytes={asm.FileSizeBytes};reported-mods={string.Join(",", asm.AssociatedModIds ?? new List<string>())}"));
					}
				}
				if ((flag | flag2 | flag3 | flag4 | flag5) && (!flag6 || !config.ForbiddenModIds.Contains(originalId, StringComparer.OrdinalIgnoreCase)))
				{
					string value3;
					if (flag)
					{
						value3 = "assembly-sha256";
					}
					else if (flag2)
					{
						value3 = "assembly-mvid";
					}
					else if (flag3)
					{
						value3 = "assembly-structure";
					}
					else
					{
						value3 = (flag4 ? "modsystem-type" : "assembly-name");
					}
					AntiCheatManualFingerprintEntry antiCheatManualFingerprintEntry2 = (item.ManualEntries ?? new List<AntiCheatManualFingerprintEntry>()).FirstOrDefault((AntiCheatManualFingerprintEntry entry) => (entry.AssemblySha256s ?? new List<string>()).Any((string x) => HashEq(x, asm.Sha256)) || (entry.AssemblyMvids ?? new List<string>()).Any((string x) => string.Equals(x, asm.ModuleVersionId, StringComparison.OrdinalIgnoreCase)) || (entry.AssemblyStructureSha256s ?? new List<string>()).Any((string x) => HashEq(x, asm.StructureSha256)));
					string value4 = ((antiCheatManualFingerprintEntry2 == null) ? "" : (";source=manual;entry=" + antiCheatManualFingerprintEntry2.Label));
					AddUnique(findings, Finding("FingerprintPolicy", "FORBIDDEN_ASSEMBLY_FINGERPRINT", (flag | flag2 | flag3 | flag4) ? AntiCheatSeverity.Critical : AntiCheatSeverity.High, "A loaded assembly matches a blacklisted mod fingerprint even though its reported mod ID differs.", asm.AssemblyName, $"blacklisted-id={originalId};match={value3};reported-mods={string.Join(",", asm.AssociatedModIds ?? new List<string>())}{value4}"));
				}
				bool flag7 = item.AssemblyBehaviorSha256s.Any((string x) => HashEq(x, asm.BehaviorSha256)) || source5.Any((string x) => HashEq(x, asm.BehaviorSha256)) || (item.Releases ?? new List<AntiCheatReleaseFingerprint>()).SelectMany((AntiCheatReleaseFingerprint r) => r.Assemblies ?? new List<AntiCheatReleaseAssemblyFingerprint>()).Any((AntiCheatReleaseAssemblyFingerprint x) => HashEq(x.BehaviorSha256, asm.BehaviorSha256));
				if (flag7 && !flag6)
				{
					AddUnique(findings, Finding("BehaviorPolicy", "FORBIDDEN_ASSEMBLY_BEHAVIOR", AntiCheatSeverity.High, "A loaded assembly has the same identifier-independent IL behavior shape as a blacklisted mod build.", asm.AssemblyName, $"blacklisted-id={originalId};match=il-behavior-sha256;behavior-tokens={asm.BehaviorTokenCount};reported-mods={string.Join(",", asm.AssociatedModIds ?? new List<string>())}"));
				}
				if (!config.SimilarityDetectionEnabled | flag6)
				{
					continue;
				}
				int num = 0;
				int num2 = 0;
				string value5 = "";
				string value6 = "";
				string value7 = "";
				string value8 = "";
				int value9 = 0;
				int value10 = 0;
				int num3 = 0;
				int num4 = 0;
				string value11 = "";
				string value12 = "";
				bool flag8 = false;
				int num5 = Math.Max(82, Math.Min(95, config.SimilarityReviewThresholdPercent + 8));
				int num6 = Math.Max(94, Math.Min(99, config.SimilarityHighThresholdPercent + 4));
				foreach (AntiCheatReleaseFingerprint item4 in item.Releases ?? new List<AntiCheatReleaseFingerprint>())
				{
					foreach (AntiCheatReleaseAssemblyFingerprint item5 in item4.Assemblies ?? new List<AntiCheatReleaseAssemblyFingerprint>())
					{
						int num7 = 0;
						int num8 = 0;
						if (asm.StructureTokenCount >= 20 && item5.StructureTokenCount >= 20 && TokenCountCompatible(asm.StructureTokenCount, item5.StructureTokenCount, 0.55) && (asm.SimilarityMinHash?.Count ?? 0) >= 64 && (item5.SimilarityMinHash?.Count ?? 0) >= 64)
						{
							num7 = StructuralSimilarity.SimilarityPercent(asm.SimilarityMinHash, item5.SimilarityMinHash);
							if (num7 > num)
							{
								num = num7;
								value5 = item4.Version ?? "";
								value6 = item5.AssemblyName ?? "";
								value9 = item5.StructureTokenCount;
							}
						}
						if (asm.BehaviorTokenCount >= 12 && item5.BehaviorTokenCount >= 12 && TokenCountCompatible(asm.BehaviorTokenCount, item5.BehaviorTokenCount, 0.65) && (asm.BehaviorMinHash?.Count ?? 0) >= 64 && (item5.BehaviorMinHash?.Count ?? 0) >= 64)
						{
							num8 = StructuralSimilarity.SimilarityPercent(asm.BehaviorMinHash, item5.BehaviorMinHash);
							if (num8 > num2)
							{
								num2 = num8;
								value7 = item4.Version ?? "";
								value8 = item5.AssemblyName ?? "";
								value10 = item5.BehaviorTokenCount;
							}
						}
						bool flag9 = HashEq(item5.BehaviorSha256, asm.BehaviorSha256);
						if (num7 >= config.SimilarityReviewThresholdPercent && (flag9 || num8 >= num6))
						{
							int num9 = num7 + (flag9 ? 100 : num8);
							int num10 = num3 + (flag8 ? 100 : num4);
							if (num9 > num10)
							{
								num3 = num7;
								num4 = num8;
								value11 = item4.Version ?? "";
								value12 = item5.AssemblyName ?? "";
								flag8 = flag9;
							}
						}
					}
				}
				if (num >= config.SimilarityReviewThresholdPercent)
				{
					AntiCheatSeverity severity = ((num >= config.SimilarityHighThresholdPercent) ? AntiCheatSeverity.High : AntiCheatSeverity.Moderate);
					AddUnique(findings, Finding("SimilarityPolicy", "FORBIDDEN_ASSEMBLY_SIMILARITY", severity, "A loaded mod assembly is structurally similar to a recent release of a blacklisted mod. Similarity is supporting evidence, not an exact fingerprint match.", asm.AssemblyName, $"blacklisted-id={originalId};release={value5};reference-assembly={value6};similarity={num}%;tokens={asm.StructureTokenCount}/{value9}"));
				}
				if (num2 >= num5)
				{
					AntiCheatSeverity severity2 = ((num2 >= num6) ? AntiCheatSeverity.High : AntiCheatSeverity.Moderate);
					AddUnique(findings, Finding("BehaviorPolicy", "FORBIDDEN_ASSEMBLY_BEHAVIOR_SIMILARITY", severity2, "A loaded assembly has identifier-independent IL behavior similar to a recent blacklisted release. This survives simple renames/repacking but remains supporting evidence.", asm.AssemblyName, $"blacklisted-id={originalId};release={value7};reference-assembly={value8};behavior-similarity={num2}%;behavior-tokens={asm.BehaviorTokenCount}/{value10}"));
				}
				if (num3 >= config.SimilarityReviewThresholdPercent && (flag8 || num4 >= num6))
				{
					AddUnique(findings, Finding("CorrelationPolicy", "FORBIDDEN_REPACK_CORRELATED", AntiCheatSeverity.Critical, "Independent structural and identifier-free IL behavior signals agree on the same stored blacklisted release/assembly reference, strongly indicating a renamed or repacked build.", asm.AssemblyName, $"blacklisted-id={originalId};release={value11};reference-assembly={value12};structure-similarity={num3}%;behavior-similarity={num4}%;behavior-exact={(flag8 ? "true" : "false")};reported-mods={string.Join(",", asm.AssociatedModIds ?? new List<string>())}"));
				}
				else if (flag7 && num >= config.SimilarityHighThresholdPercent)
				{
					AddUnique(findings, Finding("CorrelationPolicy", "FORBIDDEN_REPACK_CORRELATED", AntiCheatSeverity.Critical, "An exact identifier-free IL behavior fingerprint and a high structural similarity both point to the same blacklisted mod family.", asm.AssemblyName, $"blacklisted-id={originalId};correlation-scope=forbidden-mod;structure-similarity={num}%;behavior-exact=true;reported-mods={string.Join(",", asm.AssociatedModIds ?? new List<string>())}"));
				}
			}
		}
	}

	private void EvaluateCanonicalModConsistency(IReadOnlyList<ClientModDto> mods, IReadOnlyList<ClientAssemblyDto> assemblies, IReadOnlyDictionary<string, AntiCheatCanonicalModFingerprint>? canonicalMods, List<ScanFindingDto> findings)
	{
		if (!config.CanonicalServerModIntegrityEnabled || canonicalMods == null || canonicalMods.Count == 0)
		{
			return;
		}
		foreach (ClientModDto item in mods.Where((ClientModDto x) => x != null && !string.IsNullOrWhiteSpace(x.ModId)))
		{
			string id = (item.ModId ?? "").Trim();
			if (id.Equals("newfiesanticheatclient", StringComparison.OrdinalIgnoreCase) || id.Equals("game", StringComparison.OrdinalIgnoreCase) || id.Equals("survival", StringComparison.OrdinalIgnoreCase) || id.Equals("creative", StringComparison.OrdinalIgnoreCase) || !canonicalMods.TryGetValue(id, out AntiCheatCanonicalModFingerprint value) || value == null)
			{
				continue;
			}
			if (!string.Equals((item.Version ?? "").Trim(), (value.Version ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
			{
				AddUnique(findings, Finding("CanonicalPolicy", "MOD_SERVER_VERSION_MISMATCH", AntiCheatSeverity.High, "A server-shared mod reports a different semantic version from the copy loaded by the server.", id, $"clientVersion={item.Version};serverVersion={value.Version};serverFile={value.SourceFileName}"));
				continue;
			}
			bool flag = IsSha256(item.ContentSha256) && IsSha256(value.ContentSha256);
			bool flag2 = IsSha256(item.Sha256) && IsSha256(value.PackageSha256);
			bool flag3 = flag && !HashEq(item.ContentSha256, value.ContentSha256);
			bool flag4 = flag2 && !HashEq(item.Sha256, value.PackageSha256);
			bool flag5 = item.DllCount > 0 || value.DllCount > 0;
			bool flag6 = item.DllBytes > 0 || value.DllBytes > 0;
			bool flag7 = (flag5 && item.DllCount != value.DllCount) || (flag6 && item.DllBytes != value.DllBytes);
			if (flag7)
			{
				AddUnique(findings, Finding("CanonicalPolicy", "MOD_DLL_SET_MISMATCH", AntiCheatSeverity.Moderate, "A server-shared mod has the expected ID/version but its DLL layout differs from the server-loaded copy. This is supporting evidence unless an exact content or DLL hash also disagrees.", id, $"version={item.Version};clientDllCount={item.DllCount};expectedDllCount={value.DllCount};clientDllBytes={item.DllBytes};expectedDllBytes={value.DllBytes};clientPackageBytes={item.PackageBytes};expectedPackageBytes={value.PackageBytes};serverFile={value.SourceFileName}"));
			}
			List<ClientAssemblyDto> source = assemblies.Where((ClientAssemblyDto a) => (a.AssociatedModIds ?? new List<string>()).Any((string x) => string.Equals(x, id, StringComparison.OrdinalIgnoreCase))).ToList();
			bool flag8 = false;
			foreach (AntiCheatCanonicalDllFingerprint expectedDll in value.Assemblies ?? new List<AntiCheatCanonicalDllFingerprint>())
			{
				if (expectedDll == null || string.IsNullOrWhiteSpace(expectedDll.FileName))
				{
					continue;
				}
				ClientAssemblyDto clientAssemblyDto = source.FirstOrDefault((ClientAssemblyDto a) => string.Equals(a.FileName, expectedDll.FileName, StringComparison.OrdinalIgnoreCase));
				if (clientAssemblyDto != null)
				{
					int num = (clientAssemblyDto.AssociatedModIds ?? new List<string>()).Where((string x) => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
					if (num == 1 && IsSha256(clientAssemblyDto.Sha256) && IsSha256(expectedDll.Sha256) && !HashEq(clientAssemblyDto.Sha256, expectedDll.Sha256))
					{
						flag8 = true;
						AddUnique(findings, Finding("CanonicalPolicy", "ASSEMBLY_SERVER_HASH_MISMATCH", AntiCheatSeverity.High, "A confidently associated client DLL SHA-256 differs from the exact DLL loaded by the server for the same shared mod/version.", clientAssemblyDto.AssemblyName, $"modId={id};file={clientAssemblyDto.FileName};clientSha256={clientAssemblyDto.Sha256};serverSha256={expectedDll.Sha256};clientBytes={clientAssemblyDto.FileSizeBytes};serverBytes={expectedDll.FileSizeBytes}"));
					}
					else if (expectedDll.FileSizeBytes > 0 && clientAssemblyDto.FileSizeBytes > 0 && clientAssemblyDto.FileSizeBytes != expectedDll.FileSizeBytes)
					{
						AddUnique(findings, Finding("CanonicalPolicy", "ASSEMBLY_SIZE_MISMATCH", AntiCheatSeverity.Moderate, "A loaded DLL associated with a server-shared mod has a different byte length from the server baseline. Exact SHA-256 comparison takes precedence when available.", clientAssemblyDto.AssemblyName, $"modId={id};file={clientAssemblyDto.FileName};clientBytes={clientAssemblyDto.FileSizeBytes};expectedBytes={expectedDll.FileSizeBytes};sha256={clientAssemblyDto.Sha256}"));
					}
				}
			}
			if (flag3 || flag8 || (flag4 && !flag) || flag7)
			{
				AddUnique(findings, Finding("CanonicalPolicy", "MOD_CANONICAL_MISMATCH", (flag3 | flag8) ? AntiCheatSeverity.High : AntiCheatSeverity.Moderate, (flag3 | flag8) ? "A server-shared mod reports the expected ID/version but does not match the exact server-loaded content/DLL baseline." : "A server-shared mod has packaging or DLL-layout differences from the server-loaded copy, but no authoritative content mismatch was available.", id, $"version={item.Version};clientContent={item.ContentSha256};serverContent={value.ContentSha256};clientPackage={item.Sha256};serverPackage={value.PackageSha256};clientPackageBytes={item.PackageBytes};serverPackageBytes={value.PackageBytes};clientDllCount={item.DllCount};serverDllCount={value.DllCount};clientDllBytes={item.DllBytes};serverDllBytes={value.DllBytes};authoritativeDllMismatch={flag8.ToString().ToLowerInvariant()};serverFile={value.SourceFileName}"));
			}
		}
	}

	private static void EvaluateAssemblyConsistency(IReadOnlyList<ClientModDto> mods, IReadOnlyList<ClientAssemblyDto> assemblies, List<ScanFindingDto> findings)
	{
		HashSet<string> hashSet = (from x in mods
			where !string.IsNullOrWhiteSpace(x.ModId)
			select x.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (ClientAssemblyDto assembly in assemblies)
		{
			foreach (string item in assembly.AssociatedModIds ?? new List<string>())
			{
				if (!string.IsNullOrWhiteSpace(item) && !hashSet.Contains(item))
				{
					AddUnique(findings, Finding("AssemblyCorrelation", "UNREPORTED_MOD_ASSEMBLY", AntiCheatSeverity.High, "A loaded mod assembly references a mod ID that is absent from the reported mod inventory.", item, "assembly=" + assembly.AssemblyName + ";sha256=" + assembly.Sha256));
				}
			}
		}
	}

	private static void EvaluateCoreAssemblyIntegrity(IReadOnlyList<ClientAssemblyDto> assemblies, IReadOnlyDictionary<string, AntiCheatCoreAssemblyFingerprint>? expected, List<ScanFindingDto> findings)
	{
		if (expected == null || expected.Count == 0)
		{
			return;
		}
		Dictionary<string, ClientAssemblyDto> dictionary = assemblies.Where((ClientAssemblyDto a) => a != null && !string.IsNullOrWhiteSpace(a.AssemblyName)).GroupBy((ClientAssemblyDto a) => a.AssemblyName, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, ClientAssemblyDto> g) => g.Key, (IGrouping<string, ClientAssemblyDto> g) => g.First(), StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, AntiCheatCoreAssemblyFingerprint> item in expected)
		{
			AntiCheatCoreAssemblyFingerprint value = item.Value;
			if (!dictionary.TryGetValue(item.Key, out var value2))
			{
				AddUnique(findings, Finding("StartupIntegrity", "CORE_ASSEMBLY_MISSING", AntiCheatSeverity.Moderate, "A shared core assembly expected by the server is missing from the client's startup assembly census.", item.Key, "expectedVersion=" + value.Version + ";expectedSha256=" + value.Sha256));
			}
			else if (!string.IsNullOrWhiteSpace(value.Version) && !string.IsNullOrWhiteSpace(value2.Version) && !string.Equals(value.Version, value2.Version, StringComparison.OrdinalIgnoreCase))
			{
				AddUnique(findings, Finding("StartupIntegrity", "CORE_ASSEMBLY_VERSION_MISMATCH", AntiCheatSeverity.Moderate, "A shared core assembly reports a different version than the server baseline.", item.Key, "serverVersion=" + value.Version + ";clientVersion=" + value2.Version));
			}
			else if (!string.IsNullOrWhiteSpace(value.Sha256) && !string.IsNullOrWhiteSpace(value2.Sha256) && !HashEq(value.Sha256, value2.Sha256))
			{
				AddUnique(findings, Finding("StartupIntegrity", "CORE_ASSEMBLY_HASH_MISMATCH", AntiCheatSeverity.High, "A shared Vintage Story core assembly differs from the server's startup integrity baseline. This is strong integrity evidence but should still be reviewed for platform/build differences before disruptive enforcement.", item.Key, $"serverSha256={value.Sha256};clientSha256={value2.Sha256};serverMvid={value.ModuleVersionId};clientMvid={value2.ModuleVersionId}"));
			}
		}
	}

	private static void EvaluateHarmony(ClientHarmonyDto? harmony, List<ScanFindingDto> findings)
	{
		if (harmony == null)
		{
			return;
		}
		List<ClientHarmonyPatchDto> source = harmony.Patches ?? new List<ClientHarmonyPatchDto>();
		if (source.Any((ClientHarmonyPatchDto p) => string.Equals(p.Owner, "com.br3zzly.modmenu", StringComparison.OrdinalIgnoreCase)))
		{
			AddUnique(findings, Finding("HarmonyPolicy", "MODMENU_HARMONY_OWNER", AntiCheatSeverity.Critical, "A known ModMenu Harmony patch owner is active in the client process.", "com.br3zzly.modmenu", $"patches={source.Count((ClientHarmonyPatchDto p) => string.Equals(p.Owner, "com.br3zzly.modmenu", StringComparison.OrdinalIgnoreCase))}"));
		}
		foreach (IGrouping<string, ClientHarmonyPatchDto> item in source.GroupBy((ClientHarmonyPatchDto p) => p.PatchAssembly ?? "", StringComparer.OrdinalIgnoreCase))
		{
			if (string.IsNullOrWhiteSpace(item.Key))
			{
				continue;
			}
			List<string> list = (from x in item.Select(HarmonyCategory)
				where x.Length > 0
				select x).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList();
			if (list.Count >= 3)
			{
				List<string> values = (from x in item.SelectMany((ClientHarmonyPatchDto p) => p.PatchAssemblyModIds ?? new List<string>())
					where !string.IsNullOrWhiteSpace(x)
					select x).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).Take(16)
					.ToList();
				AddUnique(findings, Finding("HarmonyPolicy", "MODMENU_PATCH_CLUSTER", AntiCheatSeverity.High, "One loaded assembly patches a ModMenu-like cluster of movement, reach and mining code paths.", item.Key, "categories=" + string.Join(",", list) + ";mods=" + string.Join(",", values)));
			}
		}
	}

	private static string HarmonyCategory(ClientHarmonyPatchDto patch)
	{
		string text = patch.TargetType ?? "";
		string text2 = patch.TargetMethod ?? "";
		if (text.EndsWith("EntityControls", StringComparison.Ordinal) && text2.Equals("CalcMovementVectors", StringComparison.Ordinal))
		{
			return "movement";
		}
		if (text2.Equals("RayTraceForSelection", StringComparison.Ordinal) || text2.Equals("GetPickingRayByMouseCoordinates", StringComparison.Ordinal))
		{
			return "reach";
		}
		if (text2.Equals("OnGettingBroken", StringComparison.Ordinal) || text2.Equals("OnPlayerTryDestroyBlock", StringComparison.Ordinal))
		{
			return "mining";
		}
		if (text2.Contains("ReceiveDamage", StringComparison.OrdinalIgnoreCase) || text2.Contains("Attack", StringComparison.OrdinalIgnoreCase))
		{
			return "combat";
		}
		return "";
	}

	private void EvaluateClientIntegrity(IReadOnlyList<ClientAssemblyDto> assemblies, ClientIntegrityDto? integrity, List<ScanFindingDto> findings)
	{
		if (integrity == null)
		{
			AddUnique(findings, Finding("ServerIntegrity", "CLIENT_INTEGRITY_MISSING", AntiCheatSeverity.Critical, "The client did not provide its required build-integrity fingerprint.", "newfiesanticheatclient", "integrity-payload-missing"));
			return;
		}
		if (!string.Equals(integrity.AssemblyName, "NewfiesAntiCheatClient", StringComparison.OrdinalIgnoreCase))
		{
			AddUnique(findings, Finding("ServerIntegrity", "CLIENT_ASSEMBLY_IDENTITY_MISMATCH", AntiCheatSeverity.High, "The Client Integrity assembly identity is unexpected.", integrity.AssemblyName, "expected=NewfiesAntiCheatClient"));
		}
		ClientAssemblyDto clientAssemblyDto = assemblies.FirstOrDefault((ClientAssemblyDto a) => string.Equals(a.AssemblyName, integrity.AssemblyName, StringComparison.OrdinalIgnoreCase));
		if (clientAssemblyDto == null)
		{
			AddUnique(findings, Finding("ServerIntegrity", "CLIENT_INTEGRITY_CROSSCHECK_FAILED", AntiCheatSeverity.Critical, "The client build fingerprint has no matching loaded-assembly entry.", integrity.AssemblyName, "assembly-list-missing"));
		}
		else if ((!string.IsNullOrWhiteSpace(integrity.AssemblySha256) && !HashEq(clientAssemblyDto.Sha256, integrity.AssemblySha256)) || (!string.IsNullOrWhiteSpace(integrity.ModuleVersionId) && !string.Equals(clientAssemblyDto.ModuleVersionId, integrity.ModuleVersionId, StringComparison.OrdinalIgnoreCase)) || (integrity.AssemblyFileSizeBytes > 0 && clientAssemblyDto.FileSizeBytes != integrity.AssemblyFileSizeBytes))
		{
			AddUnique(findings, Finding("ServerIntegrity", "CLIENT_INTEGRITY_CROSSCHECK_FAILED", AntiCheatSeverity.Critical, "Independent client integrity and assembly scanner fingerprints disagree.", integrity.AssemblyName, "assembly-hash-mvid-or-size-mismatch"));
		}
		if (config.RequireApprovedClientBuild && config.ApprovedClientBuildHashes.Count > 0)
		{
			if (!config.ApprovedClientBuildHashes.Any((string h) => HashEq(h, integrity.AssemblySha256) || HashEq(h, integrity.PackageSha256)))
			{
				AddUnique(findings, Finding("ServerIntegrity", "CLIENT_BUILD_UNAPPROVED", AntiCheatSeverity.Critical, "The connected Client Integrity build does not match an approved server fingerprint.", integrity.AssemblySha256, "package=" + integrity.PackageSha256));
			}
		}
		else if (config.RequireApprovedClientBuild && config.ApprovedClientBuildHashes.Count == 0)
		{
			AddUnique(findings, Finding("ServerIntegrity", "CLIENT_BUILD_POLICY_EMPTY", AntiCheatSeverity.Moderate, "Approved-build enforcement is enabled but the server has no approved client hashes configured.", "newfiesanticheatclient", "configure-approved-hash"));
		}
	}

	private static bool TokenCountCompatible(int a, int b, double minRatio)
	{
		if (a <= 0 || b <= 0)
		{
			return false;
		}
		int num = Math.Min(a, b);
		int num2 = Math.Max(a, b);
		return num2 > 0 && (double)num / (double)num2 >= minRatio;
	}

	private static bool IsSha256(string? value)
	{
		string text = (value ?? "").Trim();
		return text.Length == 64 && text.All(Uri.IsHexDigit);
	}

	private static bool HashEq(string? a, string? b)
	{
		return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
	}

	private static void AddUnique(List<ScanFindingDto> list, ScanFindingDto finding)
	{
		if (!list.Any((ScanFindingDto x) => string.Equals(x.Code, finding.Code, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Subject, finding.Subject, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Evidence, finding.Evidence, StringComparison.OrdinalIgnoreCase)))
		{
			list.Add(finding);
		}
	}

	private static ScanFindingDto Finding(string scanner, string code, AntiCheatSeverity severity, string summary, string subject, string evidence)
	{
		return new ScanFindingDto
		{
			Scanner = scanner,
			Code = code,
			Severity = (int)severity,
			Summary = summary,
			Subject = (subject ?? ""),
			Evidence = evidence
		};
	}
}
