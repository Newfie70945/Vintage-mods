using System.Collections.Generic;

namespace BlockLogger.Restarts;

public sealed class AutomatedRestartSnapshot
{
	public bool Available { get; set; } = true;

	public bool Enabled { get; set; }

	public string RestartTimeUtc { get; set; } = "00:00:00";

	public List<string> RestartTimesUtc { get; set; } = new List<string>();

	public List<string> EffectiveRestartTimesUtc { get; set; } = new List<string>();

	public List<int> WarningMinutes { get; set; } = new List<int>();

	public bool AnnounceInGame { get; set; }

	public bool AnnounceToDiscord { get; set; }

	public string Reason { get; set; } = "";

	public long NextRestartUnixMilliseconds { get; set; }

	public long RemainingSeconds { get; set; }

	public string NextRestartUtc { get; set; } = "";

	public bool CustomRestartScheduled { get; set; }

	public long CustomRestartUnixMilliseconds { get; set; }

	public long CustomRestartRemainingSeconds { get; set; }

	public string CustomRestartUtc { get; set; } = "";

	public string CustomRestartReason { get; set; } = "";

	public bool Th3EssentialsDetected { get; set; }

	public string RootRestartTimeOwner { get; set; } = "";

	public string FallbackCommand { get; set; } = "/nse restarttime";

	public string ConfigFile { get; set; } = "NewfiesAutomatedRestarts.json";

	public string RestartBehavior { get; set; } = "Graceful shutdown; the server host/wrapper must relaunch the process.";
}
