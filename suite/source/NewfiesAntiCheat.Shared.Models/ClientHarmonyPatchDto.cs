using System.Collections.Generic;
using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Models;

[ProtoContract]
public sealed class ClientHarmonyPatchDto
{
	[ProtoMember(1)]
	public string Owner { get; set; } = "";

	[ProtoMember(2)]
	public string TargetAssembly { get; set; } = "";

	[ProtoMember(3)]
	public string TargetType { get; set; } = "";

	[ProtoMember(4)]
	public string TargetMethod { get; set; } = "";

	[ProtoMember(5)]
	public string PatchAssembly { get; set; } = "";

	[ProtoMember(6)]
	public string PatchType { get; set; } = "";

	[ProtoMember(7)]
	public string PatchMethod { get; set; } = "";

	[ProtoMember(8)]
	public string Kind { get; set; } = "";

	[ProtoMember(9)]
	public List<string> PatchAssemblyModIds { get; set; } = new List<string>();
}
