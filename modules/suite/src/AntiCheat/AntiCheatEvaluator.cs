using System;
using System.Collections.Generic;
using System.Linq;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Models;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatEvaluator
{
    private readonly AntiCheatConfig config;
    public AntiCheatEvaluator(AntiCheatConfig config) => this.config = config;

    public (AntiCheatPlayerStatus Status, int Score, List<ScanFindingDto> Findings) Evaluate(
        IReadOnlyList<ClientModDto> mods, IReadOnlyList<ScanFindingDto> clientFindings,
        IReadOnlyList<ClientAssemblyDto>? assemblies = null, ClientIntegrityDto? integrity = null,
        ClientHarmonyDto? harmony = null, IReadOnlyDictionary<string, AntiCheatCanonicalModFingerprint>? canonicalMods = null,
        IReadOnlyDictionary<string, AntiCheatCoreAssemblyFingerprint>? coreAssemblies = null)
    {
        assemblies ??= Array.Empty<ClientAssemblyDto>();
        var findings = (clientFindings ?? Array.Empty<ScanFindingDto>())
            .Select(NormalizeClientFinding)
            .ToList();
        var byId = mods.Where(m => !string.IsNullOrWhiteSpace(m.ModId)).GroupBy(m => m.ModId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (string id in config.RequiredClientModIds.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (!byId.ContainsKey(id)) AddUnique(findings, Finding("ServerPolicy", "REQUIRED_MOD_MISSING", AntiCheatSeverity.High, "A server-required client mod is missing.", id, "required-mod-id"));

        foreach (string id in config.ForbiddenModIds.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (byId.ContainsKey(id)) AddUnique(findings, Finding("ServerPolicy", "FORBIDDEN_MOD", AntiCheatSeverity.Critical, "A forbidden client mod is loaded.", id, "mod-id"));

        EvaluateFingerprintBlacklist(mods, assemblies, findings);
        EvaluateCanonicalModConsistency(mods, assemblies, canonicalMods, findings);
        EvaluateCoreAssemblyIntegrity(assemblies, coreAssemblies, findings);
        EvaluateAssemblyConsistency(mods, assemblies, findings);
        EvaluateClientIntegrity(assemblies, integrity, findings);
        EvaluateHarmony(harmony, findings);
        EvaluateRuntimeAssemblyCluster(findings);

        // Known benign late-loaded dependencies remain available in raw runtime telemetry,
        // but are not anti-cheat findings. In particular AnimatedGif is lazily loaded by
        // normal game/mod UI paths and should not inflate finding counts or correlation.
        findings.RemoveAll(IsKnownBenignRuntimeObservation);
        // Vintage Story registers a built-in client-side .noclip command. Command names are only
        // supporting evidence, and this exact vanilla command must never make every normal client
        // look suspicious. Keep generic noclip-related package/runtime evidence intact; only drop
        // the exact command-name observation.
        findings.RemoveAll(IsKnownVanillaClientCommandObservation);
        findings.RemoveAll(f =>
            string.Equals(f.Code, "RUNTIME_BLACKLIST_ALIAS_IGNORED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Code, "PROTECTED_ASSET_BASELINE_UNAVAILABLE", StringComparison.OrdinalIgnoreCase));

        int score = ComputeRiskScore(findings);
        AntiCheatPlayerStatus status = score >= 50 ? AntiCheatPlayerStatus.Suspicious : score >= 10 ? AntiCheatPlayerStatus.Review : AntiCheatPlayerStatus.Clean;
        return (status, score, findings);
    }


    private static ScanFindingDto NormalizeClientFinding(ScanFindingDto finding)
    {
        var normalized = new ScanFindingDto
        {
            Scanner = finding?.Scanner ?? "",
            Code = finding?.Code ?? "",
            Severity = finding?.Severity ?? 0,
            Summary = finding?.Summary ?? "",
            Evidence = finding?.Evidence ?? "",
            Subject = finding?.Subject ?? ""
        };

        // Backward-compatible false-positive guard for older Client Integrity builds. Blacklist
        // aliases are identifiers, not free-text phrases, so only accept complete identifier/token
        // matches. For example, blacklist alias "modmenu" must not match "RPVoice: Mod menu".
        if ((normalized.Code.Equals("HOTKEY_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase) ||
             normalized.Code.Equals("CLIENT_COMMAND_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase)) &&
            !IsStrictBlacklistRuntimeMatch(normalized))
        {
            normalized.Code = "RUNTIME_BLACKLIST_ALIAS_IGNORED";
            normalized.Severity = (int)AntiCheatSeverity.Info;
            normalized.Summary = "A broad blacklist runtime-name match from the client was ignored because the alias was not a complete identifier/token match.";
            return normalized;
        }

        // An empty server protected-asset baseline means the dedicated server could not expose
        // the client/render asset set. That is unavailable coverage, never evidence of tampering.
        if (normalized.Code.Equals("PROTECTED_ASSET_SET_MISMATCH", StringComparison.OrdinalIgnoreCase) &&
            EvidenceField(normalized.Evidence, "expectedCount").Equals("0", StringComparison.OrdinalIgnoreCase))
        {
            normalized.Code = "PROTECTED_ASSET_BASELINE_UNAVAILABLE";
            normalized.Severity = (int)AntiCheatSeverity.Info;
            normalized.Summary = "Protected-asset comparison was skipped because the server baseline was empty/unavailable.";
            return normalized;
        }

        // Loading ordinary .NET/framework assemblies is not cheat evidence. These frequently
        // appear as unassociated runtime assemblies because they are framework dependencies,
        // not Vintage Story mods. Keep them visible as telemetry but never turn them into risk.
        if (IsRuntimeAssemblyObservation(normalized.Code) && IsFrameworkAssembly(normalized.Subject, normalized.Evidence))
        {
            normalized.Severity = (int)AntiCheatSeverity.Info;
            normalized.Summary = "Normal framework/runtime assembly observation (informational only).";
            return normalized;
        }

        // A new/unassociated/dynamic assembly can be legitimate. It is useful evidence when
        // correlated with stronger findings, but by itself should not push a player to Review.
        if (IsRuntimeAssemblyObservation(normalized.Code))
            normalized.Severity = Math.Min(normalized.Severity, (int)AntiCheatSeverity.Low);

        if (normalized.Code.Equals("HOTKEY_SUSPICIOUS_NAME", StringComparison.OrdinalIgnoreCase) ||
            normalized.Code.Equals("CLIENT_COMMAND_SUSPICIOUS", StringComparison.OrdinalIgnoreCase) ||
            normalized.Code.Equals("RUNTIME_SURFACE_CHANGED", StringComparison.OrdinalIgnoreCase))
            normalized.Severity = Math.Min(normalized.Severity, (int)AntiCheatSeverity.Low);

        return normalized;
    }


    private static bool IsStrictBlacklistRuntimeMatch(ScanFindingDto finding)
    {
        string term = NormalizeIdentifier(EvidenceField(finding.Evidence, "blacklistTerm"));
        if (term.Length < 3) return false;

        if (finding.Code.Equals("HOTKEY_BLACKLIST_OWNER_MATCH", StringComparison.OrdinalIgnoreCase) &&
            EvidenceField(finding.Evidence, "ownerMatch").Equals("true", StringComparison.OrdinalIgnoreCase))
            return IdentifierMatches(EvidenceField(finding.Evidence, "handlerAssembly"), term);

        if (IdentifierMatches(finding.Subject, term)) return true;
        if (IdentifierMatches(EvidenceField(finding.Evidence, "name"), term)) return true;
        return false;
    }

    private static string EvidenceField(string? evidence, string key)
    {
        foreach (string part in (evidence ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0) continue;
            if (!part[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return part[(equals + 1)..].Trim();
        }
        return "";
    }

    private static bool IdentifierMatches(string? value, string normalizedTerm)
    {
        if (normalizedTerm.Length < 3 || string.IsNullOrWhiteSpace(value)) return false;
        if (NormalizeIdentifier(value).Equals(normalizedTerm, StringComparison.Ordinal)) return true;

        var token = new System.Text.StringBuilder();
        foreach (char c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                token.Append(c);
                continue;
            }
            if (token.Length > 0)
            {
                if (token.ToString().Equals(normalizedTerm, StringComparison.Ordinal)) return true;
                token.Clear();
            }
        }
        return token.Length > 0 && token.ToString().Equals(normalizedTerm, StringComparison.Ordinal);
    }

    private static string NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (char c in value.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    private static void EvaluateRuntimeAssemblyCluster(List<ScanFindingDto> findings)
    {
        var unknown = findings
            .Where(f => IsRuntimeAssemblyObservation(f.Code) &&
                        !IsFrameworkAssembly(f.Subject, f.Evidence) &&
                        !IsKnownBenignRuntimeObservation(f))
            .Select(f => (f.Subject ?? "").Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (unknown.Count < 3) return;
        AddUnique(findings, Finding(
            "Runtime",
            "RUNTIME_UNKNOWN_ASSEMBLY_CLUSTER",
            AntiCheatSeverity.Low,
            "Several distinct post-baseline assemblies could not be associated with the game runtime or a reported loaded mod. This is supporting evidence only.",
            unknown.Count.ToString(),
            "assemblies=" + string.Join(",", unknown)));
    }

    internal static int ComputeRiskScore(IEnumerable<ScanFindingDto> findings)
    {
        // Score the strongest instance of each noisy finding code instead of linearly adding
        // hundreds of equivalent observations. Exact policy/integrity evidence remains strong.
        // Individual runtime assembly observations are telemetry-only; EvaluateRuntimeAssemblyCluster
        // creates an explicit low-risk finding when several distinct unknown assemblies agree.
        var strongestByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int strongestMovementFamily = 0;
        foreach (var finding in findings)
        {
            string code = finding.Code ?? "";
            int points = RiskPoints(finding);
            if (points <= 0) continue;

            // Movement detectors observe different views of the same underlying position stream.
            // Treat them as one risk family so a mount-sync false positive cannot prime a later
            // teleport/speed anomaly for an automatic consequence. Independent Reach/Mining and
            // client-integrity evidence remain separate risk units.
            if (IsPacketGuardMovementRiskFamily(code))
            {
                strongestMovementFamily = Math.Max(strongestMovementFamily, points);
                continue;
            }

            if (!strongestByCode.TryGetValue(code, out int existing) || points > existing)
                strongestByCode[code] = points;
        }

        return Math.Clamp(strongestByCode.Values.Sum() + strongestMovementFamily, 0, 100);
    }

    internal static int RiskPoints(ScanFindingDto finding)
    {
        string code = finding.Code ?? "";
        if (AntiCheatObservationPolicy.IsReviewOnly(code)) return 0;

        // Connection/compatibility state is coverage information, not evidence of cheating.
        if (code.Equals("CLIENT_MISSING", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("SCAN_TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("HEARTBEAT_TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("PROTOCOL_MISMATCH", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("SCANNER_CAPABILITY_MISSING", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("SCAN_RETRY", StringComparison.OrdinalIgnoreCase))
            return 0;

        if (IsRuntimeAssemblyObservation(code))
            return 0;

        // Repeated unverified teleport-sized transitions are compatibility diagnostics. They are
        // intentionally re-baselined by PacketGuard and must not contribute to punishment risk.
        if (code.Equals("PACKET_UNVERIFIED_TELEPORT_PATTERN", StringComparison.OrdinalIgnoreCase))
            return 0;

        // Runtime digest churn alone is weak supporting evidence. Stronger tamper/integrity
        // findings are intentionally not included in this cap.
        if (code.Equals("RUNTIME_DIGEST_CHANGED", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("RUNTIME_DIGEST_CHANGED_WITHOUT_GENERATION", StringComparison.OrdinalIgnoreCase))
            return 2;

        // Runtime names are searchable indicators, not proof. Keep them below the normal Low
        // weight so a suspicious label cannot independently move a clean player into Review.
        if (code.Equals("HOTKEY_SUSPICIOUS_NAME", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("CLIENT_COMMAND_SUSPICIOUS", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("RUNTIME_SURFACE_CHANGED", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (code.Equals("HOTKEY_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("CLIENT_COMMAND_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase))
            return 5;
        if (code.Equals("HOTKEY_BLACKLIST_OWNER_MATCH", StringComparison.OrdinalIgnoreCase))
            return 8;
        if (code.Equals("MOD_DLL_SET_MISMATCH", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("ASSEMBLY_SIZE_MISMATCH", StringComparison.OrdinalIgnoreCase))
            return 5;
        if (code.Equals("FORBIDDEN_ASSEMBLY_SIZE_MATCH", StringComparison.OrdinalIgnoreCase))
            return 2;
        // ModDB verification is asynchronous external-reference evidence. Keep it visible for
        // investigation, but do not let a transient DB/cache/re-upload ambiguity change player
        // risk or startup admission by itself. Exact ModDB findings can still be reviewed and
        // explicitly enforced by an administrator if desired.
        if (code.Equals("MODDB_PACKAGE_MISMATCH", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("MODDB_DLL_SET_MISMATCH", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("MODDB_ASSEMBLY_MISMATCH", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("MODDB_CONTENT_MISMATCH", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (code.Equals("PROTECTED_ASSET_SET_MISMATCH", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("PROTECTED_ASSET_DIGEST_MISMATCH", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("PROTECTED_ASSET_MISSING", StringComparison.OrdinalIgnoreCase))
            return 8;

        return ((AntiCheatSeverity)Math.Clamp(finding.Severity, 0, 4)) switch
        {
            AntiCheatSeverity.Critical => 50,
            AntiCheatSeverity.High => 25,
            AntiCheatSeverity.Moderate => 10,
            AntiCheatSeverity.Low => 3,
            _ => 0
        };
    }

    internal static bool IsPacketGuardMovementRiskFamily(string? code) =>
        string.Equals(code, "PACKET_UNAUTHORIZED_MOVEMENT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "PACKET_SPEED_HACK", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "PACKET_ACCELERATION_HACK", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "PACKET_NOCLIP", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "PACKET_MOUNT_MOVEMENT_HACK", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownVanillaClientCommandObservation(ScanFindingDto? finding)
    {
        if (finding == null) return false;
        if (!string.Equals(finding.Code, "CLIENT_COMMAND_SUSPICIOUS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(finding.Code, "CLIENT_COMMAND_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase))
            return false;

        string subject = (finding.Subject ?? "").Trim();
        if (subject.Length == 0) return false;

        // FullName may include the command prefix and, on some API builds, a subcommand path.
        // Strip only the leading command prefix and compare the root command exactly.
        string root = subject.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                             .FirstOrDefault() ?? "";
        root = root.TrimStart('.', '/').Trim();
        return root.Equals("noclip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownBenignRuntimeObservation(ScanFindingDto? finding)
    {
        if (finding == null ||
            !string.Equals(finding.Code, "RUNTIME_UNASSOCIATED_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase))
            return false;

        return IsKnownBenignRuntimeAssembly(finding.Subject, finding.Evidence);
    }

    private static bool IsKnownBenignRuntimeAssembly(string? subject, string? evidence)
    {
        static string CleanAssemblyName(string? value)
        {
            string text = (value ?? "").Trim();
            int slash = Math.Max(text.LastIndexOf('/'), text.LastIndexOf('\\'));
            if (slash >= 0 && slash + 1 < text.Length) text = text[(slash + 1)..];
            if (text.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) text = text[..^4];
            return text;
        }

        if (CleanAssemblyName(subject).Equals("AnimatedGif", StringComparison.OrdinalIgnoreCase))
            return true;

        // Runtime evidence normally uses file=<path>;sha256=<hash>. Accept the benign
        // dependency by filename as well in case a scanner version leaves Subject empty.
        foreach (string token in (evidence ?? "").Split(new[] { ';', ',', '|', ' ', '=' }, StringSplitOptions.RemoveEmptyEntries))
            if (CleanAssemblyName(token).Equals("AnimatedGif", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static bool IsRuntimeAssemblyObservation(string? code) =>
        string.Equals(code, "RUNTIME_UNASSOCIATED_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "RUNTIME_DYNAMIC_ASSEMBLY_ADDED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "DUPLICATE_ASSEMBLY_IDENTITY", StringComparison.OrdinalIgnoreCase);

    private static bool IsFrameworkAssembly(string? subject, string? evidence)
    {
        static string Clean(string? value)
        {
            string text = (value ?? "").Trim();
            int slash = Math.Max(text.LastIndexOf('/'), text.LastIndexOf('\\'));
            if (slash >= 0 && slash + 1 < text.Length) text = text[(slash + 1)..];
            if (text.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) text = text[..^4];
            return text;
        }

        static bool LooksFramework(string name)
        {
            if (name.Length == 0) return false;
            return name.Equals("System", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("SQLitePCLRaw.", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("SQLitePCLRaw", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Accessibility", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("PresentationCore", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("PresentationFramework", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Anonymously Hosted DynamicMethods Assembly", StringComparison.OrdinalIgnoreCase);
        }

        string primary = Clean(subject);
        if (LooksFramework(primary)) return true;

        // Some scanner versions put the filename in Evidence instead of Subject.
        foreach (string token in (evidence ?? "").Split(new[] { ';', ',', '|', ' ', '=' }, StringSplitOptions.RemoveEmptyEntries))
            if (LooksFramework(Clean(token))) return true;
        return false;
    }

    private void EvaluateFingerprintBlacklist(IReadOnlyList<ClientModDto> mods, IReadOnlyList<ClientAssemblyDto> assemblies, List<ScanFindingDto> findings)
    {
        var activeForbiddenIds = config.ForbiddenModIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var fp in config.ForbiddenFingerprints.Where(x => activeForbiddenIds.Contains(x.ModId ?? "")))
        {
            string originalId = fp.ModId ?? "";
            var packageHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var contentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(fp.PackageSha256)) packageHashes.Add(fp.PackageSha256);
            if (!string.IsNullOrWhiteSpace(fp.ContentSha256)) contentHashes.Add(fp.ContentSha256);
            foreach (var manual in fp.ManualEntries ?? new())
            {
                if (!string.IsNullOrWhiteSpace(manual.PackageSha256)) packageHashes.Add(manual.PackageSha256);
                if (!string.IsNullOrWhiteSpace(manual.ContentSha256)) contentHashes.Add(manual.ContentSha256);
            }
            foreach (var release in fp.Releases ?? new())
            {
                if (!string.IsNullOrWhiteSpace(release.PackageSha256)) packageHashes.Add(release.PackageSha256);
                if (!string.IsNullOrWhiteSpace(release.ContentSha256)) contentHashes.Add(release.ContentSha256);
            }

            foreach (var mod in mods.Where(m => !string.Equals(m.ModId, originalId, StringComparison.OrdinalIgnoreCase)))
            {
                bool content = contentHashes.Any(x => HashEq(mod.ContentSha256, x));
                bool package = packageHashes.Any(x => HashEq(mod.Sha256, x));
                if (!content && !package) continue;
                string match = content ? "normalized-content-sha256" : "package-sha256";
                var manualMatch = (fp.ManualEntries ?? new()).FirstOrDefault(entry =>
                    (content && HashEq(mod.ContentSha256, entry.ContentSha256)) || (package && HashEq(mod.Sha256, entry.PackageSha256)));
                string source = manualMatch == null ? "" : $";source=manual;entry={manualMatch.Label}";
                AddUnique(findings, Finding("FingerprintPolicy", "FORBIDDEN_MOD_FINGERPRINT", AntiCheatSeverity.Critical, "A loaded mod package matches a blacklisted fingerprint under a different mod ID.", mod.ModId, $"blacklisted-id={originalId};match={match}{source}"));
            }

            var releaseAssemblyHashes = (fp.Releases ?? new()).SelectMany(r => r.Assemblies ?? new()).Select(a => a.Sha256).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var manualAssemblyHashes = (fp.ManualEntries ?? new()).SelectMany(x => x.AssemblySha256s ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var manualMvids = (fp.ManualEntries ?? new()).SelectMany(x => x.AssemblyMvids ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var manualStructureHashes = (fp.ManualEntries ?? new()).SelectMany(x => x.AssemblyStructureSha256s ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var manualBehaviorHashes = (fp.ManualEntries ?? new()).SelectMany(x => x.AssemblyBehaviorSha256s ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            foreach (var asm in assemblies)
            {
                bool hash = fp.AssemblySha256s.Any(x => HashEq(x, asm.Sha256)) || releaseAssemblyHashes.Any(x => HashEq(x, asm.Sha256)) || manualAssemblyHashes.Any(x => HashEq(x, asm.Sha256));
                bool mvid = fp.AssemblyMvids.Any(x => string.Equals(x, asm.ModuleVersionId, StringComparison.OrdinalIgnoreCase)) || manualMvids.Any(x => string.Equals(x, asm.ModuleVersionId, StringComparison.OrdinalIgnoreCase));
                bool structure = fp.AssemblyStructureSha256s.Any(x => HashEq(x, asm.StructureSha256)) || manualStructureHashes.Any(x => HashEq(x, asm.StructureSha256));
                bool systemType = fp.ModSystemTypeNames.Any(x => (asm.ModSystemTypeNames ?? new()).Contains(x, StringComparer.Ordinal));
                bool name = fp.AssemblyNames.Any(x => string.Equals(x, asm.AssemblyName, StringComparison.OrdinalIgnoreCase));
                bool stillOriginal = (asm.AssociatedModIds ?? new()).Any(x => string.Equals(x, originalId, StringComparison.OrdinalIgnoreCase));

                if (!stillOriginal && asm.FileSizeBytes > 0 && (fp.Source ?? "").Contains("ModDB", StringComparison.OrdinalIgnoreCase))
                {
                    var sizeReference = (fp.Releases ?? new())
                        .SelectMany(release => (release.Assemblies ?? new()).Select(reference => new { Release = release, Reference = reference }))
                        .FirstOrDefault(x => x.Reference != null && x.Reference.FileSizeBytes > 0 &&
                            string.Equals(x.Reference.AssemblyName, asm.AssemblyName, StringComparison.OrdinalIgnoreCase) &&
                            x.Reference.FileSizeBytes == asm.FileSizeBytes);
                    if (sizeReference != null)
                    {
                        AddUnique(findings, Finding(
                            "FingerprintPolicy",
                            "FORBIDDEN_ASSEMBLY_SIZE_MATCH",
                            AntiCheatSeverity.Low,
                            "A loaded assembly name and exact byte length match an official stored Mod DB release of a blacklisted mod. Size is supporting evidence only and is not sufficient by itself.",
                            asm.AssemblyName,
                            $"blacklisted-id={originalId};release={sizeReference.Release.Version};reference-assembly={sizeReference.Reference.AssemblyName};bytes={asm.FileSizeBytes};reported-mods={string.Join(",", asm.AssociatedModIds ?? new())}"));
                    }
                }

                if ((hash || mvid || structure || systemType || name) && !(stillOriginal && config.ForbiddenModIds.Contains(originalId, StringComparer.OrdinalIgnoreCase)))
                {
                    string strength = hash ? "assembly-sha256" : mvid ? "assembly-mvid" : structure ? "assembly-structure" : systemType ? "modsystem-type" : "assembly-name";
                    var manualMatch = (fp.ManualEntries ?? new()).FirstOrDefault(entry =>
                        (entry.AssemblySha256s ?? new()).Any(x => HashEq(x, asm.Sha256)) ||
                        (entry.AssemblyMvids ?? new()).Any(x => string.Equals(x, asm.ModuleVersionId, StringComparison.OrdinalIgnoreCase)) ||
                        (entry.AssemblyStructureSha256s ?? new()).Any(x => HashEq(x, asm.StructureSha256)));
                    string source = manualMatch == null ? "" : $";source=manual;entry={manualMatch.Label}";
                    AddUnique(findings, Finding("FingerprintPolicy", "FORBIDDEN_ASSEMBLY_FINGERPRINT", hash || mvid || structure || systemType ? AntiCheatSeverity.Critical : AntiCheatSeverity.High, "A loaded assembly matches a blacklisted mod fingerprint even though its reported mod ID differs.", asm.AssemblyName, $"blacklisted-id={originalId};match={strength};reported-mods={string.Join(",", asm.AssociatedModIds ?? new())}{source}"));
                }

                bool behaviorExact = fp.AssemblyBehaviorSha256s.Any(x => HashEq(x, asm.BehaviorSha256)) || manualBehaviorHashes.Any(x => HashEq(x, asm.BehaviorSha256)) ||
                    (fp.Releases ?? new()).SelectMany(r => r.Assemblies ?? new()).Any(x => HashEq(x.BehaviorSha256, asm.BehaviorSha256));
                if (behaviorExact && !stillOriginal)
                {
                    AddUnique(findings, Finding("BehaviorPolicy", "FORBIDDEN_ASSEMBLY_BEHAVIOR", AntiCheatSeverity.High,
                        "A loaded assembly has the same identifier-independent IL behavior shape as a blacklisted mod build.", asm.AssemblyName,
                        $"blacklisted-id={originalId};match=il-behavior-sha256;behavior-tokens={asm.BehaviorTokenCount};reported-mods={string.Join(",", asm.AssociatedModIds ?? new())}"));
                }

                if (!config.SimilarityDetectionEnabled || stillOriginal) continue;
                int best = 0, bestBehavior = 0;
                string bestVersion = "", bestAssembly = "", bestBehaviorVersion = "", bestBehaviorAssembly = "";
                int bestRefTokens = 0, bestBehaviorRefTokens = 0;
                int bestCorrelatedStructure = 0, bestCorrelatedBehavior = 0;
                string bestCorrelatedVersion = "", bestCorrelatedAssembly = "";
                bool bestCorrelatedBehaviorExact = false;
                int behaviorReview = Math.Max(82, Math.Min(95, config.SimilarityReviewThresholdPercent + 8));
                int behaviorHigh = Math.Max(94, Math.Min(99, config.SimilarityHighThresholdPercent + 4));
                foreach (var release in fp.Releases ?? new())
                {
                    foreach (var reference in release.Assemblies ?? new())
                    {
                        int structureScore = 0;
                        int behaviorScore = 0;
                        if (asm.StructureTokenCount >= 20 && reference.StructureTokenCount >= 20 &&
                            TokenCountCompatible(asm.StructureTokenCount, reference.StructureTokenCount, 0.55) &&
                            (asm.SimilarityMinHash?.Count ?? 0) >= StructuralSimilarity.SignatureSize &&
                            (reference.SimilarityMinHash?.Count ?? 0) >= StructuralSimilarity.SignatureSize)
                        {
                            structureScore = StructuralSimilarity.SimilarityPercent(asm.SimilarityMinHash, reference.SimilarityMinHash);
                            if (structureScore > best) { best = structureScore; bestVersion = release.Version ?? ""; bestAssembly = reference.AssemblyName ?? ""; bestRefTokens = reference.StructureTokenCount; }
                        }
                        if (asm.BehaviorTokenCount >= 12 && reference.BehaviorTokenCount >= 12 &&
                            TokenCountCompatible(asm.BehaviorTokenCount, reference.BehaviorTokenCount, 0.65) &&
                            (asm.BehaviorMinHash?.Count ?? 0) >= StructuralSimilarity.SignatureSize &&
                            (reference.BehaviorMinHash?.Count ?? 0) >= StructuralSimilarity.SignatureSize)
                        {
                            behaviorScore = StructuralSimilarity.SimilarityPercent(asm.BehaviorMinHash, reference.BehaviorMinHash);
                            if (behaviorScore > bestBehavior) { bestBehavior = behaviorScore; bestBehaviorVersion = release.Version ?? ""; bestBehaviorAssembly = reference.AssemblyName ?? ""; bestBehaviorRefTokens = reference.BehaviorTokenCount; }
                        }

                        bool referenceBehaviorExact = HashEq(reference.BehaviorSha256, asm.BehaviorSha256);
                        if (structureScore >= config.SimilarityReviewThresholdPercent && (referenceBehaviorExact || behaviorScore >= behaviorHigh))
                        {
                            int candidateStrength = structureScore + (referenceBehaviorExact ? 100 : behaviorScore);
                            int currentStrength = bestCorrelatedStructure + (bestCorrelatedBehaviorExact ? 100 : bestCorrelatedBehavior);
                            if (candidateStrength > currentStrength)
                            {
                                bestCorrelatedStructure = structureScore;
                                bestCorrelatedBehavior = behaviorScore;
                                bestCorrelatedVersion = release.Version ?? "";
                                bestCorrelatedAssembly = reference.AssemblyName ?? "";
                                bestCorrelatedBehaviorExact = referenceBehaviorExact;
                            }
                        }
                    }
                }

                if (best >= config.SimilarityReviewThresholdPercent)
                {
                    AntiCheatSeverity severity = best >= config.SimilarityHighThresholdPercent ? AntiCheatSeverity.High : AntiCheatSeverity.Moderate;
                    AddUnique(findings, Finding("SimilarityPolicy", "FORBIDDEN_ASSEMBLY_SIMILARITY", severity, "A loaded mod assembly is structurally similar to a recent release of a blacklisted mod. Similarity is supporting evidence, not an exact fingerprint match.", asm.AssemblyName, $"blacklisted-id={originalId};release={bestVersion};reference-assembly={bestAssembly};similarity={best}%;tokens={asm.StructureTokenCount}/{bestRefTokens}"));
                }

                if (bestBehavior >= behaviorReview)
                {
                    AntiCheatSeverity severity = bestBehavior >= behaviorHigh ? AntiCheatSeverity.High : AntiCheatSeverity.Moderate;
                    AddUnique(findings, Finding("BehaviorPolicy", "FORBIDDEN_ASSEMBLY_BEHAVIOR_SIMILARITY", severity,
                        "A loaded assembly has identifier-independent IL behavior similar to a recent blacklisted release. This survives simple renames/repacking but remains supporting evidence.",
                        asm.AssemblyName, $"blacklisted-id={originalId};release={bestBehaviorVersion};reference-assembly={bestBehaviorAssembly};behavior-similarity={bestBehavior}%;behavior-tokens={asm.BehaviorTokenCount}/{bestBehaviorRefTokens}"));
                }

                if (bestCorrelatedStructure >= config.SimilarityReviewThresholdPercent && (bestCorrelatedBehaviorExact || bestCorrelatedBehavior >= behaviorHigh))
                {
                    AddUnique(findings, Finding("CorrelationPolicy", "FORBIDDEN_REPACK_CORRELATED", AntiCheatSeverity.Critical,
                        "Independent structural and identifier-free IL behavior signals agree on the same stored blacklisted release/assembly reference, strongly indicating a renamed or repacked build.",
                        asm.AssemblyName, $"blacklisted-id={originalId};release={bestCorrelatedVersion};reference-assembly={bestCorrelatedAssembly};structure-similarity={bestCorrelatedStructure}%;behavior-similarity={bestCorrelatedBehavior}%;behavior-exact={(bestCorrelatedBehaviorExact ? "true" : "false")};reported-mods={string.Join(",", asm.AssociatedModIds ?? new())}"));
                }
                else if (behaviorExact && best >= config.SimilarityHighThresholdPercent)
                {
                    AddUnique(findings, Finding("CorrelationPolicy", "FORBIDDEN_REPACK_CORRELATED", AntiCheatSeverity.Critical,
                        "An exact identifier-free IL behavior fingerprint and a high structural similarity both point to the same blacklisted mod family.",
                        asm.AssemblyName, $"blacklisted-id={originalId};correlation-scope=forbidden-mod;structure-similarity={best}%;behavior-exact=true;reported-mods={string.Join(",", asm.AssociatedModIds ?? new())}"));
                }
            }
        }
    }

    private void EvaluateCanonicalModConsistency(
        IReadOnlyList<ClientModDto> mods,
        IReadOnlyList<ClientAssemblyDto> assemblies,
        IReadOnlyDictionary<string, AntiCheatCanonicalModFingerprint>? canonicalMods,
        List<ScanFindingDto> findings)
    {
        if (!config.CanonicalServerModIntegrityEnabled || canonicalMods == null || canonicalMods.Count == 0) return;
        foreach (var mod in mods.Where(x => x != null && !string.IsNullOrWhiteSpace(x.ModId)))
        {
            string id = (mod.ModId ?? "").Trim();
            if (id.Equals(AntiCheatProtocol.ClientModId, StringComparison.OrdinalIgnoreCase) ||
                id.Equals("game", StringComparison.OrdinalIgnoreCase) || id.Equals("survival", StringComparison.OrdinalIgnoreCase) || id.Equals("creative", StringComparison.OrdinalIgnoreCase)) continue;
            if (!canonicalMods.TryGetValue(id, out var expected) || expected == null) continue;

            bool versionMatch = string.Equals((mod.Version ?? "").Trim(), (expected.Version ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            if (!versionMatch)
            {
                AddUnique(findings, Finding(
                    "CanonicalPolicy",
                    "MOD_SERVER_VERSION_MISMATCH",
                    AntiCheatSeverity.High,
                    "A server-shared mod reports a different semantic version from the copy loaded by the server.",
                    id,
                    $"clientVersion={mod.Version};serverVersion={expected.Version};serverFile={expected.SourceFileName}"));
                continue;
            }

            bool contentComparable = IsSha256(mod.ContentSha256) && IsSha256(expected.ContentSha256);
            bool packageComparable = IsSha256(mod.Sha256) && IsSha256(expected.PackageSha256);
            bool contentMismatch = contentComparable && !HashEq(mod.ContentSha256, expected.ContentSha256);
            bool packageMismatch = packageComparable && !HashEq(mod.Sha256, expected.PackageSha256);

            bool dllCountComparable = mod.DllCount > 0 || expected.DllCount > 0;
            bool dllBytesComparable = mod.DllBytes > 0 || expected.DllBytes > 0;
            bool dllSetMismatch = (dllCountComparable && mod.DllCount != expected.DllCount) ||
                                  (dllBytesComparable && mod.DllBytes != expected.DllBytes);
            if (dllSetMismatch)
            {
                AddUnique(findings, Finding(
                    "CanonicalPolicy",
                    "MOD_DLL_SET_MISMATCH",
                    AntiCheatSeverity.Moderate,
                    "A server-shared mod has the expected ID/version but its DLL layout differs from the server-loaded copy. This is supporting evidence unless an exact content or DLL hash also disagrees.",
                    id,
                    $"version={mod.Version};clientDllCount={mod.DllCount};expectedDllCount={expected.DllCount};clientDllBytes={mod.DllBytes};expectedDllBytes={expected.DllBytes};clientPackageBytes={mod.PackageBytes};expectedPackageBytes={expected.PackageBytes};serverFile={expected.SourceFileName}"));
            }

            var related = assemblies.Where(a => (a.AssociatedModIds ?? new()).Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase))).ToList();
            bool authoritativeDllMismatch = false;
            foreach (var expectedDll in expected.Assemblies ?? new())
            {
                if (expectedDll == null || string.IsNullOrWhiteSpace(expectedDll.FileName)) continue;
                var clientDll = related.FirstOrDefault(a => string.Equals(a.FileName, expectedDll.FileName, StringComparison.OrdinalIgnoreCase));
                if (clientDll == null) continue;
                int ownerCount = (clientDll.AssociatedModIds ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (ownerCount == 1 && IsSha256(clientDll.Sha256) && IsSha256(expectedDll.Sha256) && !HashEq(clientDll.Sha256, expectedDll.Sha256))
                {
                    authoritativeDllMismatch = true;
                    AddUnique(findings, Finding(
                        "CanonicalPolicy",
                        "ASSEMBLY_SERVER_HASH_MISMATCH",
                        AntiCheatSeverity.High,
                        "A confidently associated client DLL SHA-256 differs from the exact DLL loaded by the server for the same shared mod/version.",
                        clientDll.AssemblyName,
                        $"modId={id};file={clientDll.FileName};clientSha256={clientDll.Sha256};serverSha256={expectedDll.Sha256};clientBytes={clientDll.FileSizeBytes};serverBytes={expectedDll.FileSizeBytes}"));
                    continue;
                }

                if (expectedDll.FileSizeBytes > 0 && clientDll.FileSizeBytes > 0 && clientDll.FileSizeBytes != expectedDll.FileSizeBytes)
                {
                    AddUnique(findings, Finding(
                        "CanonicalPolicy",
                        "ASSEMBLY_SIZE_MISMATCH",
                        AntiCheatSeverity.Moderate,
                        "A loaded DLL associated with a server-shared mod has a different byte length from the server baseline. Exact SHA-256 comparison takes precedence when available.",
                        clientDll.AssemblyName,
                        $"modId={id};file={clientDll.FileName};clientBytes={clientDll.FileSizeBytes};expectedBytes={expectedDll.FileSizeBytes};sha256={clientDll.Sha256}"));
                }
            }

            // Normalized content SHA-256 and exact server DLL SHA-256 are authoritative. A raw ZIP
            // mismatch by itself remains diagnostic because harmless repackaging can change archive bytes.
            if (!contentMismatch && !authoritativeDllMismatch && !(packageMismatch && !contentComparable) && !dllSetMismatch) continue;

            AddUnique(findings, Finding(
                "CanonicalPolicy",
                "MOD_CANONICAL_MISMATCH",
                (contentMismatch || authoritativeDllMismatch) ? AntiCheatSeverity.High : AntiCheatSeverity.Moderate,
                contentMismatch || authoritativeDllMismatch
                    ? "A server-shared mod reports the expected ID/version but does not match the exact server-loaded content/DLL baseline."
                    : "A server-shared mod has packaging or DLL-layout differences from the server-loaded copy, but no authoritative content mismatch was available.",
                id,
                $"version={mod.Version};clientContent={mod.ContentSha256};serverContent={expected.ContentSha256};clientPackage={mod.Sha256};serverPackage={expected.PackageSha256};clientPackageBytes={mod.PackageBytes};serverPackageBytes={expected.PackageBytes};clientDllCount={mod.DllCount};serverDllCount={expected.DllCount};clientDllBytes={mod.DllBytes};serverDllBytes={expected.DllBytes};authoritativeDllMismatch={authoritativeDllMismatch.ToString().ToLowerInvariant()};serverFile={expected.SourceFileName}"));
        }
    }

    private static void EvaluateAssemblyConsistency(IReadOnlyList<ClientModDto> mods, IReadOnlyList<ClientAssemblyDto> assemblies, List<ScanFindingDto> findings)
    {
        var modIds = mods.Where(x => !string.IsNullOrWhiteSpace(x.ModId)).Select(x => x.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in assemblies)
        {
            foreach (string associated in asm.AssociatedModIds ?? new())
                if (!string.IsNullOrWhiteSpace(associated) && !modIds.Contains(associated))
                    AddUnique(findings, Finding("AssemblyCorrelation", "UNREPORTED_MOD_ASSEMBLY", AntiCheatSeverity.High, "A loaded mod assembly references a mod ID that is absent from the reported mod inventory.", associated, $"assembly={asm.AssemblyName};sha256={asm.Sha256}"));
        }
    }


    private static void EvaluateCoreAssemblyIntegrity(
        IReadOnlyList<ClientAssemblyDto> assemblies,
        IReadOnlyDictionary<string, AntiCheatCoreAssemblyFingerprint>? expected,
        List<ScanFindingDto> findings)
    {
        if (expected == null || expected.Count == 0) return;
        var byName = assemblies
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.AssemblyName))
            .GroupBy(a => a.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var pair in expected)
        {
            var baseline = pair.Value;
            if (!byName.TryGetValue(pair.Key, out var client))
            {
                AddUnique(findings, Finding(
                    "StartupIntegrity",
                    "CORE_ASSEMBLY_MISSING",
                    AntiCheatSeverity.Moderate,
                    "A shared core assembly expected by the server is missing from the client's startup assembly census.",
                    pair.Key,
                    $"expectedVersion={baseline.Version};expectedSha256={baseline.Sha256}"));
                continue;
            }

            // Different managed versions are primarily a compatibility signal. Only compare the
            // exact file fingerprint when both sides claim the same managed assembly version.
            if (!string.IsNullOrWhiteSpace(baseline.Version) && !string.IsNullOrWhiteSpace(client.Version) &&
                !string.Equals(baseline.Version, client.Version, StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(findings, Finding(
                    "StartupIntegrity",
                    "CORE_ASSEMBLY_VERSION_MISMATCH",
                    AntiCheatSeverity.Moderate,
                    "A shared core assembly reports a different version than the server baseline.",
                    pair.Key,
                    $"serverVersion={baseline.Version};clientVersion={client.Version}"));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(baseline.Sha256) && !string.IsNullOrWhiteSpace(client.Sha256) &&
                !HashEq(baseline.Sha256, client.Sha256))
            {
                AddUnique(findings, Finding(
                    "StartupIntegrity",
                    "CORE_ASSEMBLY_HASH_MISMATCH",
                    AntiCheatSeverity.High,
                    "A shared Vintage Story core assembly differs from the server's startup integrity baseline. This is strong integrity evidence but should still be reviewed for platform/build differences before disruptive enforcement.",
                    pair.Key,
                    $"serverSha256={baseline.Sha256};clientSha256={client.Sha256};serverMvid={baseline.ModuleVersionId};clientMvid={client.ModuleVersionId}"));
            }
        }
    }

    private static void EvaluateHarmony(ClientHarmonyDto? harmony, List<ScanFindingDto> findings)
    {
        if (harmony == null) return;
        var patches = harmony.Patches ?? new List<ClientHarmonyPatchDto>();

        if (patches.Any(p => string.Equals(p.Owner, "com.br3zzly.modmenu", StringComparison.OrdinalIgnoreCase)))
        {
            AddUnique(findings, Finding(
                "HarmonyPolicy",
                "MODMENU_HARMONY_OWNER",
                AntiCheatSeverity.Critical,
                "A known ModMenu Harmony patch owner is active in the client process.",
                "com.br3zzly.modmenu",
                $"patches={patches.Count(p => string.Equals(p.Owner, "com.br3zzly.modmenu", StringComparison.OrdinalIgnoreCase))}"));
        }

        foreach (var group in patches.GroupBy(p => p.PatchAssembly ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key)) continue;

            var categories = group.Select(HarmonyCategory)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (categories.Count < 3) continue;

            var modIds = group.SelectMany(p => p.PatchAssemblyModIds ?? new())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Take(16)
                .ToList();

            AddUnique(findings, Finding(
                "HarmonyPolicy",
                "MODMENU_PATCH_CLUSTER",
                AntiCheatSeverity.High,
                "One loaded assembly patches a ModMenu-like cluster of movement, reach and mining code paths.",
                group.Key,
                $"categories={string.Join(",", categories)};mods={string.Join(",", modIds)}"));
        }
    }

    private static string HarmonyCategory(ClientHarmonyPatchDto patch)
    {
        string type = patch.TargetType ?? "";
        string method = patch.TargetMethod ?? "";

        if (type.EndsWith("EntityControls", StringComparison.Ordinal) &&
            method.Equals("CalcMovementVectors", StringComparison.Ordinal))
            return "movement";

        if (method.Equals("RayTraceForSelection", StringComparison.Ordinal) ||
            method.Equals("GetPickingRayByMouseCoordinates", StringComparison.Ordinal))
            return "reach";

        if (method.Equals("OnGettingBroken", StringComparison.Ordinal) ||
            method.Equals("OnPlayerTryDestroyBlock", StringComparison.Ordinal))
            return "mining";

        if (method.Contains("ReceiveDamage", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Attack", StringComparison.OrdinalIgnoreCase))
            return "combat";

        return "";
    }

    private void EvaluateClientIntegrity(IReadOnlyList<ClientAssemblyDto> assemblies, ClientIntegrityDto? integrity, List<ScanFindingDto> findings)
    {
        if (integrity == null)
        {
            AddUnique(findings, Finding("ServerIntegrity", "CLIENT_INTEGRITY_MISSING", AntiCheatSeverity.Critical, "The client did not provide its required build-integrity fingerprint.", AntiCheatProtocol.ClientModId, "integrity-payload-missing"));
            return;
        }
        if (!string.Equals(integrity.AssemblyName, "NewfiesAntiCheatClient", StringComparison.OrdinalIgnoreCase))
            AddUnique(findings, Finding("ServerIntegrity", "CLIENT_ASSEMBLY_IDENTITY_MISMATCH", AntiCheatSeverity.High, "The Client Integrity assembly identity is unexpected.", integrity.AssemblyName, "expected=NewfiesAntiCheatClient"));

        var selfAsm = assemblies.FirstOrDefault(a => string.Equals(a.AssemblyName, integrity.AssemblyName, StringComparison.OrdinalIgnoreCase));
        if (selfAsm == null)
            AddUnique(findings, Finding("ServerIntegrity", "CLIENT_INTEGRITY_CROSSCHECK_FAILED", AntiCheatSeverity.Critical, "The client build fingerprint has no matching loaded-assembly entry.", integrity.AssemblyName, "assembly-list-missing"));
        else if ((!string.IsNullOrWhiteSpace(integrity.AssemblySha256) && !HashEq(selfAsm.Sha256, integrity.AssemblySha256)) ||
                 (!string.IsNullOrWhiteSpace(integrity.ModuleVersionId) && !string.Equals(selfAsm.ModuleVersionId, integrity.ModuleVersionId, StringComparison.OrdinalIgnoreCase)) ||
                 (integrity.AssemblyFileSizeBytes > 0 && selfAsm.FileSizeBytes != integrity.AssemblyFileSizeBytes))
            AddUnique(findings, Finding("ServerIntegrity", "CLIENT_INTEGRITY_CROSSCHECK_FAILED", AntiCheatSeverity.Critical, "Independent client integrity and assembly scanner fingerprints disagree.", integrity.AssemblyName, "assembly-hash-mvid-or-size-mismatch"));

        if (config.RequireApprovedClientBuild && config.ApprovedClientBuildHashes.Count > 0)
        {
            bool approved = config.ApprovedClientBuildHashes.Any(h => HashEq(h, integrity.AssemblySha256) || HashEq(h, integrity.PackageSha256));
            if (!approved) AddUnique(findings, Finding("ServerIntegrity", "CLIENT_BUILD_UNAPPROVED", AntiCheatSeverity.Critical, "The connected Client Integrity build does not match an approved server fingerprint.", integrity.AssemblySha256, $"package={integrity.PackageSha256}"));
        }
        else if (config.RequireApprovedClientBuild && config.ApprovedClientBuildHashes.Count == 0)
            AddUnique(findings, Finding("ServerIntegrity", "CLIENT_BUILD_POLICY_EMPTY", AntiCheatSeverity.Moderate, "Approved-build enforcement is enabled but the server has no approved client hashes configured.", AntiCheatProtocol.ClientModId, "configure-approved-hash"));
    }

    private static bool TokenCountCompatible(int a, int b, double minRatio)
    {
        if (a <= 0 || b <= 0) return false;
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        return hi > 0 && (double)lo / hi >= minRatio;
    }

    private static bool IsSha256(string? value)
    {
        string text = (value ?? "").Trim();
        return text.Length == 64 && text.All(Uri.IsHexDigit);
    }

    private static bool HashEq(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    private static void AddUnique(List<ScanFindingDto> list, ScanFindingDto finding)
    {
        if (!list.Any(x => string.Equals(x.Code, finding.Code, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Subject, finding.Subject, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Evidence, finding.Evidence, StringComparison.OrdinalIgnoreCase))) list.Add(finding);
    }
    private static ScanFindingDto Finding(string scanner, string code, AntiCheatSeverity severity, string summary, string subject, string evidence) => new() { Scanner=scanner, Code=code, Severity=(int)severity, Summary=summary, Subject=subject ?? "", Evidence=evidence };
}
