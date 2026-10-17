using System.Collections;
using System.Collections.Generic;
using System.Numerics;

namespace BlockLogger.AntiCheat;

internal sealed class OreShieldDiscoveryCells : IEnumerable<int>, IEnumerable
{
	private const int Volume = 32768;

	private const int PromotionThreshold = 256;

	private HashSet<int>? sparse = new HashSet<int>();

	private ulong[]? dense;

	public int Count { get; private set; }

	public bool IsDense => dense != null;

	public bool Contains(int index)
	{
		return (uint)index < 32768u && ((dense != null) ? ((dense[index >> 6] & (ulong)(1L << index)) != 0) : sparse.Contains(index));
	}

	public bool Add(int index)
	{
		if ((uint)index >= 32768u)
		{
			return false;
		}
		if (dense == null)
		{
			if (!sparse.Add(index))
			{
				return false;
			}
			Count++;
			if (Count == 256)
			{
				dense = new ulong[512];
				foreach (int item in sparse)
				{
					dense[item >> 6] |= (ulong)(1L << item);
				}
				sparse = null;
			}
			return true;
		}
		ulong num = (ulong)(1L << index);
		if ((dense[index >> 6] & num) != 0)
		{
			return false;
		}
		dense[index >> 6] |= num;
		Count++;
		return true;
	}

	public OreShieldDiscoveryCells Clone()
	{
		OreShieldDiscoveryCells oreShieldDiscoveryCells = new OreShieldDiscoveryCells
		{
			Count = Count
		};
		if (dense != null)
		{
			oreShieldDiscoveryCells.dense = (ulong[])dense.Clone();
			oreShieldDiscoveryCells.sparse = null;
		}
		else
		{
			oreShieldDiscoveryCells.sparse = new HashSet<int>(sparse);
		}
		return oreShieldDiscoveryCells;
	}

	public bool Remove(int index)
	{
		if (!Contains(index))
		{
			return false;
		}
		if (dense == null)
		{
			sparse.Remove(index);
		}
		else
		{
			dense[index >> 6] &= (ulong)(~(1L << index));
		}
		Count--;
		return true;
	}

	public IEnumerator<int> GetEnumerator()
	{
		if (dense == null)
		{
			foreach (int item in sparse)
			{
				yield return item;
			}
			yield break;
		}
		for (int word = 0; word < dense.Length; word++)
		{
			for (ulong bits = dense[word]; bits != 0; bits &= bits - 1)
			{
				int bit = BitOperations.TrailingZeroCount(bits);
				yield return word * 64 + bit;
			}
		}
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}
}
