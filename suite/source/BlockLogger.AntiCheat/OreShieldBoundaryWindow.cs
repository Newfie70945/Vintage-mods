using System;
using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

internal static class OreShieldBoundaryWindow
{
	public static int CopyHorizontalFace(HashSet<int> indices, bool fixedX, int coordinate, Span<int> destination)
	{
		if (destination.Length < 1024 || (coordinate != 0 && coordinate != 31))
		{
			throw new ArgumentException("A horizontal chunk face requires 1,024 cells and coordinate 0 or 31.");
		}
		int result = 0;
		if (indices.Count < 1024)
		{
			foreach (int index in indices)
			{
				if ((uint)index < 32768u)
				{
					int num = (fixedX ? (index & 0x1F) : ((index >> 5) & 0x1F));
					if (num == coordinate)
					{
						destination[result++] = index;
					}
				}
			}
		}
		else
		{
			for (int i = 0; i < 32; i++)
			{
				for (int j = 0; j < 32; j++)
				{
					int num2 = (fixedX ? coordinate : j);
					int num3 = (fixedX ? j : coordinate);
					int num4 = (i * 32 + num3) * 32 + num2;
					if (indices.Contains(num4))
					{
						destination[result++] = num4;
					}
				}
			}
		}
		return result;
	}
}
