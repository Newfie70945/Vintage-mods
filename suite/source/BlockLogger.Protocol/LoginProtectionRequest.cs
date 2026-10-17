using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class LoginProtectionRequest
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public string Operation { get; set; } = "status";

	[ProtoMember(3)]
	public string SettingKey { get; set; } = "";

	[ProtoMember(4)]
	public int IntValue { get; set; }

	[ProtoMember(5)]
	public bool BoolValue { get; set; }
}
