using System;
using System.Collections.Generic;
using System.Numerics;

namespace BlockLogger.AntiCheat;

/// <summary>
/// Fixed-size, mutation-safe exposed-cell index. Window reads never copy a whole dense chunk,
/// and a cursor remains valid when a block change adds or removes entries between ticks.
/// Callers serialize reads and writes with the Ore Shield state lock.
/// </summary>
internal sealed class OreShieldExposedIndex
{
    internal const int Volume = 32 * 32 * 32;
    private ulong[]? words;
    public int Count { get; private set; }

    public bool Add(int index)
    {
        if ((uint)index >= Volume) return false;
        words ??= new ulong[Volume / 64];
        ulong mask = 1UL << (index & 63);
        int word = index >> 6;
        if ((words[word] & mask) != 0) return false;
        words[word] |= mask;
        Count++;
        return true;
    }

    public bool Remove(int index)
    {
        if ((uint)index >= Volume || words == null) return false;
        ulong mask = 1UL << (index & 63);
        int word = index >> 6;
        if ((words[word] & mask) == 0) return false;
        words[word] &= ~mask;
        Count--;
        return true;
    }

    public int CopyWindow(int startIndex, Span<int> destination, out int nextIndex)
    {
        int start = Math.Clamp(startIndex, 0, Volume);
        nextIndex = Volume;
        if (words == null || Count == 0 || start == Volume) return 0;
        if (destination.Length == 0) { nextIndex = start; return 0; }
        int written = 0;
        int firstWord = start >> 6;
        for (int word = firstWord; word < words.Length; word++)
        {
            ulong bits = words[word];
            if (word == firstWord) bits &= ulong.MaxValue << (start & 63);
            while (bits != 0)
            {
                int index = (word << 6) + BitOperations.TrailingZeroCount(bits);
                destination[written++] = index;
                bits &= bits - 1;
                if (written == destination.Length)
                {
                    nextIndex = index + 1;
                    return written;
                }
            }
        }
        return written;
    }
}

internal sealed class OreShieldWorkBudget
{
    public int Remaining { get; private set; }
    public int Used { get; private set; }
    public OreShieldWorkBudget(int limit) => Remaining = Math.Max(0, limit);
    public bool TryTake()
    {
        if (Remaining == 0) return false;
        Remaining--;
        Used++;
        return true;
    }
}

internal static class OreShieldBoundaryWindow
{
    public static int CopyHorizontalFace(HashSet<int> indices, bool fixedX, int coordinate, Span<int> destination)
    {
        if (destination.Length < 32 * 32 || (coordinate != 0 && coordinate != 31))
            throw new ArgumentException("A horizontal chunk face requires 1,024 cells and coordinate 0 or 31.");
        int count = 0;
        if (indices.Count < 32 * 32)
        {
            // Sparse geology is cheaper to filter; dense geology is cheaper to probe by face.
            foreach (int index in indices)
            {
                if ((uint)index >= OreShieldExposedIndex.Volume) continue;
                int axis = fixedX ? index & 31 : (index >> 5) & 31;
                if (axis == coordinate) destination[count++] = index;
            }
        }
        else
        {
            for (int y = 0; y < 32; y++)
            for (int offset = 0; offset < 32; offset++)
            {
                int x = fixedX ? coordinate : offset;
                int z = fixedX ? offset : coordinate;
                int index = (y * 32 + z) * 32 + x;
                if (indices.Contains(index)) destination[count++] = index;
            }
        }
        return count;
    }
}

/// <summary>Visits nearby chunks first, then resumes the same sweep on the next tick.</summary>
internal sealed class OreShieldScanCursor
{
    internal readonly record struct Offset(int X, int Y, int Z);
    private static readonly Offset[][] OffsetsByRadius = BuildOffsets();
    private Offset[] offsets = Array.Empty<Offset>();
    private int offsetIndex;
    private int centerX, centerY, centerZ, radius, dimension;
    public bool HasPendingSweep { get; private set; }
    public int LocalIndex { get; set; }
    public Offset Current => offsets[offsetIndex];

    public void Configure(int x, int y, int z, int scanRadius, int scanDimension)
    {
        scanRadius = Math.Clamp(scanRadius, 1, 6);
        if (offsets.Length > 0 && centerX == x && centerY == y && centerZ == z &&
            radius == scanRadius && dimension == scanDimension) return;
        centerX = x; centerY = y; centerZ = z; radius = scanRadius; dimension = scanDimension;
        offsets = OffsetsByRadius[scanRadius];
        offsetIndex = 0;
        LocalIndex = 0;
        HasPendingSweep = true;
    }

    public void BeginSweep() => HasPendingSweep = true;

    public bool AdvanceChunk()
    {
        LocalIndex = 0;
        if (++offsetIndex < offsets.Length) return true;
        offsetIndex = 0;
        HasPendingSweep = false;
        return false;
    }

    private static Offset[][] BuildOffsets()
    {
        var result = new Offset[7][];
        result[0] = Array.Empty<Offset>();
        for (int r = 1; r <= 6; r++)
        {
            var list = new List<Offset>();
            for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
            for (int z = -r; z <= r; z++) list.Add(new Offset(x, y, z));
            list.Sort((a, b) =>
            {
                int d = (a.X * a.X + a.Y * a.Y + a.Z * a.Z).CompareTo(b.X * b.X + b.Y * b.Y + b.Z * b.Z);
                if (d != 0) return d;
                d = a.Y.CompareTo(b.Y); if (d != 0) return d;
                d = a.X.CompareTo(b.X); return d != 0 ? d : a.Z.CompareTo(b.Z);
            });
            result[r] = list.ToArray();
        }
        return result;
    }
}
