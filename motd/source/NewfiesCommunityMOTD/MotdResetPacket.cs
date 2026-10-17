using System;
using ProtoBuf;

namespace NewfiesCommunityMOTD;

[Serializable]
[ProtoContract(SkipConstructor = true)]
public class MotdResetPacket
{
	[ProtoMember(1)]
	public bool ResetAll { get; set; }
}
