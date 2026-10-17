using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class ProfilerProfileSlowTickPacket
{
	[ProtoMember(1)]
	public long UnixMilliseconds { get; set; }

	[ProtoMember(2)]
	public double TickMs { get; set; }

	[ProtoMember(3)]
	public string TopCauseSummary { get; set; } = "";
}
