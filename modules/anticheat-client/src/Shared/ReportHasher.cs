using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NewfiesAntiCheat.Shared.Models;

namespace NewfiesAntiCheat.Shared;

public static class ReportHasher
{
    public static string Compute(
        string challengeId, byte[]? nonce, IEnumerable<ClientModDto> mods,
        IEnumerable<ScanFindingDto> findings, IEnumerable<ClientAssemblyDto>? assemblies = null,
        ClientIntegrityDto? integrity = null, ClientRuntimeDto? runtime = null,
        IEnumerable<ClientScannerMetricDto>? scannerMetrics = null, ClientHarmonyDto? harmony = null,
        IEnumerable<SelfAttestationResultDto>? selfAttestation = null)
    {
        var builder = new StringBuilder();
        builder.Append(challengeId).Append('|').Append(Convert.ToHexString(nonce ?? Array.Empty<byte>()));
        foreach (var mod in (mods ?? Array.Empty<ClientModDto>()).OrderBy(m => m.ModId, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.FileName, StringComparer.OrdinalIgnoreCase))
            builder.Append('|').Append(mod.ModId).Append('@').Append(mod.Version).Append('#').Append(mod.Sha256).Append('#').Append(mod.ContentSha256).Append('#').Append(mod.FileName)
                .Append("#bytes:").Append(mod.PackageBytes).Append("#dlls:").Append(mod.DllCount).Append("#dllbytes:").Append(mod.DllBytes);

        foreach (var asm in (assemblies ?? Array.Empty<ClientAssemblyDto>()).OrderBy(a => a.AssemblyName, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.ModuleVersionId, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("|asm:").Append(asm.AssemblyName).Append('@').Append(asm.Version)
                .Append('#').Append(asm.Sha256).Append('#').Append(asm.ModuleVersionId).Append('#').Append(asm.StructureSha256)
                .Append("#behavior:").Append(asm.BehaviorSha256).Append("#bytes:").Append(asm.FileSizeBytes);
            foreach (var modId in (asm.AssociatedModIds ?? new()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) builder.Append('#').Append(modId);
            foreach (var typeName in (asm.ModSystemTypeNames ?? new()).OrderBy(x => x, StringComparer.Ordinal)) builder.Append('#').Append(typeName);
            builder.Append("#tokens:").Append(asm.StructureTokenCount);
            foreach (ulong value in (asm.SimilarityMinHash ?? new()).Take(StructuralSimilarity.SignatureSize)) builder.Append('#').Append(value);
            builder.Append("#behaviorTokens:").Append(asm.BehaviorTokenCount);
            foreach (ulong value in (asm.BehaviorMinHash ?? new()).Take(StructuralSimilarity.SignatureSize)) builder.Append('#').Append(value);
        }

        if (integrity != null)
        {
            builder.Append("|integrity:").Append(integrity.AssemblyName).Append('@').Append(integrity.AssemblyVersion)
                .Append('#').Append(integrity.AssemblySha256).Append('#').Append(integrity.ModuleVersionId)
                .Append('#').Append(integrity.PackageSha256).Append("#content:").Append(integrity.ContentSha256)
                .Append("#asmbytes:").Append(integrity.AssemblyFileSizeBytes)
                .Append("#pkgbytes:").Append(integrity.PackageBytes)
                .Append("#dlls:").Append(integrity.DllCount)
                .Append("#dllbytes:").Append(integrity.DllBytes)
                .Append("#manifest:").Append(integrity.BuildManifestSha256)
                .Append("#manifestsig:").Append(integrity.BuildManifestSignatureSha256)
                .Append("#build:").Append(integrity.BuildId);
        }

        if (runtime != null)
        {
            builder.Append("|runtime:").Append(runtime.BaselineUtc).Append('#').Append(runtime.BaselineAssemblyCount)
                .Append('#').Append(runtime.LoadedAssemblyCount).Append('#').Append(runtime.DynamicAssemblyCount)
                .Append('#').Append(runtime.RuntimeGeneration).Append('#').Append(runtime.AssemblySetDigest)
                .Append('#').Append(runtime.DuplicateAssemblyIdentityCount).Append('#').Append(runtime.UnassociatedNewAssemblyCount);
            foreach (var asm in (runtime.NewAssemblies ?? new()).OrderBy(x => x.AssemblyName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.ModuleVersionId, StringComparer.OrdinalIgnoreCase))
                builder.Append("|runtimeasm:").Append(asm.AssemblyName).Append('@').Append(asm.Version).Append('#').Append(asm.FileName)
                    .Append('#').Append(asm.ModuleVersionId).Append('#').Append(asm.Sha256).Append('#').Append(asm.IsDynamic ? '1' : '0')
                    .Append('#').Append(asm.LoadContextName).Append('#').Append(asm.AssociatedWithLoadedMod ? '1' : '0');
        }

        if (harmony != null)
        {
            builder.Append("|harmony:").Append(harmony.HarmonyDetected ? '1' : '0')
                .Append('#').Append(harmony.PatchCount).Append('#').Append(harmony.PatchSetDigest)
                .Append('#').Append(harmony.SuspiciousOwnerCount);
            foreach (var patch in (harmony.Patches ?? new()).OrderBy(x => x.TargetAssembly, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.TargetType, StringComparer.Ordinal).ThenBy(x => x.TargetMethod, StringComparer.Ordinal)
                         .ThenBy(x => x.Owner, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
                         .Take(512))
            {
                builder.Append("|patch:").Append(patch.Owner).Append('#').Append(patch.TargetAssembly)
                    .Append('#').Append(patch.TargetType).Append('#').Append(patch.TargetMethod)
                    .Append('#').Append(patch.PatchAssembly).Append('#').Append(patch.PatchType)
                    .Append('#').Append(patch.PatchMethod).Append('#').Append(patch.Kind);
                foreach (var modId in (patch.PatchAssemblyModIds ?? new()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    builder.Append('#').Append(modId);
            }
        }

        foreach (var metric in (scannerMetrics ?? Array.Empty<ClientScannerMetricDto>()).OrderBy(x => x.Scanner, StringComparer.OrdinalIgnoreCase))
            builder.Append("|metric:").Append(metric.Scanner).Append('#').Append(metric.DurationMs).Append('#').Append(metric.Success ? '1' : '0')
                .Append('#').Append(metric.ModCount).Append('#').Append(metric.AssemblyCount).Append('#').Append(metric.FindingCount)
                .Append('#').Append(metric.CacheHits).Append('#').Append(metric.CacheMisses);

        foreach (var att in (selfAttestation ?? Array.Empty<SelfAttestationResultDto>()).OrderBy(x => x.ProbeId, StringComparer.Ordinal).ThenBy(x => x.Kind).ThenBy(x => x.MetadataToken))
            builder.Append("|selfprobe:").Append(att.ProbeId).Append('#').Append(att.Kind).Append('#').Append(att.MetadataToken)
                .Append('#').Append(att.Success ? '1' : '0').Append('#').Append(att.Digest).Append('#').Append(att.ErrorCode);

        foreach (var finding in (findings ?? Array.Empty<ScanFindingDto>()).OrderBy(f => f.Scanner).ThenBy(f => f.Code).ThenBy(f => f.Subject))
            builder.Append('|').Append(finding.Scanner).Append(':').Append(finding.Code).Append(':').Append(finding.Subject).Append(':').Append(finding.Severity).Append(':').Append(finding.Evidence);

        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
    public static string ComputeV2(
        int protocolVersion, string clientVersion, long scanStartedUtc, long scanCompletedUtc, int completedScanners,
        string challengeId, byte[]? nonce, IEnumerable<ClientModDto> mods,
        IEnumerable<ScanFindingDto> findings, IEnumerable<ClientAssemblyDto>? assemblies = null,
        ClientIntegrityDto? integrity = null, ClientRuntimeDto? runtime = null,
        IEnumerable<ClientScannerMetricDto>? scannerMetrics = null, ClientHarmonyDto? harmony = null,
        IEnumerable<SelfAttestationResultDto>? selfAttestation = null)
    {
        // Keep the proven v1 payload canonicalization and bind the previously-uncovered report
        // control fields around it. This preserves backwards verification while preventing an
        // in-flight metadata downgrade from escaping the report digest on hardened clients.
        string legacyPayloadHash = Compute(challengeId, nonce, mods, findings, assemblies, integrity, runtime, scannerMetrics, harmony, selfAttestation);
        string raw = string.Join("|", new[]
        {
            "report-v2",
            protocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            clientVersion ?? "",
            scanStartedUtc.ToString(System.Globalization.CultureInfo.InvariantCulture),
            scanCompletedUtc.ToString(System.Globalization.CultureInfo.InvariantCulture),
            completedScanners.ToString(System.Globalization.CultureInfo.InvariantCulture),
            challengeId ?? "",
            Convert.ToHexString(nonce ?? Array.Empty<byte>()),
            legacyPayloadHash
        });
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

}
