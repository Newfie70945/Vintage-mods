using System.Collections.Generic;
using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Models;

[ProtoContract]
public sealed class ClientHarmonyDto
{
    [ProtoMember(1)] public bool HarmonyDetected { get; set; }
    [ProtoMember(2)] public int PatchCount { get; set; }
    [ProtoMember(3)] public string PatchSetDigest { get; set; } = "";
    [ProtoMember(4)] public List<ClientHarmonyPatchDto> Patches { get; set; } = new();
    [ProtoMember(5)] public int SuspiciousOwnerCount { get; set; }
}
