using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class DiscordAnnouncementGuiPacket
{
	[ProtoMember(1)]
	public bool Enabled { get; set; }

	[ProtoMember(2)]
	public string ChannelId { get; set; } = "0";

	[ProtoMember(3)]
	public string Format { get; set; } = "";
}
