using System;
using System.Net;
using System.Text;

namespace Newfies.ChatProtocol;

internal static class ChatMessageBoundary
{
	internal sealed record Line(string Body, bool HasSender)
	{
		public bool IsCommand => Body.TrimStart().StartsWith("/", StringComparison.Ordinal);
	}

	public static Line Parse(string? message, string? realName, string? displayName = null, string? roleName = null)
	{
		string text = (message ?? "").TrimStart();
		if (text.Length == 0 || text[0] == '/')
		{
			return new Line(text, HasSender: false);
		}
		StringBuilder stringBuilder = new StringBuilder();
		int num = 0;
		while (num < text.Length && num < 512)
		{
			if (text[num] == '<')
			{
				int num2 = text.IndexOf('>', num + 1);
				if (num2 < 0 || num2 >= 512)
				{
					return new Line(text, HasSender: false);
				}
				int num3 = num + 1;
				string tag = text.Substring(num3, num2 - num3).Trim();
				if (!SenderTag(tag))
				{
					return new Line(text, HasSender: false);
				}
				num = num2 + 1;
				continue;
			}
			if (text[num] == ':')
			{
				string value = WebUtility.HtmlDecode(stringBuilder.ToString()).Trim();
				if (!Match(value, realName, roleName) && !Match(value, displayName, roleName))
				{
					return new Line(text, HasSender: false);
				}
				num++;
				while (num < text.Length)
				{
					if (char.IsWhiteSpace(text[num]))
					{
						num++;
						continue;
					}
					if (text[num] == '<' && num + 1 < text.Length && text[num + 1] == '/')
					{
						int num4 = text.IndexOf('>', num + 2);
						if (num4 >= 0)
						{
							int num3 = num + 1;
							if (!SenderTag(text.Substring(num3, num4 - num3).Trim()))
							{
								break;
							}
							num = num4 + 1;
							continue;
						}
						break;
					}
					break;
				}
				return new Line(text.Substring(num), HasSender: true);
			}
			stringBuilder.Append(text[num++]);
		}
		return new Line(text, HasSender: false);
	}

	private static bool Match(string value, string? name, string? role)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return false;
		}
		if (value.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return !string.IsNullOrWhiteSpace(role) && value.Equals("[" + role.Trim() + "] " + name.Trim(), StringComparison.OrdinalIgnoreCase);
	}

	private static bool SenderTag(string tag)
	{
		if (tag.StartsWith('/'))
		{
			tag = tag.Substring(1).TrimStart();
		}
		int num = tag.IndexOfAny(new char[4] { ' ', '\t', '\r', '\n' });
		string text = ((num < 0) ? tag : tag.Substring(0, num));
		return text.Equals("strong", StringComparison.OrdinalIgnoreCase) || text.Equals("font", StringComparison.OrdinalIgnoreCase) || text.Equals("b", StringComparison.OrdinalIgnoreCase);
	}
}
