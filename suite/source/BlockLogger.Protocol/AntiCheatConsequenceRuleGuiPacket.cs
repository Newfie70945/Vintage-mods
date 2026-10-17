using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class AntiCheatConsequenceRuleGuiPacket
{
	[ProtoMember(1)]
	public string Id { get; set; } = "";

	[ProtoMember(2)]
	public string Name { get; set; } = "";

	[ProtoMember(3)]
	public bool Enabled { get; set; }

	[ProtoMember(4)]
	public string TriggerType { get; set; } = "FindingCode";

	[ProtoMember(5)]
	public string TriggerValue { get; set; } = "";

	[ProtoMember(6)]
	public string Subject { get; set; } = "";

	[ProtoMember(7)]
	public string Action { get; set; } = "Log";

	[ProtoMember(8)]
	public string Duration { get; set; } = "30m";

	[ProtoMember(9)]
	public string Message { get; set; } = "";
}
