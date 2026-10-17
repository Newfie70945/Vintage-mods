using System;
using System.Collections.Generic;
using System.Text;

namespace BlockLogger.Services;

public static class ChatMarkdown
{
	public sealed record Result(string Vtml, string ModerationText);

	private sealed class Parser(string text, bool colors, bool links)
	{
		public readonly StringBuilder Plain = new StringBuilder();

		private int spans;

		private int scanBudget = 65536;

		public string Read(int start, int end, int depth)
		{
			StringBuilder stringBuilder = new StringBuilder();
			int num = start;
			while (num < end)
			{
				if (text[num] == '\\' && num + 1 < end && "\\*_`[]()".Contains(text[num + 1]))
				{
					Literal(stringBuilder, text[num + 1]);
					num += 2;
					continue;
				}
				if (depth < 8 && spans < 64)
				{
					if (text[num] == '`')
					{
						int num2 = FindCode(num + 1, end);
						if (num2 > num + 1)
						{
							string value = text.Substring(num + 1, num2 - num - 1);
							stringBuilder.Append("<code>").Append(Escape(value)).Append("</code>");
							Plain.Append(value);
							spans++;
							num = num2 + 1;
							continue;
						}
					}
					if (colors && IsColorOpen(num, end))
					{
						string value2 = text.Substring(num + 8, 6);
						if (text[num + 14] == ']' && IsHex(value2))
						{
							int num3 = FindColorClose(num + 15, end);
							if (num3 > num + 15)
							{
								spans++;
								stringBuilder.Append("<font color=\"#").Append(value2).Append("\">")
									.Append(Read(num + 15, num3, depth + 1))
									.Append("</font>");
								num = num3 + 8;
								continue;
							}
						}
					}
					if (links && text[num] == '[')
					{
						int num4 = FindLabelEnd(num + 1, end);
						int num5 = ((num4 < 0) ? (-1) : FindUrlEnd(num4 + 2, end));
						if (num4 > num + 1 && num5 > num4 + 2)
						{
							string text2 = text.Substring(num4 + 2, num5 - num4 - 2);
							if (SafeUrl(text2))
							{
								string value3 = UnescapeLabel(num + 1, num4);
								stringBuilder.Append("<a href=\"").Append(text2).Append("\">")
									.Append(Escape(value3))
									.Append("</a>");
								Plain.Append(value3).Append(" (").Append(text2)
									.Append(')');
								spans++;
								num = num5 + 1;
								continue;
							}
						}
					}
					char c = text[num];
					if ((c == '*' || c == '_') ? true : false)
					{
						char c2 = text[num];
						int i;
						for (i = 1; num + i < end && text[num + i] == c2; i++)
						{
						}
						bool flag = (num == start || text[num - 1] != c2) && (c2 != '_' || num == start || !char.IsLetterOrDigit(text[num - 1]));
						if (((i <= 3) & flag) && num + i < end && !char.IsWhiteSpace(text[num + i]))
						{
							int num6 = FindEmphasisClose(c2, i, num + i, end);
							if (num6 > num + i && !char.IsWhiteSpace(text[num6 - 1]) && (c2 != '_' || num6 + i == end || !char.IsLetterOrDigit(text[num6 + i])))
							{
								spans++;
								string value4 = Read(num + i, num6, depth + 1);
								stringBuilder.Append(i switch
								{
									2 => "<strong>", 
									3 => "<strong><i>", 
									_ => "<i>", 
								}).Append(value4).Append(i switch
								{
									2 => "</strong>", 
									3 => "</i></strong>", 
									_ => "</i>", 
								});
								num = num6 + i;
								continue;
							}
						}
						Literal(stringBuilder, text.Substring(num, i));
						num += i;
						continue;
					}
				}
				Literal(stringBuilder, text[num]);
				num++;
			}
			return stringBuilder.ToString();
		}

		private void Literal(StringBuilder output, char value)
		{
			switch (value)
			{
			case '<':
				output.Append("&lt;");
				break;
			case '>':
				output.Append("&gt;");
				break;
			default:
				output.Append(value);
				break;
			}
			Plain.Append(value);
		}

		private void Literal(StringBuilder output, string value)
		{
			output.Append(Escape(value));
			Plain.Append(value);
		}

		private bool IsColorOpen(int index, int end)
		{
			return index + 15 <= end && text.AsSpan(index, 8).SequenceEqual("[color=#") && text[index + 14] == ']' && IsHex(text.AsSpan(index + 8, 6));
		}

		private int FindCode(int start, int end)
		{
			for (int i = start; i < end; i++)
			{
				if (scanBudget-- <= 0)
				{
					break;
				}
				if (text[i] == '`')
				{
					return i;
				}
			}
			return -1;
		}

		private int SkipCode(int index, int end)
		{
			if (text[index] != '`')
			{
				return index;
			}
			int num = FindCode(index + 1, end);
			return (num < 0) ? index : num;
		}

