using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace NewfiesAntiCheat.Shared;

/// <summary>
/// Builds an identifier-independent fingerprint from managed IL opcode shape. Operand values
/// (metadata tokens, strings, branch destinations, constants) are intentionally ignored so a
/// simple repack/rename/re-MVID does not erase the signal. This is supporting evidence, not a
/// cryptographic identity check.
/// </summary>
public static class IlBehaviorFingerprint
{
    private const int MaxOpcodesPerMethod = 768;
    private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodeMap();

    public static string BuildMethodToken(byte[]? il, int parameterCount, int exceptionRegionCount)
    {
        if (il == null || il.Length == 0) return "";
        try
        {
            var codes = new List<string>(Math.Min(il.Length, MaxOpcodesPerMethod));
            int offset = 0;
            while (offset < il.Length && codes.Count < MaxOpcodesPerMethod)
            {
                int first = il[offset++];
                short key;
                if (first == 0xFE)
                {
                    if (offset >= il.Length) return "";
                    key = unchecked((short)(0xFE00 | il[offset++]));
                }
                else key = (short)first;

                if (!OpCodesByValue.TryGetValue(key, out OpCode op)) return "";
                codes.Add(unchecked((ushort)op.Value).ToString("x4"));
                int operand = OperandBytes(op.OperandType, il, offset);
                if (operand < 0 || offset + operand > il.Length) return "";
                offset += operand;
            }

            if (codes.Count == 0) return "";
            return $"P{Math.Max(0, parameterCount)}:E{Math.Max(0, exceptionRegionCount)}:" + string.Join(',', codes);
        }
        catch { return ""; }
    }

    private static int OperandBytes(OperandType type, byte[] il, int offset)
    {
        return type switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod or
            OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or
            OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => SwitchBytes(il, offset),
            _ => -1
        };
    }

    private static int SwitchBytes(byte[] il, int offset)
    {
        if (offset < 0 || offset + 4 > il.Length) return -1;
        int count = BitConverter.ToInt32(il, offset);
        if (count < 0 || count > 8192) return -1;
        long size = 4L + count * 4L;
        return size > int.MaxValue ? -1 : (int)size;
    }

    private static Dictionary<short, OpCode> BuildOpCodeMap()
    {
        var result = new Dictionary<short, OpCode>();
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode)) continue;
            try
            {
                var op = (OpCode)field.GetValue(null)!;
                result[op.Value] = op;
            }
            catch { }
        }
        return result;
    }
}
