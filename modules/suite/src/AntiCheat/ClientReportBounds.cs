using NewfiesAntiCheat.Shared.Packets;

namespace BlockLogger.AntiCheat;

// Reject malformed shapes before hashing, sorting or retaining client-supplied
// evidence. These limits match/exceed the companion's own bounded collectors.
internal static class ClientReportBounds
{
    public static bool IsValid(ClientScanReportPacket? report)
    {
        if (report == null) return false;
        int remaining = 2 * 1024 * 1024;
        bool Text(string? value, int limit = 512)
        {
            if (value == null) return true;
            remaining -= value.Length;
            return value.Length <= limit && remaining >= 0;
        }
        bool List<T>(List<T>? values, int limit, System.Func<T, bool> valid) where T : class =>
            values == null || (values.Count <= limit && values.All(value => value != null && valid(value)));
        bool Strings(List<string>? values, int limit) => List(values, limit, value => Text(value));

        if (!Text(report.ChallengeId, 128) || !Text(report.ClientVersion, 128) ||
            !Text(report.ReportHash, 128) || !Text(report.SelfAttestationDigest, 128) ||
            (report.Nonce?.Length ?? 0) > 128) return false;
        if (!List(report.Mods, 512, m => Text(m.ModId) && Text(m.Version) && Text(m.ModType) &&
            Text(m.FileName, 4096) && Text(m.Sha256, 128) && Text(m.ContentSha256, 128) && Text(m.SourceType))) return false;
        if (!List(report.Assemblies, 512, a => Text(a.AssemblyName) && Text(a.Version) &&
            Text(a.FileName, 4096) && Text(a.ModuleVersionId, 128) && Text(a.Sha256, 128) &&
            Text(a.StructureSha256, 128) && Text(a.BehaviorSha256, 128) &&
            Strings(a.AssociatedModIds, 32) && Strings(a.ModSystemTypeNames, 128) &&
            (a.SimilarityMinHash?.Count ?? 0) <= 128 && (a.BehaviorMinHash?.Count ?? 0) <= 128)) return false;
        if (!List(report.Findings, 256, f => Text(f.Scanner) && Text(f.Code) && Text(f.Subject, 4096) &&
            Text(f.Summary, 4096) && Text(f.Evidence, 16384))) return false;
        if (!List(report.ScannerMetrics, 16, m => Text(m.Scanner))) return false;
        if (!List(report.SelfAttestationResults, 32, a => Text(a.ProbeId, 128) &&
            Text(a.Digest, 128) && Text(a.ErrorCode))) return false;
        var runtime = report.Runtime;
        if (runtime != null && (!Text(runtime.AssemblySetDigest, 128) ||
            !List(runtime.NewAssemblies, 64, a => Text(a.AssemblyName) && Text(a.Version) &&
                Text(a.FileName, 4096) && Text(a.ModuleVersionId, 128) && Text(a.Sha256, 128) &&
                Text(a.LoadContextName)))) return false;
        var harmony = report.Harmony;
        if (harmony != null && (!Text(harmony.PatchSetDigest, 128) ||
            !List(harmony.Patches, 512, p => Text(p.Owner) && Text(p.TargetAssembly) &&
                Text(p.TargetType, 2048) && Text(p.TargetMethod, 2048) && Text(p.PatchAssembly) &&
                Text(p.PatchType, 2048) && Text(p.PatchMethod, 2048) && Text(p.Kind) &&
                Strings(p.PatchAssemblyModIds, 32)))) return false;
        var integrity = report.Integrity;
        return integrity == null || (Text(integrity.AssemblyName) && Text(integrity.AssemblyVersion) &&
            Text(integrity.AssemblySha256, 128) && Text(integrity.ModuleVersionId, 128) &&
            Text(integrity.PackageFileName, 4096) && Text(integrity.PackageSha256, 128) &&
            Text(integrity.ContentSha256, 128) && Text(integrity.BuildManifestSha256, 128) &&
            Text(integrity.BuildManifestSignatureSha256, 128) && Text(integrity.BuildId, 1024));
    }
}
