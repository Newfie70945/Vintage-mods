using System;
using System.Collections.Generic;

namespace BlockLogger.Admin;

public sealed class LagHereReport
{
	public int RadiusChunks { get; set; }

	public int Entities { get; set; }

	public int DroppedItems { get; set; }

	public int Players { get; set; }

	public int LoadedChunks { get; set; }

	public double MainThreadDelayMs { get; set; }

	public double CpuPercent { get; set; }

	public int BlockLoggerQueue { get; set; }

	public Dictionary<string, int> TopEntityTypes { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
}
