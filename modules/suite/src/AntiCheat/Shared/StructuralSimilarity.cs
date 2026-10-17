using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace NewfiesAntiCheat.Shared;

public static class StructuralSimilarity
{
    public const int SignatureSize = 64;

    public static List<ulong> ComputeMinHash(IEnumerable<string>? tokens)
    {
        var signature = Enumerable.Repeat(ulong.MaxValue, SignatureSize).ToArray();
        if (tokens == null) return signature.ToList();

        int seen = 0;
        foreach (string raw in tokens)
        {
            string token = (raw ?? "").Trim().ToLowerInvariant();
            if (token.Length == 0) continue;
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            ulong h1 = BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(0, 8));
            ulong h2 = BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(8, 8));
            if (h2 == 0) h2 = 0x9e3779b97f4a7c15UL;
            for (int i = 0; i < SignatureSize; i++)
            {
                ulong candidate = unchecked(h1 + ((ulong)(i + 1) * h2));
                if (candidate < signature[i]) signature[i] = candidate;
            }
            seen++;
            if (seen >= 4096) break;
        }
        return signature.ToList();
    }

    public static int SimilarityPercent(IReadOnlyList<ulong>? a, IReadOnlyList<ulong>? b)
    {
        if (a == null || b == null) return 0;
        int count = Math.Min(Math.Min(a.Count, b.Count), SignatureSize);
        if (count <= 0) return 0;
        int valid = 0, equal = 0;
        for (int i = 0; i < count; i++)
        {
            if (a[i] == ulong.MaxValue || b[i] == ulong.MaxValue) continue;
            valid++;
            if (a[i] == b[i]) equal++;
        }
        return valid == 0 ? 0 : (int)Math.Round(equal * 100.0 / valid, MidpointRounding.AwayFromZero);
    }
}
