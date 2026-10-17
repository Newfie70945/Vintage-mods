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
		ulong[] array = Enumerable.Repeat(ulong.MaxValue, 64).ToArray();
		if (tokens == null)
		{
			return array.ToList();
		}
		int num = 0;
		foreach (string token in tokens)
		{
			string text = (token ?? "").Trim().ToLowerInvariant();
			if (text.Length == 0)
			{
				continue;
			}
			byte[] array2 = SHA256.HashData(Encoding.UTF8.GetBytes(text));
			ulong num2 = BinaryPrimitives.ReadUInt64LittleEndian(array2.AsSpan(0, 8));
			ulong num3 = BinaryPrimitives.ReadUInt64LittleEndian(array2.AsSpan(8, 8));
			if (num3 == 0)
			{
				num3 = 11400714819323198485uL;
			}
			for (int i = 0; i < 64; i++)
			{
				ulong num4 = num2 + (ulong)((long)(i + 1) * (long)num3);
				if (num4 < array[i])
				{
					array[i] = num4;
				}
			}
			num++;
			if (num < 4096)
			{
				continue;
			}
			break;
		}
		return array.ToList();
	}

	public static int SimilarityPercent(IReadOnlyList<ulong>? a, IReadOnlyList<ulong>? b)
	{
		if (a == null || b == null)
		{
			return 0;
		}
		int num = Math.Min(Math.Min(a.Count, b.Count), 64);
		if (num <= 0)
		{
			return 0;
		}
		int num2 = 0;
		int num3 = 0;
		for (int i = 0; i < num; i++)
		{
			if (a[i] != ulong.MaxValue && b[i] != ulong.MaxValue)
			{
				num2++;
				if (a[i] == b[i])
				{
					num3++;
				}
			}
		}
		return (num2 != 0) ? ((int)Math.Round((double)num3 * 100.0 / (double)num2, MidpointRounding.AwayFromZero)) : 0;
	}
}
