using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace BlockLogger.WebDashboard;

// Fixed-length, one-request-per-connection HTTP parser, independent of routing
// and game APIs. Header scans resume at the previous read boundary.
internal static class WebHttpRequestParser
{
    private const int MaxHeaderBytes=32*1024, MaxBodyBytes=128*1024;
    public static async Task<WebRequest?> ReadAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        byte[] buffer = new byte[4096];
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0) return null;

            int previousLength=(int)memory.Length;
            memory.Write(buffer, 0, read);
            if (memory.Length > MaxHeaderBytes + MaxBodyBytes)
                throw new InvalidDataException("HTTP request too large.");

            byte[] current = memory.GetBuffer();
            headerEnd = FindHeaderEnd(current, (int)memory.Length, Math.Max(0,previousLength-3));

            if (headerEnd < 0 && memory.Length > MaxHeaderBytes)
                throw new InvalidDataException("HTTP headers too large.");
        }

        if (headerEnd > MaxHeaderBytes)
            throw new InvalidDataException("HTTP headers too large.");

        byte[] data = memory.GetBuffer();
        string headerText = Encoding.ASCII.GetString(data, 0, headerEnd);
        string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);

        if (lines.Length == 0)
            return null;

        string[] requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
            throw new InvalidDataException("Invalid HTTP request line.");

        string method = requestLine[0].Trim().ToUpperInvariant();
        string target = requestLine[1].Trim();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(lines[i][0]))
                throw new InvalidDataException("Invalid HTTP header.");

            string name = lines[i][..colon];
            if (name.Any(c => !(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c))))
                throw new InvalidDataException("Invalid HTTP header name.");
            if (!headers.TryAdd(name, lines[i][(colon + 1)..].Trim()))
                throw new InvalidDataException("Duplicate HTTP header.");
        }

        // This server supports fixed-length requests only. Reject ambiguous framing
        // rather than interpreting a chunked request differently from a proxy.
        if (headers.ContainsKey("Transfer-Encoding"))
            throw new InvalidDataException("Transfer-Encoding is not supported.");

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out string? lengthText))
        {
            if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) ||
                contentLength < 0 ||
                contentLength > MaxBodyBytes)
                throw new InvalidDataException("Invalid Content-Length.");
        }

        int bodyOffset = headerEnd + 4;
        int alreadyRead = Math.Max(0, (int)memory.Length - bodyOffset);

        while (alreadyRead < contentLength)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                throw new EndOfStreamException("Unexpected end of HTTP request body.");

            memory.Write(buffer, 0, read);
            alreadyRead += read;

            if (alreadyRead > MaxBodyBytes)
                throw new InvalidDataException("HTTP body too large.");
        }

        data = memory.GetBuffer();
        string body = contentLength == 0
            ? ""
            : Encoding.UTF8.GetString(data, bodyOffset, Math.Min(contentLength, (int)memory.Length - bodyOffset));

        ParseTarget(target, out string path, out var query);

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
            if (data[i] == '\r' &&
                data[i + 1] == '\n' &&
                data[i + 2] == '\r' &&
                data[i + 3] == '\n')
                return i;
        }

        return -1;
    }

    private static void ParseTarget(
        string target,
        out string path,
        out Dictionary<string, string> query)
    {
        int q = target.IndexOf('?');
        path = q >= 0 ? target[..q] : target;
        if (string.IsNullOrWhiteSpace(path)) path = "/";

        query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (q < 0 || q >= target.Length - 1)
            return;

        foreach (string pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = equals >= 0 ? pair[..equals] : pair;
            string value = equals >= 0 ? pair[(equals + 1)..] : "";

            query[UrlDecode(key)] = UrlDecode(value);
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

    internal sealed class WebRequest
    {
        public string Method { get; init; } = "";
        public string Target { get; init; } = "";
        public string Path { get; init; } = "/";
        public Dictionary<string, string> Query { get; init; } = new();
        public Dictionary<string, string> Headers { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
        public string Body { get; init; } = "";
    }