		private int FindColorClose(int start, int end)
		{
			int num = 1;
			for (int i = start; i < end && scanBudget-- > 0; i++)
			{
				if (text[i] == '\\')
				{
					i++;
					continue;
				}
				int num2 = SkipCode(i, end);
				if (num2 != i)
				{
					i = num2;
					continue;
				}
				if (links && text[i] == '[')
				{
					int num3 = FindLabelEnd(i + 1, end);
					int num4 = ((num3 < 0) ? (-1) : FindUrlEnd(num3 + 2, end));
					if (num4 >= 0 && SafeUrl(text.Substring(num3 + 2, num4 - num3 - 2)))
					{
						i = num4;
						continue;
					}
				}
				if (IsColorOpen(i, end))
				{
					num++;
					i += 14;
				}
				else if (i + 8 <= end && text.AsSpan(i, 8).SequenceEqual("[/color]"))
				{
					if (--num == 0)
					{
						return i;
					}
					i += 7;
				}
			}
			return -1;
		}

		private int FindLabelEnd(int start, int end)
		{
			int num = 0;
			for (int i = start; i < end; i++)
			{
				if (scanBudget-- <= 0)
				{
					break;
				}
				if (text[i] == '\\')
				{
					i++;
				}
				else if (text[i] == '[')
				{
					num++;
				}
				else if (text[i] == ']')
				{
					if (num <= 0)
					{
						return (i + 1 < end && text[i + 1] == '(') ? i : (-1);
					}
					num--;
				}
			}
			return -1;
		}

		private int FindUrlEnd(int start, int end)
		{
			int num = 0;
			for (int i = start; i < end; i++)
			{
				if (scanBudget-- <= 0)
				{
					break;
				}
				if (text[i] == '(')
				{
					num++;
				}
				else if (text[i] == ')')
				{
					if (num == 0)
					{
						return i;
					}
					num--;
				}
				if (i - start >= 512)
				{
					return -1;
				}
			}
			return -1;
		}

		private string UnescapeLabel(int start, int end)
		{
			StringBuilder stringBuilder = new StringBuilder();
			for (int i = start; i < end; i++)
			{
				if (text[i] == '\\' && i + 1 < end && "\\*_`[]()".Contains(text[i + 1]))
				{
					i++;
				}
				stringBuilder.Append(text[i]);
			}
			return stringBuilder.ToString();
		}

		private int FindEmphasisClose(char marker, int length, int start, int end)
		{
			Stack<int> stack = new Stack<int>();
			for (int i = start; i < end && scanBudget-- > 0; i++)
			{
				if (text[i] == '\\')
				{
					i++;
					continue;
				}
				int num = SkipCode(i, end);
				if (num != i)
				{
					i = num;
					continue;
				}
				if (colors && IsColorOpen(i, end))
				{
					int num2 = FindColorClose(i + 15, end);
					if (num2 >= 0)
					{
						i = num2 + 7;
						continue;
					}
				}
				if (links && text[i] == '[')
				{
					int num3 = FindLabelEnd(i + 1, end);
					int num4 = ((num3 < 0) ? (-1) : FindUrlEnd(num3 + 2, end));
					if (num4 >= 0 && SafeUrl(text.Substring(num3 + 2, num4 - num3 - 2)))
					{
						i = num4;
						continue;
					}
				}
				if (text[i] != marker || (i > start && text[i - 1] == marker))
				{
					continue;
				}
				int j;
				for (j = 1; i + j < end && text[i + j] == marker; j++)
				{
				}
				bool flag = i > start && !char.IsWhiteSpace(text[i - 1]) && (marker != '_' || i + j == end || !char.IsLetterOrDigit(text[i + j]));
				bool flag2 = i + j < end && !char.IsWhiteSpace(text[i + j]) && (marker != '_' || i == start || !char.IsLetterOrDigit(text[i - 1]));
				if (j <= 3)
				{
					int num5 = j;
					if (flag)
					{
						if (j == length)
						{
							return i;
						}
						while (stack.Count > 0 && num5 >= stack.Peek())
						{
							num5 -= stack.Pop();
						}
						if (stack.Count == 0 && num5 == length)
						{
							return i + j - length;
						}
					}
					if (flag2 && num5 > 0 && stack.Count < 8)
					{
						stack.Push(num5);
					}
				}
				i += j - 1;
			}
			return -1;
		}

		private static bool IsHex(ReadOnlySpan<char> value)
		{
			ReadOnlySpan<char> readOnlySpan = value;
			for (int i = 0; i < readOnlySpan.Length; i++)
			{
				char character = readOnlySpan[i];
				if (!Uri.IsHexDigit(character))
				{
					return false;
				}
			}
			return true;
		}

		private static bool SafeUrl(string url)
		{
			if (url.Length > 512)
			{
				return false;
			}
			foreach (char c in url)
			{
				if (char.IsWhiteSpace(c) || char.IsControl(c) || "\"'<>\\".Contains(c))
				{
					return false;
				}
			}
			Uri result;
			return Uri.TryCreate(url, UriKind.Absolute, out result) && (result.Scheme == "https" || result.Scheme == "http") && result.UserInfo.Length == 0 && !string.IsNullOrEmpty(result.Host);
		}
	}

	public static Result Format(string text, bool colors = false, bool links = false)
	{
		if (text.Length > 4096)
		{
			return new Result(Escape(text), text);
		}
		Parser parser = new Parser(text, colors, links);
		string vtml = parser.Read(0, text.Length, 0);
		return new Result(vtml, parser.Plain.ToString());
	}

	private static string Escape(string text)
	{
		return text.Replace("<", "&lt;").Replace(">", "&gt;");
	}
}
