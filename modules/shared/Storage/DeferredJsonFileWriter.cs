namespace Newfies.Storage;

/// <summary>One immutable snapshot in flight and one newer snapshot pending; durable atomic replacement.</summary>
internal sealed class DeferredJsonFileWriter : IDisposable
{
    private readonly string path;
    private readonly Action<string> reportError;
    private readonly object gate = new();
    private readonly AutoResetEvent signal = new(false);
    private readonly Thread worker;
    private Func<byte[]>? pending;
    private long requested, completed;
    private bool stopping;
    private string lastError = "";
    public string LastError { get { lock (gate) return lastError; } }

    public DeferredJsonFileWriter(string path, Action<string> reportError)
    {
        this.path = path; this.reportError = reportError;
        worker = new Thread(Run) { IsBackground = true, Name = "NSS JSON writer " + Path.GetFileName(path) };
        worker.Start();
    }

    public bool Queue(Func<byte[]> serializeImmutableSnapshot)
    {
        lock (gate)
        {
            if (stopping) return false;
            pending = serializeImmutableSnapshot;
            requested++;
        }
        signal.Set();
        return true;
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
                Func<byte[]>? serialize;
                long generation;
                lock (gate)
                {
                    serialize = pending; pending = null; generation = requested;
                    if (serialize == null) { if (stopping) return; break; }
                }
                try
                {
                    byte[] bytes = serialize();
                    if (bytes.LongLength > JsonStateRecovery.MaxStateBytes) throw new InvalidDataException("Serialized state exceeds the 64 MiB safety limit; previous file retained.");
                    string temporary = path + ".tmp";
                    using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
                    File.Move(temporary, path, true);
                    lock (gate) { completed = generation; lastError = ""; Monitor.PulseAll(gate); }
                }
                catch (Exception ex)
                {
                    bool shuttingDown;
                    bool changed;
                    lock (gate)
                    {
                        changed = lastError != ex.Message;
                        lastError = ex.Message;
                        pending ??= serialize; // Never replace a newer queued snapshot with the failed older one.
                        shuttingDown = stopping;
                        Monitor.PulseAll(gate);
                    }
                    if (changed) { try { reportError(ex.Message); } catch { } }
                    if (shuttingDown) return;
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
        else { try { reportError("Writer did not finish within shutdown budget; pending data was not confirmed durable."); } catch { } }
    }
}
