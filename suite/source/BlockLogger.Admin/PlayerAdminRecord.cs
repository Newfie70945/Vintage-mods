using System.Collections.Generic;

namespace BlockLogger.Admin;

public sealed class PlayerAdminRecord
{
	public string PlayerUid { get; set; } = "";

	public string LastKnownName { get; set; } = "";

	public long FirstSeenUnixMilliseconds { get; set; }

	public long LastSeenUnixMilliseconds { get; set; }

	public long TotalPlaySeconds { get; set; }

	public long SessionStartedUnixMilliseconds { get; set; }

	public List<AdminWarningRecord> Warnings { get; set; } = new List<AdminWarningRecord>();

	public List<AdminNoteRecord> Notes { get; set; } = new List<AdminNoteRecord>();

	public long MuteUntilUnixMilliseconds { get; set; }

	public bool PermanentMute { get; set; }

	public string MuteReason { get; set; } = "";

	public string MutedBy { get; set; } = "";

	public long BanUntilUnixMilliseconds { get; set; }

	public string BanReason { get; set; } = "";

	public string BannedBy { get; set; } = "";

	public bool Frozen { get; set; }

	public long FreezeUntilUnixMilliseconds { get; set; }

	public string FrozenBy { get; set; } = "";

	public string FreezeReason { get; set; } = "";
}
