using System.Threading.Channels;
using System.Text.Json;

namespace NewfiesOptimizer;

// Explicit opt-in test evidence, not an authentication implementation or a substitute for gameplay verification.
public sealed class SessionEvidence : IDisposable
{
    private readonly Channel<string> queue = Channel.CreateBounded<string>(new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task writer;
    private readonly int maximumBytes;
    public long Dropped;
    public string? Failure { get; private set; }
    public SessionEvidence(string directory, int maximumBytes)
    {
        this.maximumBytes = maximumBytes;
        writer = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "sessions.jsonl");
                // Each process start begins a new evidence epoch. Preserve one bounded predecessor.
                if (File.Exists(path)) File.Move(path, path + ".previous", true);
                await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 8192, true);
                await foreach (var line in queue.Reader.ReadAllAsync())
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
                    if (file.Position + bytes.Length > this.maximumBytes)
                    {
                        Failure = "Evidence file size cap reached; restart capture with a new run after preserving evidence.";
                        Interlocked.Increment(ref Dropped); continue;
                    }
                    await file.WriteAsync(bytes); await file.FlushAsync();
                }
            }
            catch (Exception e) { Failure = e.GetType().Name + ": " + e.Message; }
        });
    }
    public void Emit(string kind, string uid, string runId, bool authenticationEnabled)
    {
        if (!queue.Writer.TryWrite(JsonSerializer.Serialize(new { schemaVersion = 1, kind, clientId = uid, utc = DateTimeOffset.UtcNow, runId, authenticationEnabled }))) Interlocked.Increment(ref Dropped);
    }
    public void Dispose() { queue.Writer.TryComplete(); try { writer.Wait(TimeSpan.FromSeconds(3)); } catch { /* Runtime reports Failure. */ } }
}
