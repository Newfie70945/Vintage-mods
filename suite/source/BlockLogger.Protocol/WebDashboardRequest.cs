using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class WebDashboardRequest
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public string Operation { get; set; } = "status";

	[ProtoMember(3)]
	public string SettingKey { get; set; } = "";

	[ProtoMember(4)]
	public string StringValue { get; set; } = "";

	[ProtoMember(5)]
	public int IntValue { get; set; }

	[ProtoMember(6)]
	public bool BoolValue { get; set; }

	[ProtoMember(7)]
	public WebMapGuiSettingsPacket? WebMapSettings { get; set; }
}
