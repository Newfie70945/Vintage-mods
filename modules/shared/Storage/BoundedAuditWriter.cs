using System.Text;

namespace Newfies.Storage;

/// <summary>Bounded audit queue with background append and four rotated backups.</summary>
internal sealed class BoundedAuditWriter : IDisposable
{
    internal const long MaximumFileBytes = 8 * 1024 * 1024;
    private const int MaximumQueuedBytes = 4 * 1024 * 1024;
    private readonly string path;
    private readonly Action<string> reportError;
    private readonly object gate = new();
    private readonly Queue<byte[]> queue = new();
    private readonly AutoResetEvent signal = new(false);
    private readonly Thread worker;
    private int queuedBytes;
    private long requested, completed, dropped, lastDropWarning;
    private bool stopping;
    private string lastError = "";
    public long Dropped { get { lock (gate) return dropped; } }
    public string LastError { get { lock (gate) return lastError; } }

    public BoundedAuditWriter(string path, Action<string> reportError)
    {
        this.path = path; this.reportError = reportError;
        worker = new Thread(Run) { IsBackground = true, Name = "NSS audit writer" };
        worker.Start();
    }

    public bool Queue(string jsonLine)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(jsonLine + "\n");
        bool warn = false;
        lock (gate)
        {
            if (stopping) return false;
            if (bytes.Length > 64 * 1024 || queue.Count >= 1024 || queuedBytes + bytes.Length > MaximumQueuedBytes)
            {
                dropped++;
                if (Environment.TickCount64 - lastDropWarning >= 30_000) { lastDropWarning = Environment.TickCount64; warn = true; }
            }
            else { queue.Enqueue(bytes); queuedBytes += bytes.Length; requested++; signal.Set(); return true; }
        }
        if (warn) { try { reportError("Audit queue reached its safety limit; dropped entries are counted in ChatGuard diagnostics."); } catch { } }
        return false;
    }

    public bool Flush(int milliseconds = 5000)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        lock (gate)
        {
            long target = requested;
            signal.Set();
            while (completed < target)
            {
                if (lastError.Length > 0) return false;
                int remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
                if (remaining == 0 || !Monitor.Wait(gate, remaining)) return completed >= target;
            }
            return true;
        }
    }

    private void Run()
    {
        while (true)
        {
            signal.WaitOne();
            while (true)
            {
                byte[] bytes;
                lock (gate)
                {
                    if (queue.Count == 0) { if (stopping) return; break; }
                    bytes = queue.Peek(); // Keep the failed entry queued, preserving ordering and memory limits.
                }
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaximumFileBytes)
                    {
                        File.Delete(path + ".4");
                        for (int i = 3; i >= 1; i--) if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1), true);
                        File.Move(path, path + ".1", true);
                    }
                    using (var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { output.Write(bytes); output.Flush(true); }
                    lock (gate) { queue.Dequeue(); queuedBytes -= bytes.Length; completed++; lastError = ""; Monitor.PulseAll(gate); }
                }
                catch (Exception ex)
                {
                    bool warn, shutdown;
                    lock (gate) { warn = lastError != ex.Message; lastError = ex.Message; shutdown = stopping; Monitor.PulseAll(gate); }
                    if (warn) { try { reportError(ex.Message); } catch { } }
                    if (shutdown) return;
                    signal.WaitOne(2000);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (gate) { if (stopping) return; stopping = true; }
        signal.Set();
        if (worker.Join(5000)) signal.Dispose();
        else { try { reportError("Audit writer did not finish within the shutdown budget."); } catch { } }
    }
}
