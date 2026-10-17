using System;
using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

internal sealed class OreShieldScanCursor
{
	internal readonly record struct Offset(int X, int Y, int Z);

	private static readonly Offset[][] OffsetsByRadius = BuildOffsets();

	private Offset[] offsets = Array.Empty<Offset>();

	private int offsetIndex;

	private int centerX;

	private int centerY;

	private int centerZ;

	private int radius;

	private int dimension;

	public bool HasPendingSweep { get; private set; }

	public int LocalIndex { get; set; }

	public Offset Current => offsets[offsetIndex];

	public void Configure(int x, int y, int z, int scanRadius, int scanDimension)
	{
		scanRadius = Math.Clamp(scanRadius, 1, 6);
		if (offsets.Length == 0 || centerX != x || centerY != y || centerZ != z || radius != scanRadius || dimension != scanDimension)
		{
			centerX = x;
			centerY = y;
			centerZ = z;
			radius = scanRadius;
			dimension = scanDimension;
			offsets = OffsetsByRadius[scanRadius];
			offsetIndex = 0;
			LocalIndex = 0;
			HasPendingSweep = true;
		}
	}

	public void BeginSweep()
	{
		HasPendingSweep = true;
	}

	public bool AdvanceChunk()
	{
		LocalIndex = 0;
		if (++offsetIndex < offsets.Length)
		{
			return true;
		}
		offsetIndex = 0;
		HasPendingSweep = false;
		return false;
	}

	private static Offset[][] BuildOffsets()
	{
		Offset[][] array = new Offset[7][]
		{
			Array.Empty<Offset>(),
			null,
			null,
			null,
			null,
			null,
			null
		};
		for (int i = 1; i <= 6; i++)
		{
			List<Offset> list = new List<Offset>();
			for (int j = -i; j <= i; j++)
			{
				for (int k = -i; k <= i; k++)
				{
					for (int l = -i; l <= i; l++)
					{
						list.Add(new Offset(j, k, l));
					}
				}
			}
			list.Sort((Offset a, Offset b) =>
			{
				int num = (a.X * a.X + a.Y * a.Y + a.Z * a.Z).CompareTo(b.X * b.X + b.Y * b.Y + b.Z * b.Z);
				if (num != 0)
				{
					return num;
				}
				num = a.Y.CompareTo(b.Y);
				if (num != 0)
				{
					return num;
				}
				num = a.X.CompareTo(b.X);
				return (num != 0) ? num : a.Z.CompareTo(b.Z);
			});
			array[i] = list.ToArray();
		}
		return array;
	}
}
