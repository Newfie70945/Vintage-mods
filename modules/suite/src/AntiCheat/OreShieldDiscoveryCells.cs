using System.Collections;
using System.Numerics;

namespace BlockLogger.AntiCheat;

// Small discoveries stay sparse. Dense discovered chunks use a 4 KiB bitset
// instead of thousands of HashSet entries. Promotion never loses a discovery.
internal sealed class OreShieldDiscoveryCells : IEnumerable<int>
{
    private const int Volume = 32768, PromotionThreshold = 256;
    private HashSet<int>? sparse = new();
    private ulong[]? dense;
    public int Count { get; private set; }
    public bool IsDense => dense != null;
    public bool Contains(int index) => (uint)index < Volume &&
        (dense != null ? (dense[index >> 6] & (1UL << (index & 63))) != 0 : sparse!.Contains(index));
    public bool Add(int index)
    {
        if ((uint)index >= Volume) return false;
        if (dense == null)
        {
            if (!sparse!.Add(index)) return false;
            Count++;
            if (Count == PromotionThreshold)
            {
                dense = new ulong[Volume / 64];
                foreach (int value in sparse) dense[value >> 6] |= 1UL << (value & 63);
                sparse = null;
            }
            return true;
        }
        ulong mask = 1UL << (index & 63);
        if ((dense[index >> 6] & mask) != 0) return false;
        dense[index >> 6] |= mask; Count++; return true;
    }
    public OreShieldDiscoveryCells Clone()
    {
        var copy=new OreShieldDiscoveryCells {Count=Count};
        if(dense!=null){copy.dense=(ulong[])dense.Clone();copy.sparse=null;}
        else copy.sparse=new HashSet<int>(sparse!);
        return copy;
    }
    public bool Remove(int index)
    {
        if (!Contains(index)) return false;
        if (dense == null) sparse!.Remove(index);
        else dense[index >> 6] &= ~(1UL << (index & 63));
        Count--;
        return true;
    }
    public IEnumerator<int> GetEnumerator()
    {
        if (dense == null) { foreach (int index in sparse!) yield return index; yield break; }
        for (int word = 0; word < dense.Length; word++)
        {
            ulong bits = dense[word];
            while (bits != 0) { int bit = BitOperations.TrailingZeroCount(bits); yield return word * 64 + bit; bits &= bits - 1; }
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
