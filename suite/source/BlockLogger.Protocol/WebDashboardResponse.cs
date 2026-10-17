using System.Collections.Generic;
using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class WebDashboardResponse
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public bool Success { get; set; }

	[ProtoMember(3)]
	public string Message { get; set; } = "";

	[ProtoMember(4)]
	public List<string> Lines { get; set; } = new List<string>();

	[ProtoMember(5)]
	public bool Enabled { get; set; }

	[ProtoMember(6)]
	public bool Running { get; set; }

	[ProtoMember(7)]
	public string BindAddress { get; set; } = "";

	[ProtoMember(8)]
	public int Port { get; set; }

	[ProtoMember(9)]
	public string AdminUsername { get; set; } = "";

	[ProtoMember(10)]
	public bool PasswordConfigured { get; set; }

	[ProtoMember(11)]
	public int ActiveSessions { get; set; }

	[ProtoMember(12)]
	public int SessionTimeoutMinutes { get; set; }

	[ProtoMember(13)]
	public bool SecureCookies { get; set; }

	[ProtoMember(14)]
	public string LastError { get; set; } = "";

	[ProtoMember(15)]
	public bool LocalAnonymousMode { get; set; }

	[ProtoMember(16)]
	public string LocalUrl { get; set; } = "";

	[ProtoMember(17)]
	public string PublicUrl { get; set; } = "";

	[ProtoMember(18)]
	public WebMapGuiSettingsPacket? WebMapSettings { get; set; }
}
