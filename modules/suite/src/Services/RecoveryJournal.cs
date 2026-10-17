using System.Text;
using Newtonsoft.Json;

namespace BlockLogger.Services;

// A recovery owns exactly one writer. Durable appends are serialized off the game
// thread and awaited before any world mutation. No writes are fire-and-forget.
internal sealed class RecoveryJournal : IAsyncDisposable
{
    private readonly string auditPath, receiptPath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private FileStream? audit, receipts;
    private int disposed;

    public RecoveryJournal(string auditPath, string receiptPath)
    { this.auditPath = auditPath; this.receiptPath = receiptPath; }

    public async Task AppendAsync(object auditRecord, object? receiptRecord = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                audit ??= Open(auditPath);
                Write(audit, auditRecord);
                if (receiptRecord != null)
                {
                    receipts ??= Open(receiptPath);
                    Write(receipts, receiptRecord);
                }
            }).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private static FileStream Open(string path) => new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    private static void Write(FileStream file, object record)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(record) + Environment.NewLine);
        file.Write(bytes);
        file.Flush(true); // retain durable intent/receipt ordering across crashes
    }

    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        await gate.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(() => { audit?.Dispose(); receipts?.Dispose(); }).ConfigureAwait(false); }
        finally { gate.Release(); gate.Dispose(); }
    }
}
