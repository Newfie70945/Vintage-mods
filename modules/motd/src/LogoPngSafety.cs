using System;
using System.Buffers.Binary;

namespace NewfiesCommunityMOTD;

// A small compressed file can still describe an enormous native image. Check before decoding.
internal static class LogoPngSafety
{
    internal const int MaximumBytes=1024*1024;
    private const uint MaximumDimension=4096;
    private const ulong MaximumPixels=4UL*1024*1024;
    public static bool IsSafe(byte[]? bytes)
    {
        if(bytes==null||bytes.Length<33||bytes.Length>MaximumBytes)return false;
        ReadOnlySpan<byte> header=bytes;
        if(!header[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}) ||
            BinaryPrimitives.ReadUInt32BigEndian(header[8..12])!=13 ||
            !header[12..16].SequenceEqual("IHDR"u8))return false;
        uint width=BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        uint height=BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        return width>0 && height>0 && width<=MaximumDimension && height<=MaximumDimension && (ulong)width*height<=MaximumPixels;
    }
}
