using System.Collections.Generic;
using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class AntiCheatGuiResponse
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public bool Success { get; set; }

	[ProtoMember(3)]
	public string Operation { get; set; } = "overview";

	[ProtoMember(4)]
	public string Message { get; set; } = "";

	[ProtoMember(5)]
	public List<string> Lines { get; set; } = new List<string>();

	[ProtoMember(6)]
	public string SelectedPlayer { get; set; } = "";

	[ProtoMember(7)]
	public bool Enabled { get; set; }

	[ProtoMember(8)]
	public AntiCheatGuiSettingsPacket? Settings { get; set; }

	[ProtoMember(9)]
	public List<AntiCheatConsequenceRuleGuiPacket> ConsequenceRules { get; set; } = new List<AntiCheatConsequenceRuleGuiPacket>();

	[ProtoMember(10)]
	public AntiCheatPacketProtectionGuiPacket? PacketSettings { get; set; }
}
