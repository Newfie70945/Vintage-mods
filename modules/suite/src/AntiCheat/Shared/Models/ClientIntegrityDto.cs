using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Models;

[ProtoContract]
public sealed class ClientIntegrityDto
{
    [ProtoMember(1)] public string AssemblyName { get; set; } = "";
    [ProtoMember(2)] public string AssemblyVersion { get; set; } = "";
    [ProtoMember(3)] public string AssemblySha256 { get; set; } = "";
    [ProtoMember(4)] public string ModuleVersionId { get; set; } = "";
    [ProtoMember(5)] public string PackageFileName { get; set; } = "";
    [ProtoMember(6)] public string PackageSha256 { get; set; } = "";
    // Protocol 13 self-integrity evidence. These measurements are independently cross-checked
    // against the server-installed companion, the client ModLoader inventory and the loaded
    // assembly list. No single client-reported field is trusted on its own.
    [ProtoMember(7)] public string ContentSha256 { get; set; } = "";
    [ProtoMember(8)] public long AssemblyFileSizeBytes { get; set; }
    [ProtoMember(9)] public long PackageBytes { get; set; }
    [ProtoMember(10)] public int DllCount { get; set; }
    [ProtoMember(11)] public long DllBytes { get; set; }
    // Protocol 13 signed-build metadata. The server independently verifies the signature on its
    // installed companion; these client fields bind the reported local package to that manifest.
    [ProtoMember(12)] public string BuildManifestSha256 { get; set; } = "";
    [ProtoMember(13)] public string BuildManifestSignatureSha256 { get; set; } = "";
    [ProtoMember(14)] public string BuildId { get; set; } = "";
}
