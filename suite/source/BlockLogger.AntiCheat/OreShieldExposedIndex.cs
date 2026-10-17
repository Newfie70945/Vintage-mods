using System;
using System.Numerics;

namespace BlockLogger.AntiCheat;

internal sealed class OreShieldExposedIndex
{
	internal const int Volume = 32768;

	private ulong[]? words;

	public int Count { get; private set; }

	public bool Add(int index)
	{
		if ((uint)index >= 32768u)
		{
			return false;
		}
		if (words == null)
		{
			words = new ulong[512];
		}
		ulong num = (ulong)(1L << index);
		int num2 = index >> 6;
		if ((words[num2] & num) != 0)
		{
			return false;
		}
		words[num2] |= num;
		Count++;
		return true;
	}

	public bool Remove(int index)
	{
		if ((uint)index >= 32768u || words == null)
		{
			return false;
		}
		ulong num = (ulong)(1L << index);
		int num2 = index >> 6;
		if ((words[num2] & num) == 0)
		{
			return false;
		}
		words[num2] &= ~num;
		Count--;
		return true;
	}

	public int CopyWindow(int startIndex, Span<int> destination, out int nextIndex)
	{
		int num = Math.Clamp(startIndex, 0, 32768);
		nextIndex = 32768;
		if (words == null || Count == 0 || num == 32768)
		{
			return 0;
		}
		if (destination.Length == 0)
		{
			nextIndex = num;
			return 0;
		}
		int num2 = 0;
		int num3 = num >> 6;
		for (int i = num3; i < words.Length; i++)
		{
			ulong num4 = words[i];
			if (i == num3)
			{
				num4 &= (ulong)(-1L << (num & 0x3F));
			}
			while (num4 != 0)
			{
				int num5 = (i << 6) + BitOperations.TrailingZeroCount(num4);
				destination[num2++] = num5;
				num4 &= num4 - 1;
				if (num2 == destination.Length)
				{
					nextIndex = num5 + 1;
					return num2;
				}
			}
		}
		return num2;
	}
}
