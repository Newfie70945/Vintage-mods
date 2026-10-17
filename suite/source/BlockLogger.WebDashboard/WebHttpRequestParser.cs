using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BlockLogger.WebDashboard;

internal static class WebHttpRequestParser
{
	private const int MaxHeaderBytes = 32768;

	private const int MaxBodyBytes = 131072;

	public static async Task<WebRequest?> ReadAsync(NetworkStream stream, CancellationToken cancellationToken)
	{
		using MemoryStream memory = new MemoryStream();
		byte[] buffer = new byte[4096];
		int headerEnd = -1;
		while (headerEnd < 0)
		{
			int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (read <= 0)
			{
				return null;
			}
			int previousLength = (int)memory.Length;
			memory.Write(buffer, 0, read);
			if (memory.Length > 163840)
			{
				throw new InvalidDataException("HTTP request too large.");
			}
			byte[] current = memory.GetBuffer();
			headerEnd = FindHeaderEnd(current, (int)memory.Length, Math.Max(0, previousLength - 3));
			if (headerEnd < 0 && memory.Length > 32768)
			{
				throw new InvalidDataException("HTTP headers too large.");
			}
		}
		if (headerEnd > 32768)
		{
			throw new InvalidDataException("HTTP headers too large.");
		}
		byte[] data = memory.GetBuffer();
		string headerText = Encoding.ASCII.GetString(data, 0, headerEnd);
		string[] lines = headerText.Split(new string[1] { "\r\n" }, StringSplitOptions.None);
		if (lines.Length == 0)
		{
			return null;
		}
		string[] requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
		if (requestLine.Length < 2)
		{
			throw new InvalidDataException("Invalid HTTP request line.");
		}
		string method = requestLine[0].Trim().ToUpperInvariant();
		string target = requestLine[1].Trim();
		Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		for (int i = 1; i < lines.Length; i++)
		{
			int colon = lines[i].IndexOf(':');
			if (colon <= 0 || char.IsWhiteSpace(lines[i][0]))
			{
				throw new InvalidDataException("Invalid HTTP header.");
			}
			string name = lines[i].Substring(0, colon);
			if (name.Any((char c) => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)))
			{
				throw new InvalidDataException("Invalid HTTP header name.");
			}
			if (!headers.TryAdd(name, lines[i].Substring(colon + 1).Trim()))
			{
				throw new InvalidDataException("Duplicate HTTP header.");
			}
		}
		if (headers.ContainsKey("Transfer-Encoding"))
		{
			throw new InvalidDataException("Transfer-Encoding is not supported.");
		}
		int contentLength = 0;
		if (headers.TryGetValue("Content-Length", out string lengthText) && (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) || contentLength < 0 || contentLength > 131072))
		{
			throw new InvalidDataException("Invalid Content-Length.");
		}
		int bodyOffset = headerEnd + 4;
		int alreadyRead = Math.Max(0, (int)memory.Length - bodyOffset);
		while (alreadyRead < contentLength)
		{
			int read2 = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (read2 <= 0)
			{
				throw new EndOfStreamException("Unexpected end of HTTP request body.");
			}
			memory.Write(buffer, 0, read2);
			alreadyRead += read2;
			if (alreadyRead > 131072)
			{
				throw new InvalidDataException("HTTP body too large.");
			}
		}
		data = memory.GetBuffer();
		string body = ((contentLength == 0) ? "" : Encoding.UTF8.GetString(data, bodyOffset, Math.Min(contentLength, (int)memory.Length - bodyOffset)));
		ParseTarget(target, out string path, out Dictionary<string, string> query);
		return new WebRequest
		{
			Method = method,
			Target = target,
			Path = path,
			Query = query,
			Headers = headers,
			Body = body
		};
	}

	private static int FindHeaderEnd(byte[] data, int length, int start)
	{
		for (int i = start; i <= length - 4; i++)
		{
			if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
			{
				return i;
			}
		}
		return -1;
	}

	private static void ParseTarget(string target, out string path, out Dictionary<string, string> query)
	{
		int num = target.IndexOf('?');
		path = ((num >= 0) ? target.Substring(0, num) : target);
		if (string.IsNullOrWhiteSpace(path))
		{
			path = "/";
		}
		query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (num >= 0 && num < target.Length - 1)
		{
			string[] array = target.Substring(num + 1).Split('&', StringSplitOptions.RemoveEmptyEntries);
			foreach (string text in array)
			{
				int num2 = text.IndexOf('=');
				string value = ((num2 >= 0) ? text.Substring(0, num2) : text);
				string value2 = ((num2 >= 0) ? text.Substring(num2 + 1) : "");
				query[UrlDecode(value)] = UrlDecode(value2);
			}
		}
	}

	private static string UrlDecode(string value)
	{
		try
		{
			return Uri.UnescapeDataString((value ?? "").Replace("+", " "));
		}
		catch
		{
			return value ?? "";
		}
	}
}
