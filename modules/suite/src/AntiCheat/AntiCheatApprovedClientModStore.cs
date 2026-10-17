using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

/// <summary>
/// Server-owner approvals for intentional/private client mod variants. Approval is exact and
/// fingerprint-based: ModID + version + normalized content SHA-256. A ModID/version by itself is
/// never sufficient to suppress an official ModDB mismatch.
/// </summary>
public sealed class AntiCheatApprovedClientModStore
{
    public int SchemaVersion { get; set; } = 1;
    public List<AntiCheatApprovedClientModFingerprint> Entries { get; set; } = new();
}

public sealed class AntiCheatApprovedClientModFingerprint
{
    public string Id { get; set; } = "";
    public string ModId { get; set; } = "";
    public string Version { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string Label { get; set; } = "";
    public string Notes { get; set; } = "";
    public string ApprovedBy { get; set; } = "";
    public long CreatedUtc { get; set; }
    public long UpdatedUtc { get; set; }
}
