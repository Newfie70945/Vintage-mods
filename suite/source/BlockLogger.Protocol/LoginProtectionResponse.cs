using System.Collections.Generic;
using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class LoginProtectionResponse
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public bool Success { get; set; }

	[ProtoMember(3)]
	public string Message { get; set; } = "";

	[ProtoMember(4)]
	public List<string> Lines { get; set; } = new List<string>();

	[ProtoMember(5)]
	public bool Enabled { get; set; }

	[ProtoMember(6)]
	public int ProtectionSeconds { get; set; }

	[ProtoMember(7)]
	public bool ProtectOnJoin { get; set; }

	[ProtoMember(8)]
	public bool ProtectOnRespawn { get; set; }

	[ProtoMember(9)]
	public bool AnnounceOnJoin { get; set; }

	[ProtoMember(10)]
	public bool AnnounceOnEnd { get; set; }

	[ProtoMember(11)]
	public bool BlockPvP { get; set; }

	[ProtoMember(12)]
	public bool CancelOnAttack { get; set; }

	[ProtoMember(13)]
	public bool ProtectFallDamage { get; set; }

	[ProtoMember(14)]
	public bool ExtinguishFire { get; set; }

	[ProtoMember(15)]
	public int ActiveProtectedCount { get; set; }

	[ProtoMember(16)]
	public bool RecoverSwimmingOnLogin { get; set; }
}
