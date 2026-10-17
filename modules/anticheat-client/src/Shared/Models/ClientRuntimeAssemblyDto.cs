using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Models;

[ProtoContract]
public sealed class ClientRuntimeAssemblyDto
{
    [ProtoMember(1)] public string AssemblyName { get; set; } = "";
    [ProtoMember(2)] public string Version { get; set; } = "";
    [ProtoMember(3)] public string FileName { get; set; } = "";
    [ProtoMember(4)] public string ModuleVersionId { get; set; } = "";
    [ProtoMember(5)] public string Sha256 { get; set; } = "";
    [ProtoMember(6)] public bool IsDynamic { get; set; }
    [ProtoMember(7)] public string LoadContextName { get; set; } = "";
    [ProtoMember(8)] public bool AssociatedWithLoadedMod { get; set; }
}
