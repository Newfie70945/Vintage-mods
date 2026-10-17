using System.Collections.Generic;
using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class AntiCheatGuiRequest
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public string Operation { get; set; } = "overview";

	[ProtoMember(3)]
	public string Search { get; set; } = "";

	[ProtoMember(4)]
	public bool BoolValue { get; set; }

	[ProtoMember(5)]
	public string Secondary { get; set; } = "";

	[ProtoMember(6)]
	public AntiCheatGuiSettingsPacket? Settings { get; set; }

	[ProtoMember(7)]
	public List<AntiCheatConsequenceRuleGuiPacket> ConsequenceRules { get; set; } = new List<AntiCheatConsequenceRuleGuiPacket>();

	[ProtoMember(8)]
	public bool ApplyCurrent { get; set; }

	[ProtoMember(9)]
	public AntiCheatPacketProtectionGuiPacket? PacketSettings { get; set; }
}
