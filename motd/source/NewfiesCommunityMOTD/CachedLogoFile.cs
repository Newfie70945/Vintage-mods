using System;
using System.IO;
using System.Security.Cryptography;

namespace NewfiesCommunityMOTD;

internal sealed class CachedLogoFile
{
	internal const int MaximumBytes = 1048576;

	private readonly object gate = new object();

	private string cachedPath = "";

	private long cachedLength = -1L;

	private long cachedWrite;

	private long cachedCreated;

	private (byte[] Bytes, string Hash) cached = (Bytes: Array.Empty<byte>(), Hash: "");

	public (byte[] Bytes, string Hash) Read(string path)
	{
		lock (gate)
		{
			FileInfo fileInfo = new FileInfo(path);
			if (!fileInfo.Exists)
			{
				cachedPath = "";
				return (Bytes: Array.Empty<byte>(), Hash: "");
			}
			long length = fileInfo.Length;
			long ticks = fileInfo.LastWriteTimeUtc.Ticks;
			long ticks2 = fileInfo.CreationTimeUtc.Ticks;
			if (path == cachedPath && length == cachedLength && ticks == cachedWrite && ticks2 == cachedCreated)
			{
				return cached;
			}
			(byte[], string) result = (Array.Empty<byte>(), "");
			if (length >= 8 && length <= 1048576)
			{
				using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
				if (fileStream.Length != length)
				{
					return result;
				}
				byte[] array = new byte[(int)length];
				fileStream.ReadExactly(array);
				if (fileStream.ReadByte() != -1)
				{
					return result;
				}
				if (((ReadOnlySpan<byte>)array.AsSpan(0, 8)).SequenceEqual((ReadOnlySpan<byte>)new byte[8] { 137, 80, 78, 71, 13, 10, 26, 10 }))
				{
					result = (array, Convert.ToHexString(SHA256.HashData(array)).ToLowerInvariant());
				}
			}
			fileInfo.Refresh();
			if (!fileInfo.Exists || fileInfo.Length != length || fileInfo.LastWriteTimeUtc.Ticks != ticks || fileInfo.CreationTimeUtc.Ticks != ticks2)
			{
				return (Bytes: Array.Empty<byte>(), Hash: "");
			}
			cachedPath = path;
			cachedLength = length;
			cachedWrite = ticks;
			cachedCreated = ticks2;
			cached = result;
			return result;
		}
	}
}
