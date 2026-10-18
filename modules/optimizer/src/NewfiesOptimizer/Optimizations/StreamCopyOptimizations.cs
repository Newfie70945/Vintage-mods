using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Datastructures;

namespace NewfiesOptimizer.Optimizations;

/// <summary>A narrow copy substitution for the inspected official 1.22.7 stream. No packet interpretation or retained stream references.</summary>
public static class StreamCopyOptimizations
{
    public const int MinimumReadBytes = 256;
    private static int readEnabled;
    private static long acceleratedCalls, copiedBytes, fallbackCalls, failures;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static MethodInfo ReadTarget => typeof(FastMemoryStream).GetMethod(nameof(FastMemoryStream.Read), [typeof(byte[]), typeof(int), typeof(int)])!;
    public static MethodInfo ReadPrefixMethod => typeof(StreamCopyOptimizations).GetMethod(nameof(ReadPrefix), BindingFlags.Static | BindingFlags.Public)!;
    public static MethodInfo[] ReadConflictTargets => [ReadTarget, typeof(FastMemoryStream).GetProperty(nameof(FastMemoryStream.Position))!.GetMethod!, typeof(FastMemoryStream).GetProperty(nameof(FastMemoryStream.Position))!.SetMethod!];
    public static bool ReadEnabled => Volatile.Read(ref readEnabled) != 0;
    public static void ConfigureRead(bool enabled) => Volatile.Write(ref readEnabled, enabled ? 1 : 0);
    public static StreamCopySnapshot Snapshot => new(ReadEnabled, Interlocked.Read(ref acceleratedCalls), Interlocked.Read(ref copiedBytes), Interlocked.Read(ref fallbackCalls), Interlocked.Read(ref failures));

    public static bool ValidateContract(out string reason)
    {
        Type type = typeof(FastMemoryStream);
        FieldInfo? buffer = type.GetField("buffer", Instance);
        FieldInfo? length = type.GetField("bufferlength", Instance);
        PropertyInfo? position = type.GetProperty(nameof(FastMemoryStream.Position), Instance);
        MethodInfo? read = type.GetMethod(nameof(FastMemoryStream.Read), [typeof(byte[]), typeof(int), typeof(int)]);
        if (buffer?.FieldType != typeof(byte[]) || buffer.IsStatic || length?.FieldType != typeof(int) || length.IsStatic ||
            position?.PropertyType != typeof(long) || position.GetMethod?.DeclaringType != type || position.SetMethod?.DeclaringType != type ||
            read?.DeclaringType != type || read.ReturnType != typeof(int) || read.IsAbstract || read.GetMethodBody() is null)
        {
            reason = "FastMemoryStream buffer, length, position or Read method contract differs from the inspected official API.";
            return false;
        }
        reason = "Inspected byte-array stream layout and Read method are present. Assembly hashes and Harmony owners must also be checked by the registry.";
        return true;
    }

    // Every decline happens before modification. Invalid ranges and all aliases use the stock
    // byte loop, including its unusual partial-write, end-of-stream and exception behavior.
    public static bool ReadPrefix(FastMemoryStream __instance, byte[]? destBuffer, int offset, int count,
        byte[]? ___buffer, int ___bufferlength, ref int __result)
    {
        if (count < MinimumReadBytes || !ReadEnabled) return true;
        if (__instance.GetType() != typeof(FastMemoryStream) || ___buffer is null || destBuffer is null ||
            ReferenceEquals(___buffer, destBuffer) || ___bufferlength < 0 || ___bufferlength > ___buffer.Length)
        {
            Interlocked.Increment(ref fallbackCalls);
            return true;
        }
        long position = __instance.Position;
        if (position < 0 || position > ___bufferlength || offset < 0 || offset > destBuffer.Length)
        {
            Interlocked.Increment(ref fallbackCalls);
            return true;
        }
        int read = (int)Math.Min((long)count, ___bufferlength - position);
        if (read < MinimumReadBytes || read > destBuffer.Length - offset)
        {
            Interlocked.Increment(ref fallbackCalls);
            return true;
        }
        try
        {
            Buffer.BlockCopy(___buffer, (int)position, destBuffer, offset, read);
            __instance.Position = position + read;
            __result = read;
        }
        catch
        {
            // Never retry the original after a copy or setter may have changed observable state.
            Volatile.Write(ref readEnabled, 0);
            Interlocked.Increment(ref failures);
            throw;
        }
        Interlocked.Increment(ref acceleratedCalls);
        Interlocked.Add(ref copiedBytes, read);
        return false;
    }
}

public readonly record struct StreamCopySnapshot(bool Enabled, long AcceleratedCalls, long CopiedBytes, long FallbackCalls, long Failures);
