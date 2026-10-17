using System.Collections.Generic;
using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Models;

[ProtoContract]
public sealed class ClientRuntimeDto
{
    [ProtoMember(1)] public long BaselineUtc { get; set; }
    [ProtoMember(2)] public int BaselineAssemblyCount { get; set; }
    [ProtoMember(3)] public int LoadedAssemblyCount { get; set; }
    [ProtoMember(4)] public int DynamicAssemblyCount { get; set; }
    [ProtoMember(5)] public int RuntimeGeneration { get; set; }
    [ProtoMember(6)] public string AssemblySetDigest { get; set; } = "";
    [ProtoMember(7)] public List<ClientRuntimeAssemblyDto> NewAssemblies { get; set; } = new();
    [ProtoMember(8)] public int DuplicateAssemblyIdentityCount { get; set; }
    [ProtoMember(9)] public int UnassociatedNewAssemblyCount { get; set; }
}
