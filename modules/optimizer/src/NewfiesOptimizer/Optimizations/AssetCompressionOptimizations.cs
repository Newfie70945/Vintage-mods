using System.Reflection;
using HarmonyLib;
using Vintagestory.Common;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

// Cache only the inspected server-assets buffer, never arbitrary game/terrain packets.
// Vanilla still serializes, sends, frames and accounts for every packet.
public static class AssetCompressionOptimizations
{
    private static readonly object Gate = new();
    private static byte[]? tracked, sourceSnapshot, compressed;
    private static object? compressor;
    private static int limit = 16 * 1024 * 1024;
    private static bool enabled, unsuitable;
    private static long hits, misses, failures, generation;
    public static MethodInfo Target => AccessTools.Method(typeof(Compression), nameof(Compression.CompressOffset4), [typeof(byte[]), typeof(int)]);
    public static MethodInfo PrefixMethod => AccessTools.Method(typeof(AssetCompressionOptimizations), nameof(Prefix));
    public static MethodInfo PostfixMethod => AccessTools.Method(typeof(AssetCompressionOptimizations), nameof(Postfix));
    public static MethodInfo[] ConflictTargets => [Target, AccessTools.Method(typeof(ServerMain), "SendServerAssets"), AccessTools.Method(typeof(ServerMain), "BuildServerAssetsPacket")];
    public static bool ValidateContract(out string reason)
    {
        var field = AccessTools.Field(typeof(ServerMain), "serverAssetsPacket");
        bool valid = Target?.ReturnType == typeof(byte[]) && field != null && AccessTools.Field(field.FieldType, "buffer")?.FieldType == typeof(byte[]);
        reason = valid ? "Verified assets buffer and compression signature." : "Official assets/compression layout unavailable.";
        return valid;
    }
    public static void Configure(bool value) { lock(Gate) { generation++; var buffer=tracked; ClearUnsafe(); if(value) tracked=buffer; enabled=value; } }
    public static void Track(byte[]? value, int maximumBytes)
    {
        lock(Gate)
        {
            int nextLimit=Math.Clamp(maximumBytes, 1048576, 33554432);
            if(value is not null && value.Length > nextLimit) value=null;
            if(limit!=nextLimit || !ReferenceEquals(tracked,value))
            { generation++; ClearUnsafe(); limit=nextLimit; tracked=value; }
        }
    }
    private static void ClearUnsafe() { tracked=null; sourceSnapshot=null; compressed=null; compressor=null; unsuitable=false; }
    public static (long Hits,long Misses,long Failures,long Bytes) Counters { get { lock(Gate)return(hits,misses,failures,(compressed?.LongLength??0)+(sourceSnapshot?.LongLength??0)); } }
    public sealed record Pending(byte[] Source, object Compressor, long Generation);
    public static bool Prefix(byte[] data, int len, ref byte[] __result, out Pending? __state)
    {
        __state=null;
        if(!Volatile.Read(ref enabled) || !ReferenceEquals(Volatile.Read(ref tracked),data)) return true;
        lock(Gate)
        {
            if(!enabled || unsuitable || !ReferenceEquals(data,tracked) || len<=1460 || len>data.Length || len>limit) return true;
            try
            {
                var source=data.AsSpan(0,len);
                var current=Compression.compressor;
                if(compressed != null && ReferenceEquals(compressor,current) && sourceSnapshot!.AsSpan().SequenceEqual(source))
                {
                    // Socket framing mutates its input. Each send must receive an independent copy.
                    __result=(byte[])compressed.Clone(); hits++; return false;
                }
                misses++; __state=new(source.ToArray(),current,generation); return true;
            }
            catch { failures++; enabled=false; ClearUnsafe(); return true; }
        }
    }
    public static void Postfix(byte[] data,int len,byte[] __result,Pending? __state)
    {
        if(__state is null) return;
        lock(Gate)
        {
            if(!enabled || __state.Generation!=generation || !ReferenceEquals(data,tracked) || __result is null) return;
            if(__result.LongLength+__state.Source.LongLength>limit || __result.LongLength>len*3L/4)
            {
                // Low-savings assets do not justify a retained copy and repeated byte comparisons.
                // Continue using stock until the buffer changes or the module is reapplied.
                unsuitable=true; sourceSnapshot=null; compressed=null; compressor=null; return;
            }
            try
            {
                // Never publish a cache entry for data changed during compression.
                if(!ReferenceEquals(Compression.compressor,__state.Compressor) || !data.AsSpan(0,len).SequenceEqual(__state.Source)) return;
                compressed=(byte[])__result.Clone(); sourceSnapshot=__state.Source; compressor=__state.Compressor;
            }
            catch { failures++; enabled=false; ClearUnsafe(); }
        }
    }
}
