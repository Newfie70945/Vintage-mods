using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BlockLogger.WebDashboard;

// Follows the server log for native slow-tick entries during an open-ended capture. A timed
// capture can show the raw log delta, but over hours that would be almost entirely unrelated
// lines, so this keeps only each "A tick took ..." entry and its continuation lines (the
// breakdown the engine prints after it) and retains the newest MaxRetainedChars of them.
// Only complete lines are consumed; a partly written line waits for the next poll.
internal sealed class SlowTickLogScanner
{
    public const int MaxRetainedChars = 24000;

    // A new log entry starts with a date and time, e.g. "12.5.2024 14:03:22" or "2024-05-12 14:03:22".
    private static readonly Regex EntryStart = new(
        @"^\s*\d{1,4}[./\-]\d{1,2}[./\-]\d{1,4}[ T]\d{1,2}:\d{2}:\d{2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string path;
    private readonly StringBuilder retained = new();
    private long offset;
    private bool inBlock;

    public int BlocksFound { get; private set; }

    public SlowTickLogScanner(string path, long startOffset)
    {
        this.path = path ?? "";
        offset = Math.Max(0, startOffset);
    }

    public string Text
    {
        get
        {
            if (retained.Length <= MaxRetainedChars) return retained.ToString().Trim();
            string tail = retained.ToString(retained.Length - MaxRetainedChars, MaxRetainedChars);
            int firstBreak = tail.IndexOf('\n');
            return (firstBreak >= 0 ? tail[(firstBreak + 1)..] : tail).Trim();
        }
    }

    public static bool IsEntryStart(string line) => EntryStart.IsMatch(line);

    public static bool IsSlowTickStart(string line) =>
        line.Contains("tick took", StringComparison.OrdinalIgnoreCase);

    // Reads at most maxBytes of newly written complete lines. Safe to call on every status poll.
    public void Poll(int maxBytes = 512 * 1024)
    {
        if (path.Length == 0 || !File.Exists(path)) return;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < offset)
            {
                // The log was rotated or truncated: continue from the start of the new file.
                offset = 0;
                inBlock = false;
            }
            long available = fs.Length - offset;
            if (available <= 0) return;

            int count = (int)Math.Min(available, maxBytes);
            var buffer = new byte[count];
            fs.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < count)
            {
                int n = fs.Read(buffer, read, count - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) return;

            int lastBreak = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            // No newline in a full buffer is one enormous line: skip it rather than stall forever.
            int consume = lastBreak >= 0 ? lastBreak + 1 : read >= maxBytes ? read : 0;
            if (consume == 0) return;

            offset += consume;
            Feed(Encoding.UTF8.GetString(buffer, 0, consume));
        }
        catch
        {
            // Diagnostics must never throw into the status endpoint; try again on the next poll.
        }
    }

    // Processes complete lines. Public so the line rules can be exercised without a file.
    public void Feed(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (IsSlowTickStart(line))
            {
                inBlock = true;
                BlocksFound++;
                Append(line);
            }
            else if (inBlock)
            {
                if (IsEntryStart(line)) inBlock = false;
                else Append(line);
            }
        }
    }

    private void Append(string line)
    {
        retained.Append(line).Append('\n');
        // Trim in bulk so appends stay cheap; the Text getter does the exact tail cut.
        if (retained.Length > MaxRetainedChars * 2)
            retained.Remove(0, retained.Length - MaxRetainedChars);
    }
}
