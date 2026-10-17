using System;
using System.Linq;
using System.Text;

namespace BlockLogger.Services;

public static class ChatLayout
{
	public const string DefaultFormat = "{role} {name}: {timestamp} {message}";

	private static readonly string[] Tokens = new string[4] { "role", "name", "timestamp", "message" };

	public static string? Validate(string? format)
	{
		if (string.IsNullOrWhiteSpace(format) || format.Length > 512)
		{
			return "Chat format must contain 1–512 characters.";
		}
		if (format.Any(char.IsControl) || format.TrimStart().StartsWith('/'))
		{
			return "Chat format must be a single line and cannot start with a slash.";
		}
		int[] array = new int[Tokens.Length];
		for (int i = 0; i < format.Length; i++)
		{
			if (format[i] == '}')
			{
				return "Unmatched closing brace in chat format.";
			}
			if (format[i] == '{')
			{
				int num = format.IndexOf('}', i + 1);
				if (num < 0)
				{
					return "Unclosed placeholder in chat format.";
				}
				string[] tokens = Tokens;
				int num2 = i + 1;
				int num3 = Array.IndexOf(tokens, format.Substring(num2, num - num2));
				if (num3 < 0)
				{
					return "Use only {role}, {name}, {timestamp} and {message} placeholders.";
				}
				if (++array[num3] > 1)
				{
					return "Each chat placeholder can appear only once.";
				}
				i = num;
			}
		}
		return (array[1] == 1 && array[3] == 1) ? null : "Chat format requires exactly one {name} and one {message}.";
	}

	public static string Render(string format, string role, string name, string timestamp, string message, Func<string, string> escapeLiteral)
	{
		string[] array = new string[4] { role, name, timestamp, message };
		StringBuilder stringBuilder = new StringBuilder();
		int num = 0;
		while (num < format.Length)
		{
			int num2 = format.IndexOf('{', num);
			if (num2 < 0)
			{
				stringBuilder.Append(escapeLiteral(format.Substring(num)));
				break;
			}
			int num3 = num;
			stringBuilder.Append(escapeLiteral(format.Substring(num3, num2 - num3)));
			int num4 = format.IndexOf('}', num2 + 1);
			int num5;
			if (num4 >= 0)
			{
				string[] tokens = Tokens;
				num3 = num2 + 1;
				num5 = Array.IndexOf(tokens, format.Substring(num3, num4 - num3));
			}
			else
			{
				num5 = -1;
			}
			int num6 = num5;
			if (num6 < 0)
			{
				stringBuilder.Append(escapeLiteral(format.Substring(num2)));
				break;
			}
			stringBuilder.Append(array[num6]);
			num = num4 + 1;
			bool flag = array[num6].Length == 0;
			bool flag2 = flag;
			if (flag2)
			{
				bool flag3 = ((num6 == 0 || num6 == 2) ? true : false);
				flag2 = flag3;
			}
			if (flag2 && num < format.Length && format[num] == ' ')
			{
				num++;
			}
		}
		return stringBuilder.ToString();
	}
}
