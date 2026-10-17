namespace BlockLogger.AntiCheat;

public sealed class AntiCheatConnectionDiagnosticWebSnapshot
{
	public long Utc { get; set; }

	public string PlayerUid { get; set; } = "";

	public string PlayerName { get; set; } = "";

	public string Code { get; set; } = "";

	public string Stage { get; set; } = "";

	public string Action { get; set; } = "";

	public string Detail { get; set; } = "";

	public string Status { get; set; } = "";

	public bool ClientInstalled { get; set; }

	public string ClientVersion { get; set; } = "";

	public int ProtocolVersion { get; set; }

	public long LastHelloUtc { get; set; }

	public long LastHeartbeatUtc { get; set; }

	public long StartupGateStartedUtc { get; set; }

	public string ChallengeId { get; set; } = "";
}
