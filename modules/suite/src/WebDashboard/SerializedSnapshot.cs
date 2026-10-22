using System.Text.Json;

namespace BlockLogger.WebDashboard;

// Owned by one snapshot cache entry. Bytes disappear with that entry, and are
// prepared on the HTTP worker, never while capturing game-thread state.
internal sealed class SerializedSnapshot
{
    internal const int MaxRetainedBytes = 256 * 1024;
    private readonly object gate = new();
    private byte[]? bytes;
    private readonly object payload;
    public SerializedSnapshot(object payload) => this.payload = payload;

    public byte[] GetBytes(JsonSerializerOptions options, out bool reused)
    {
        lock (gate)
        {
            reused = bytes != null;
            if (bytes != null) return bytes;
            var result = JsonSerializer.SerializeToUtf8Bytes(payload, options);
            if (result.Length <= MaxRetainedBytes) bytes = result;
            return result;
        }
    }
}
