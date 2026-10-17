using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace NewfiesAntiCheat.Shared;

public static class IlBehaviorFingerprint
{
	private const int MaxOpcodesPerMethod = 768;

	private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodeMap();

	public static string BuildMethodToken(byte[]? il, int parameterCount, int exceptionRegionCount)
	{
		if (il == null || il.Length == 0)
		{
			return "";
		}
		try
		{
			List<string> list = new List<string>(Math.Min(il.Length, 768));
			int num2;
			for (int i = 0; i < il.Length; i += num2)
			{
				if (list.Count >= 768)
				{
					break;
				}
				int num = il[i++];
				short key;
				if (num == 254)
				{
					if (i >= il.Length)
					{
						return "";
					}
					key = (short)(0xFE00 | il[i++]);
				}
				else
				{
					key = (short)num;
				}
				if (!OpCodesByValue.TryGetValue(key, out var value))
				{
					return "";
				}
				list.Add(((ushort)value.Value).ToString("x4"));
				num2 = OperandBytes(value.OperandType, il, i);
				if (num2 < 0 || i + num2 > il.Length)
				{
					return "";
				}
			}
			if (list.Count == 0)
			{
				return "";
			}
			return $"P{Math.Max(0, parameterCount)}:E{Math.Max(0, exceptionRegionCount)}:" + string.Join(',', list);
		}
		catch
		{
			return "";
		}
	}

	private static int OperandBytes(OperandType type, byte[] il, int offset)
	{
		if (1 == 0)
		{
		}
		int result;
		switch (type)
		{
		case OperandType.InlineNone:
			result = 0;
			break;
		case OperandType.ShortInlineBrTarget:
		case OperandType.ShortInlineI:
		case OperandType.ShortInlineVar:
			result = 1;
			break;
		case OperandType.InlineVar:
			result = 2;
			break;
		case OperandType.InlineBrTarget:
		case OperandType.InlineField:
		case OperandType.InlineI:
		case OperandType.InlineMethod:
		case OperandType.InlineSig:
		case OperandType.InlineString:
		case OperandType.InlineTok:
		case OperandType.InlineType:
		case OperandType.ShortInlineR:
			result = 4;
			break;
		case OperandType.InlineI8:
		case OperandType.InlineR:
			result = 8;
			break;
		case OperandType.InlineSwitch:
			result = SwitchBytes(il, offset);
			break;
		default:
			result = -1;
			break;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static int SwitchBytes(byte[] il, int offset)
	{
		if (offset < 0 || offset + 4 > il.Length)
		{
			return -1;
		}
		int num = BitConverter.ToInt32(il, offset);
		if (num < 0 || num > 8192)
		{
			return -1;
		}
		long num2 = 4 + (long)num * 4L;
		return (int)((num2 > int.MaxValue) ? (-1) : num2);
	}

	private static Dictionary<short, OpCode> BuildOpCodeMap()
	{
		Dictionary<short, OpCode> dictionary = new Dictionary<short, OpCode>();
		FieldInfo[] fields = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public);
		foreach (FieldInfo fieldInfo in fields)
		{
			if (!(fieldInfo.FieldType != typeof(OpCode)))
			{
				try
				{
					OpCode value = (OpCode)fieldInfo.GetValue(null);
					dictionary[value.Value] = value;
				}
				catch
				{
				}
			}
		}
		return dictionary;
	}
}
