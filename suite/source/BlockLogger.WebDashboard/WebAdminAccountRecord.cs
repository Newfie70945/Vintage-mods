using System;

namespace BlockLogger.WebDashboard;

public sealed class WebAdminAccountRecord
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N");

	public string Username { get; set; } = "";

	public string Role { get; set; } = "Administrator";

	public bool Enabled { get; set; } = true;

	public string PasswordSalt { get; set; } = "";

	public string PasswordHash { get; set; } = "";

	public int PasswordIterations { get; set; } = 210000;

	public long CreatedUnixMilliseconds { get; set; }

	public long LastLoginUnixMilliseconds { get; set; }

	public string LastLoginRemote { get; set; } = "";

	public long LastPasswordChangedUnixMilliseconds { get; set; }

	public int FailedLoginCount { get; set; }

	public long LockedUntilUnixMilliseconds { get; set; }
}
