namespace BlockLogger.PlayerList;

public sealed class PublicPlayerListClientPresence
{
	public string PlayerUid { get; set; } = "";

	public string PlayerName { get; set; } = "";

	public string ClientVersion { get; set; } = "";

	public int ProtocolVersion { get; set; }

	public string Capabilities { get; set; } = "";

	public long FirstSeenUnixMilliseconds { get; set; }

	public long LastSeenUnixMilliseconds { get; set; }
}
