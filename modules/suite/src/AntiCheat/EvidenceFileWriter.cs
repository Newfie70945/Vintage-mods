using System.Security.Cryptography;
using System.Threading.Channels;

namespace BlockLogger.AntiCheat;

// At most 64 queued 64-KiB chunks (4 MiB per transfer); two transfers are admitted.
// The game thread validates order/identity and only enqueues owned byte arrays.
internal sealed class EvidenceFileWriter
{
    private readonly Channel<byte[]> chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource cancellation = new();
    private readonly string path;
    private readonly Task<string> work;
    public EvidenceFileWriter(string path)
    {
        this.path = path;
        work = Task.Run(WriteAsync);
        // Observe failures even when a client disappears before sending completion.
        _ = work.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
    public bool TryWrite(byte[] bytes) => !work.IsCompleted && chunks.Writer.TryWrite((byte[])bytes.Clone());
    public Task<string> CompleteAsync() { chunks.Writer.TryComplete(); return work; }
    public void Abort()
    {
        cancellation.Cancel(); chunks.Writer.TryComplete();
        _ = Task.Run(async () => { try { await work.ConfigureAwait(false); } catch { } try { File.Delete(path); } catch { } });
    }
    public bool IsAborted => cancellation.IsCancellationRequested;
    private async Task<string> WriteAsync()
    {
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await foreach (byte[] bytes in chunks.Reader.ReadAllAsync(cancellation.Token).ConfigureAwait(false))
                    await stream.WriteAsync(bytes, cancellation.Token).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                stream.Flush(true);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            using var input = File.OpenRead(path);
            byte[] hash = await SHA256.HashDataAsync(input, cancellation.Token).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }
}
