using System;
using BlockLogger.Models;

namespace BlockLogger.Services;

public sealed record RecoveryCell(string Dimension, int X, int Y, int Z)
{
	public string Key => $"{Dimension}:{X}:{Y}:{Z}";

	public static RecoveryCell At(LogEventRecord r)
	{
		return new RecoveryCell(Normalize(r.Dimension), r.X, r.Y, r.Z);
	}

	public static RecoveryCell At(ContainerSnapshotRecord r)
	{
		return new RecoveryCell(Normalize(r.Dimension), r.X, r.Y, r.Z);
	}

	public static string Normalize(string d)
	{
		return d.Equals("overworld", StringComparison.OrdinalIgnoreCase) ? "0" : d;
	}
}
