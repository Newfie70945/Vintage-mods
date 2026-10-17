using ProtoBuf;

namespace BlockLogger.Services;

[ProtoContract]
public sealed class SupporterLampStatePacket
{
	[ProtoMember(1)]
	public string PlayerUid { get; set; } = string.Empty;

	[ProtoMember(2)]
	public bool Enabled { get; set; }

	[ProtoMember(3)]
	public byte Hue { get; set; } = 33;

	[ProtoMember(4)]
	public byte Saturation { get; set; } = 7;

	[ProtoMember(5)]
	public byte Brightness { get; set; } = 8;
}
