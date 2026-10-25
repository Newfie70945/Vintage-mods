using System;
using System.IO;
using System.Security.Cryptography;

namespace NewfiesCommunityMOTD;

/// <summary>Stat before allocating; cache one immutable PNG and its hash until it changes.</summary>
internal sealed class CachedLogoFile
{
    internal const int MaximumBytes = LogoPngSafety.MaximumBytes;
    private readonly object gate = new();
    private string cachedPath = "";
    private long cachedLength = -1, cachedWrite, cachedCreated;
    private (byte[] Bytes, string Hash) cached = (Array.Empty<byte>(), "");

    public (byte[] Bytes, string Hash) Read(string path)
    {
        lock (gate)
        {
            var info = new FileInfo(path);
            if (!info.Exists) { cachedPath = ""; return (Array.Empty<byte>(), ""); }
            long length = info.Length, written = info.LastWriteTimeUtc.Ticks, created = info.CreationTimeUtc.Ticks;
            if (path == cachedPath && length == cachedLength && written == cachedWrite && created == cachedCreated) return cached;
            (byte[] Bytes, string Hash) result = (Array.Empty<byte>(), "");
            if (length >= 8 && length <= MaximumBytes)
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length != length) return result;
                byte[] bytes = new byte[(int)length];
                input.ReadExactly(bytes);
                // Reject a file that grew during the read and validate the complete PNG signature.
                if (input.ReadByte() != -1) return result;
                if (LogoPngSafety.IsSafe(bytes))
                    result = (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            }
            info.Refresh();
            if (!info.Exists || info.Length != length || info.LastWriteTimeUtc.Ticks != written || info.CreationTimeUtc.Ticks != created) return (Array.Empty<byte>(), "");
            cachedPath = path; cachedLength = length; cachedWrite = written; cachedCreated = created; cached = result;
            return result;
        }
    }
}
