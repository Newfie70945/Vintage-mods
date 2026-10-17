using System.Collections.Generic;
using BlockLogger.WebDashboard;

namespace BlockLogger.WebConsole;

public sealed class WebConsoleSnapshot
{
	public bool CommandInputEnabled { get; set; }

	public long CommandInputExpiresUnixMilliseconds { get; set; }

	public double CpuPercent { get; set; }

	public double RamMb { get; set; }

	public int PlayersOnline { get; set; }

	public string LogPath { get; set; } = "";

	public long LogLength { get; set; }

	public List<string> Lines { get; set; } = new List<string>();

	public bool PlayerChatIncluded { get; set; }

	public List<string> ChatLines { get; set; } = new List<string>();

	public BadBlockEntitySnapshot BlockEntityAlerts { get; set; } = new BadBlockEntitySnapshot();
}
