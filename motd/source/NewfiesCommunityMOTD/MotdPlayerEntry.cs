using System;
using ProtoBuf;

namespace NewfiesCommunityMOTD;

[Serializable]
[ProtoContract(SkipConstructor = true)]
public class MotdPlayerEntry
{
	[ProtoMember(1)]
	public string Name { get; set; } = "";

	[ProtoMember(2)]
	public string Role { get; set; } = "";

	[ProtoMember(3)]
	public int Ping { get; set; }

	[ProtoMember(4)]
	public bool IsStaff { get; set; }
}
