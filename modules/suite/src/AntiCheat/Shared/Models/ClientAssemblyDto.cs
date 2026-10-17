using System.Collections.Generic;
using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Models;

[ProtoContract]
public sealed class ClientAssemblyDto
{
    [ProtoMember(1)] public string AssemblyName { get; set; } = "";
    [ProtoMember(2)] public string Version { get; set; } = "";
    [ProtoMember(3)] public string FileName { get; set; } = "";
    [ProtoMember(4)] public string Sha256 { get; set; } = "";
    [ProtoMember(5)] public string ModuleVersionId { get; set; } = "";
    [ProtoMember(6)] public List<string> AssociatedModIds { get; set; } = new();
    [ProtoMember(7)] public string StructureSha256 { get; set; } = "";
    [ProtoMember(8)] public List<string> ModSystemTypeNames { get; set; } = new();
    [ProtoMember(9)] public List<ulong> SimilarityMinHash { get; set; } = new();
    [ProtoMember(10)] public int StructureTokenCount { get; set; }
    [ProtoMember(11)] public string BehaviorSha256 { get; set; } = "";
    [ProtoMember(12)] public List<ulong> BehaviorMinHash { get; set; } = new();
    [ProtoMember(13)] public int BehaviorTokenCount { get; set; }
    [ProtoMember(14)] public long FileSizeBytes { get; set; }
}
