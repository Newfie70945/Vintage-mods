using System.Text;

namespace BlockLogger.Services;

/// <summary>Bounded byte reads with UTF-8, incomplete-line and exception-context carry.</summary>
internal sealed class IncrementalLogReader
{
    public const int MaxBytesPerPass = 256 * 1024;
    private const int MaxLineCharacters = 16 * 1024;
    private string path = "";
    private Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder line = new();
    private readonly List<string> context = new();
    private bool skippingLine;
    private byte[] previousTail = Array.Empty<byte>();
    public long Offset { get; private set; }
    public long BacklogBytes { get; private set; }
    public long OversizedLines { get; private set; }
    public void Reset(string file, long offset)
    {
        path = file; Offset = Math.Max(0, offset);
        decoder = Encoding.UTF8.GetDecoder(); line.Clear(); context.Clear(); previousTail = Array.Empty<byte>();
        skippingLine = offset > 0; // A tail window may begin in the middle of a line/code point.
    }

    public string Read(string file, long startOffset, Func<string, bool>? isHeader = null)
    {
        if (file != path || Offset != startOffset) Reset(file, startOffset);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < Offset) Reset(file, 0);
        if (previousTail.Length > 0 && Offset >= previousTail.Length && stream.Length >= Offset)
        {
            stream.Position = Offset - previousTail.Length;
            byte[] tail = new byte[previousTail.Length];
            stream.ReadExactly(tail);
            if (!tail.AsSpan().SequenceEqual(previousTail)) Reset(file, 0); // Same-path rotation/replacement.
        }
        stream.Position = Offset;
        int count = (int)Math.Min(MaxBytesPerPass, stream.Length - Offset);
        if (count == 0) { BacklogBytes = 0; return Emit(context.Count); }
        byte[] bytes = new byte[count];
        int read = stream.Read(bytes);
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(read)];
        int characterCount = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
        Offset += read;
        previousTail = bytes.AsSpan(Math.Max(0, read - 64), Math.Min(read, 64)).ToArray();
        BacklogBytes = Math.Max(0, stream.Length - Offset);
        for (int i = 0; i < characterCount; i++)
        {
            char c = chars[i];
            if (c == '\n')
            {
                if (!skippingLine) context.Add(line.ToString().TrimEnd('\r'));
                line.Clear(); skippingLine = false;
            }
            else if (!skippingLine)
            {
                if (line.Length >= MaxLineCharacters) { line.Clear(); skippingLine = true; OversizedLines++; }
                else line.Append(c);
            }
        }
        // Reserve the parser's eight-line look-ahead and rewind to any event header
        // immediately before the boundary, so its context is never emitted in another batch.
        int emit = BacklogBytes > 0 ? Math.Max(0, context.Count - 9) : context.Count;
        if (BacklogBytes > 0 && isHeader != null)
            for (int i = Math.Max(0, emit - 8); i < emit; i++)
                if (isHeader(context[i])) { emit = i; break; }
        return Emit(emit);
    }

    private string Emit(int count)
    {
        if (count == 0) return "";
        string text = string.Join('\n', context.Take(count)) + "\n";
        context.RemoveRange(0, count);
        return text;
    }
}
