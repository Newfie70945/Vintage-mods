using System.Collections.Generic;

namespace BlockLogger.ModUpdates;

public sealed class ModUpdateSnapshot
{
	public long CheckedUnixMilliseconds { get; set; }

	public string TargetGameVersion { get; set; } = "";

	public bool CheckInProgress { get; set; }

	public string LastError { get; set; } = "";

	public string LastWarning { get; set; } = "";

	public int LookupFailedCount { get; set; }

	public int UpdateCount { get; set; }

	public int CurrentCount { get; set; }

	public int UnknownCount { get; set; }

	public bool RestartRequired { get; set; }

	public List<string> StagedUpdates { get; set; } = new List<string>();

	public List<ModUpdateEntry> Mods { get; set; } = new List<ModUpdateEntry>();
}
