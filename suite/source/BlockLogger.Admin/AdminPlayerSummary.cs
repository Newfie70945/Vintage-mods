namespace BlockLogger.Admin;

public sealed class AdminPlayerSummary
{
	public string PlayerUid { get; set; } = "";

	public string Name { get; set; } = "";

	public string Nickname { get; set; } = "";

	public bool Online { get; set; }

	public string Role { get; set; } = "";

	public string GameMode { get; set; } = "";

	public int PingMilliseconds { get; set; }

	public long FirstSeenUnixMilliseconds { get; set; }

	public long LastSeenUnixMilliseconds { get; set; }

	public long SessionSeconds { get; set; }

	public long TotalPlaySeconds { get; set; }

	public int ActiveWarnings { get; set; }

	public bool Muted { get; set; }

	public long MuteUntilUnixMilliseconds { get; set; }

	public bool Banned { get; set; }

	public long BanUntilUnixMilliseconds { get; set; }

	public bool Frozen { get; set; }

	public bool Jailed { get; set; }

	public long JailUntilUnixMilliseconds { get; set; }

	public bool Afk { get; set; }

	public long IdleSeconds { get; set; }

	public string Position { get; set; } = "";

	public float Health { get; set; }

	public float MaxHealth { get; set; }

	public bool PlayerListClientDetected { get; set; }

	public string PlayerListClientVersion { get; set; } = "";
}
