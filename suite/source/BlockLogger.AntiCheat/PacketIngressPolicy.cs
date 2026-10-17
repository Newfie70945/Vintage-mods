using System;

namespace BlockLogger.AntiCheat;

internal static class PacketIngressPolicy
{
	internal static int BoundViewDistanceBlocks(int requested, int configuredMaximum, int serverMaximumChunks, int chunkSize)
	{
		int num = Math.Clamp(configuredMaximum, 64, 4096);
		if (serverMaximumChunks > 0 && chunkSize > 0)
		{
			num = (int)Math.Min(num, Math.Min(2147483647L, (long)serverMaximumChunks * (long)chunkSize));
		}
		num = Math.Max(32, num);
		return Math.Clamp(requested, 32, num);
	}

	internal static bool IsValidCoordinate(double value)
	{
		return double.IsFinite(value) && Math.Abs(value) <= 2147482623.0;
	}

	internal static bool IsValidBlockId(int blockId, int registryCount)
	{
		return blockId >= 0 && blockId < registryCount;
	}

	internal static bool IsValidFace(int face)
	{
		return face >= 0 && face <= 5;
	}

	internal static bool IsValidHit(double value)
	{
		return double.IsFinite(value) && value >= -16.0 && value <= 16.0;
	}
}
