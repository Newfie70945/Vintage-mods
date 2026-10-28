using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.MathTools;

namespace BlockLogger.SeedPrivacy;

// Outbound-only protobuf edits. Unrelated fields (including unknown fields) keep
// their exact bytes; no native WeatherState or saved region object is modified.
internal static class SeedPrivacyWire
{
    internal const int MaximumBytes = 16 * 1024 * 1024;
    private const int MaximumFields = 65536;
    private readonly record struct Field(int Number, int Wire, int Start, int Data, int End);

    public static byte[] Weather(byte[] source, int publicSeed)
    {
        var fields = Parse(source);
        int x = 0, z = 0;
        foreach (var f in fields)
        {
            if (f.Number is 1 or 2)
            {
                if (f.Wire != 0) throw Invalid();
                int at = f.Data;
                int value = unchecked((int)Read(source, ref at));
                if (f.Number == 1) x = value; else z = value;
            }
            if (f.Number is >= 12 and <= 14 && f.Wire != 0) throw Invalid();
        }
        var rng = new LCGRandom(publicSeed);
        rng.InitPositionSeed(x / 3, z / 3);
        long[] seeds = [rng.worldSeed, rng.mapGenSeed, rng.currentSeed];
        bool[] seen = new bool[3];
        using var output = new MemoryStream(Math.Min(MaximumBytes, checked(source.Length + 64)));
        foreach (var f in fields)
        {
            if (f.Number is >= 12 and <= 14)
            {
                Write(output, (ulong)(f.Number << 3));
                Write(output, unchecked((ulong)seeds[f.Number - 12]));
                seen[f.Number - 12] = true;
            }
            else output.Write(source.AsSpan(f.Start, f.End - f.Start));
            CheckSize(output);
        }
        for (int i = 0; i < 3; i++) if (!seen[i]) { Write(output, (ulong)((i + 12) << 3)); Write(output, unchecked((ulong)seeds[i])); }
        CheckSize(output);
        return output.ToArray();
    }

    // Dictionary<string,byte[]> is a repeated field-1 key/value entry. Copy
    // unrecognized entries exactly; edit only the native weatherState value.
    public static byte[]? Region(byte[] source, int publicSeed)
    {
        if (source.AsSpan().IndexOf("weatherState"u8) < 0) return null;
        var fields = Parse(source);
        using var output = new MemoryStream(source.Length);
        bool changed = false;
        foreach (var f in fields)
        {
            byte[]? entry = null;
            if (f.Number == 1)
            {
                if (f.Wire != 2) throw Invalid();
                entry = Entry(source.AsSpan(f.Data, f.End - f.Data), publicSeed);
            }
            if (entry == null) output.Write(source.AsSpan(f.Start, f.End - f.Start));
            else { Write(output, 10); Write(output, (ulong)entry.Length); output.Write(entry); changed = true; }
            CheckSize(output);
        }
        return changed ? output.ToArray() : null;
    }

    private static byte[]? Entry(ReadOnlySpan<byte> source, int publicSeed)
    {
        var fields = Parse(source);
        Field? key = null, value = null;
        foreach (var f in fields)
        {
            if (f.Number == 1) { if (key != null || f.Wire != 2) throw Invalid(); key = f; }
            if (f.Number == 2) { if (value != null || f.Wire != 2) throw Invalid(); value = f; }
        }
        if (key is not { } k || !source.Slice(k.Data, k.End - k.Data).SequenceEqual("weatherState"u8)) return null;
        if (value is not { } v) throw Invalid();
        byte[] weather = Weather(source.Slice(v.Data, v.End - v.Data).ToArray(), publicSeed);
        using var output = new MemoryStream(checked(source.Length + weather.Length));
        foreach (var f in fields)
        {
            if (f.Number == 2) { Write(output, 18); Write(output, (ulong)weather.Length); output.Write(weather); }
            else output.Write(source.Slice(f.Start, f.End - f.Start));
            CheckSize(output);
        }
        return output.ToArray();
    }

    private static List<Field> Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length > MaximumBytes) throw Invalid();
        var fields = new List<Field>();
        int at = 0;
        while (at < source.Length)
        {
            if (fields.Count >= MaximumFields) throw Invalid();
            int start = at;
            ulong tag = Read(source, ref at);
            int wire = (int)(tag & 7);
            if ((tag >> 3) is 0 or > 536870911) throw Invalid();
            int data = at;
            switch (wire)
            {
                case 0: Read(source, ref at); break;
                case 1: at = checked(at + 8); break;
                case 2:
                    ulong length = Read(source, ref at);
                    if (length > MaximumBytes) throw Invalid();
                    data = at; at = checked(at + (int)length); break;
                case 5: at = checked(at + 4); break;
                default: throw Invalid();
            }
            if (at > source.Length) throw Invalid();
            fields.Add(new((int)(tag >> 3), wire, start, data, at));
        }
        return fields;
    }
    private static ulong Read(ReadOnlySpan<byte> data, ref int at)
    {
        ulong value = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (at >= data.Length) throw Invalid();
            byte b = data[at++];
            if (shift == 63 && b > 1) throw Invalid();
            value |= (ulong)(b & 127) << shift;
            if (b < 128) return value;
        }
        throw Invalid();
    }
    private static void Write(Stream output, ulong value)
    {
        while (value >= 128) { output.WriteByte((byte)((value & 127) | 128)); value >>= 7; }
        output.WriteByte((byte)value);
    }
    private static void CheckSize(MemoryStream output) { if (output.Length > MaximumBytes) throw Invalid(); }
    private static InvalidDataException Invalid() => new("Unsupported or malformed seed-bearing protobuf payload; original data was not sent.");
}
