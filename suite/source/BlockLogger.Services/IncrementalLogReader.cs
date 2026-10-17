using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BlockLogger.Services;

internal sealed class IncrementalLogReader
{
	public const int MaxBytesPerPass = 262144;

	private const int MaxLineCharacters = 16384;

	private string path = "";

	private Decoder decoder = Encoding.UTF8.GetDecoder();

	private readonly StringBuilder line = new StringBuilder();

	private readonly List<string> context = new List<string>();

	private bool skippingLine;

	private byte[] previousTail = Array.Empty<byte>();

	public long Offset { get; private set; }

	public long BacklogBytes { get; private set; }

	public long OversizedLines { get; private set; }

	public void Reset(string file, long offset)
	{
		path = file;
		Offset = Math.Max(0L, offset);
		decoder = Encoding.UTF8.GetDecoder();
		line.Clear();
		context.Clear();
		previousTail = Array.Empty<byte>();
		skippingLine = offset > 0;
	}

	public string Read(string file, long startOffset, Func<string, bool>? isHeader = null)
	{
		if (file != path || Offset != startOffset)
		{
			Reset(file, startOffset);
		}
		using FileStream fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		if (fileStream.Length < Offset)
		{
			Reset(file, 0L);
		}
		if (previousTail.Length != 0 && Offset >= previousTail.Length && fileStream.Length >= Offset)
		{
			fileStream.Position = Offset - previousTail.Length;
			byte[] array = new byte[previousTail.Length];
			fileStream.ReadExactly(array);
			if (!((ReadOnlySpan<byte>)array.AsSpan()).SequenceEqual((ReadOnlySpan<byte>)previousTail))
			{
				Reset(file, 0L);
			}
		}
		fileStream.Position = Offset;
		int num = (int)Math.Min(262144L, fileStream.Length - Offset);
		if (num == 0)
		{
			BacklogBytes = 0L;
			return Emit(context.Count);
		}
		byte[] array2 = new byte[num];
		int num2 = fileStream.Read(array2);
		char[] array3 = new char[Encoding.UTF8.GetMaxCharCount(num2)];
		int chars = decoder.GetChars(array2, 0, num2, array3, 0, flush: false);
		Offset += num2;
		previousTail = array2.AsSpan(Math.Max(0, num2 - 64), Math.Min(num2, 64)).ToArray();
		BacklogBytes = Math.Max(0L, fileStream.Length - Offset);
		for (int i = 0; i < chars; i++)
		{
			char c = array3[i];
			if (c == '\n')
			{
				if (!skippingLine)
				{
					context.Add(line.ToString().TrimEnd('\r'));
				}
				line.Clear();
				skippingLine = false;
			}
			else if (!skippingLine)
			{
				if (line.Length >= 16384)
				{
					line.Clear();
					skippingLine = true;
					OversizedLines++;
				}
				else
				{
					line.Append(c);
				}
			}
		}
		int num3 = ((BacklogBytes > 0) ? Math.Max(0, context.Count - 9) : context.Count);
		if (BacklogBytes > 0 && isHeader != null)
		{
			for (int j = Math.Max(0, num3 - 8); j < num3; j++)
			{
				if (isHeader(context[j]))
				{
					num3 = j;
					break;
				}
			}
		}
		return Emit(num3);
	}

	private string Emit(int count)
	{
		if (count == 0)
		{
			return "";
		}
		string result = string.Join('\n', context.Take(count)) + "\n";
		context.RemoveRange(0, count);
		return result;
	}
}
