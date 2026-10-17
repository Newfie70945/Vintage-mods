using ProtoBuf;

namespace BlockLogger.PlayerList;

[ProtoContract]
public sealed class PublicPlayerListEntry
{
	[ProtoMember(1)]
	public string PlayerName { get; set; } = "";

	[ProtoMember(2)]
	public string RoleName { get; set; } = "";

	[ProtoMember(5)]
	public int PingMs { get; set; } = -1;

	[ProtoMember(6)]
	public long SessionSeconds { get; set; }

	[ProtoMember(7)]
	public bool Afk { get; set; }

	[ProtoMember(8)]
	public string NameColorHex { get; set; } = "";

	public int SortPrivilegeLevel { get; set; }
}
