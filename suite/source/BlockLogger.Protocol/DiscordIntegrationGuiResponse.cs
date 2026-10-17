using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class DiscordIntegrationGuiResponse
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public bool Success { get; set; }

	[ProtoMember(3)]
	public string Message { get; set; } = "";

	[ProtoMember(4)]
	public DiscordIntegrationGuiSettingsPacket Settings { get; set; } = new DiscordIntegrationGuiSettingsPacket();
}
