using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Newfies.WebsiteConnector;

public sealed class ConnectorServer : IDisposable
{
    public const string Version = "1.1.1";
    private readonly ConnectorConfig config;
    public AccountLinking Links { get; }
    private readonly Func<byte[]> status;
    private readonly HttpClient upstream;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim slots;
    private readonly ConcurrentDictionary<long, Task> requests = new();
    private readonly ConcurrentDictionary<string, CacheEntry> cache = new();
    private readonly TcpListener listener;
    private Task? acceptTask;
    private long nextId, lastSuiteSuccess;
    private int disposed;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class CacheEntry
    {
        public readonly SemaphoreSlim Gate = new(1);
        public byte[]? Body;
        public long Expires;
        public long FailureExpires;
    }
    public ConnectorServer(ConnectorConfig config, Func<byte[]> status)
    {
        this.config = config; this.status = status; Links = new AccountLinking(config.AccountLinkingEnabled);
        slots = new SemaphoreSlim(config.MaxConcurrentRequests);
        listener = new TcpListener(IPAddress.Parse(config.BindAddress), config.Port);
        upstream = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, MaxConnectionsPerServer = config.MaxConcurrentRequests })
        { BaseAddress = new Uri(config.SuitePublicOrigin), Timeout = TimeSpan.FromSeconds(config.UpstreamTimeoutSeconds) };
    }
    public void Start()
    {
        listener.Start(64);
        acceptTask = AcceptLoop();
    }
    public string Status => $"Website Connector {Version}; {config.BindAddress}:{config.Port}; active requests={requests.Count}; Suite read={(Interlocked.Read(ref lastSuiteSuccess) == 0 ? "not yet requested" : "successful")}.";
    private async Task AcceptLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                if (!slots.Wait(0)) { client.Dispose(); continue; }
                long id = Interlocked.Increment(ref nextId);
                var task = HandleClient(client);
                requests[id] = task;
                _ = task.ContinueWith(_ => requests.TryRemove(id, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
    }
    private async Task HandleClient(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(config.UpstreamTimeoutSeconds + 5));
            NetworkStream stream = client.GetStream();
            try
            {
                client.NoDelay = true;
                var bytes = new byte[8192]; int count = 0, end = -1;
                while (count < bytes.Length)
                {
                    int n = await stream.ReadAsync(bytes.AsMemory(count), deadline.Token).ConfigureAwait(false);
                    if (n == 0) return;
                    count += n;
                    for (int i = Math.Max(0, count - n - 3); i + 3 < count; i++)
                        if (bytes[i] == 13 && bytes[i+1] == 10 && bytes[i+2] == 13 && bytes[i+3] == 10) { end = i; break; }
                    if (end >= 0) break;
                }
                if (end < 0) { await Reply(stream, 431, "application/json", Message("Request headers too large."), deadline.Token); return; }
                var lines = Encoding.ASCII.GetString(bytes, 0, end).Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length != 3 || first[2] is not ("HTTP/1.0" or "HTTP/1.1")) { await Fail(400, "Invalid request."); return; }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0 || !headers.TryAdd(line[..colon], line[(colon+1)..].Trim())) { await Fail(400, "Invalid headers."); return; }
                }
                if (!PublicContract.Authorized(headers.GetValueOrDefault("Authorization"), config.AccessToken)) { await Fail(401, "Website connector authorization required."); return; }
                var target = first[1];
                if (target.Any(c => c <= 32 || c >= 127)) { await Fail(400, "Invalid request target."); return; }
                int question = target.IndexOf('?');
                var path = question < 0 ? target : target[..question]; var query = question < 0 ? "" : target[(question+1)..];
                if (first[0] == "POST" && path is "/api/link/challenge" or "/api/link/status" && question < 0)
                {
                    if (!Links.Enabled) { await Fail(503, "Account linking is disabled."); return; }
                    if (headers.ContainsKey("Transfer-Encoding") || !headers.TryGetValue("Content-Length", out var lengthText) || !int.TryParse(lengthText, out int length) || length is < 1 or > 4096 || !headers.GetValueOrDefault("Content-Type", "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) { await Fail(400, "Invalid linking request body."); return; }
                    int buffered = count - end - 4;
                    if (buffered > length) { await Fail(400, "Unexpected request bytes."); return; }
                    var input = new byte[length]; bytes.AsSpan(end + 4, buffered).CopyTo(input); int received = buffered;
                    while (received < length) { int n = await stream.ReadAsync(input.AsMemory(received), deadline.Token).ConfigureAwait(false); if (n == 0) return; received += n; }
                    try
                    {
                        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 8 });
                        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("secret", out var secretValue) || secretValue.ValueKind != JsonValueKind.String) { await Fail(400, "Invalid private challenge."); return; }
                        var secret = secretValue.GetString()!;
                        var result = path == "/api/link/challenge" ? Links.Create(secret) : Links.Status(secret);
                        await Reply(stream, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(result, Json), deadline.Token).ConfigureAwait(false);
                    }
                    catch (JsonException) { await Fail(400, "Invalid private challenge."); }
                    catch (ArgumentException) { await Fail(400, "Invalid private challenge."); }
                    catch (InvalidOperationException) { await Fail(503, "Account linking is busy or disabled."); }
                    return;
                }
                if (first[0] != "GET") { await Fail(405, "Only public GET reads and private linking POSTs are supported."); return; }
                if (headers.ContainsKey("Transfer-Encoding") || (headers.TryGetValue("Content-Length", out var size) && size != "0") || count > end + 4) { await Fail(400, "GET request bodies are not supported."); return; }
                bool tile = path.EndsWith(".png", StringComparison.Ordinal);
                if (!PublicContract.AllowedPath(path) || (question >= 0 && (!tile || !PublicContract.TileQuery().IsMatch(query)))) { await Fail(404, "Route not found."); return; }
                byte[] body;
                if (path == "/api/status") body = status();
                else if (path == "/api/community") body = JsonSerializer.SerializeToUtf8Bytes(new { connected = true, supporters = config.Supporters, updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), stale = false }, Json);
                else if (path == "/api/health") body = JsonSerializer.SerializeToUtf8Bytes(new { connectorVersion = Version, contractVersion = 2, ready = true, accountLinkingEnabled = Links.Enabled, lastSuiteReadUnixMilliseconds = Interlocked.Read(ref lastSuiteSuccess) }, Json);
                else
                {
                    var result = await ReadSuite(path, query, tile, deadline.Token).ConfigureAwait(false);
                    if (result == null) { await Fail(tile ? 404 : 503, "Suite public feed unavailable. Check its map listener configuration."); return; }
                    body = result;
                }
                await Reply(stream, 200, tile ? "image/png" : "application/json; charset=utf-8", body, deadline.Token).ConfigureAwait(false);
                async Task Fail(int code, string message) => await Reply(stream, code, "application/json; charset=utf-8", Message(message), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or HttpRequestException or JsonException or InvalidOperationException)
            {
                if (!deadline.IsCancellationRequested)
                    try { await Reply(stream, 503, "application/json", Message("Website connector temporarily unavailable."), deadline.Token).ConfigureAwait(false); } catch { }
            }
            finally { slots.Release(); }
        }
    }
    private async Task<byte[]?> ReadSuite(string path, string query, bool tile, CancellationToken token)
    {
        // Exactly three fixed JSON keys; unbounded visitor-controlled tile paths are never cached here.
        var entry = tile ? null : cache.GetOrAdd(path, _ => new CacheEntry());
        if (entry != null)
        {
            await entry.Gate.WaitAsync(token).ConfigureAwait(false);
            if (entry.FailureExpires > Environment.TickCount64) { entry.Gate.Release(); return null; }
            if (entry.Body != null && entry.Expires > Environment.TickCount64) { entry.Gate.Release(); return entry.Body; }
        }
        if (entry != null) entry.FailureExpires = Environment.TickCount64 + 5000;
        try
        {
            using var response = await upstream.GetAsync(path + (query.Length == 0 ? "" : "?" + query), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || (tile && response.Content.Headers.ContentType?.MediaType != "image/png")) return null;
            int cap = tile ? 2 * 1024 * 1024 : 4 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > cap) return null;
            using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream(); var buffer = new byte[16384];
            int n;
            while ((n = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            { if (output.Length + n > cap) return null; await output.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false); }
            var result = output.ToArray();
            if (!tile)
            {
                if (JsonNode.Parse(result) is not JsonObject source) return null;
                result = JsonSerializer.SerializeToUtf8Bytes(PublicContract.Sanitize(path, source), Json);
                entry!.FailureExpires = 0; entry.Body = result; entry.Expires = Environment.TickCount64 + config.SnapshotCacheSeconds * 1000L;
            }
            Interlocked.Exchange(ref lastSuiteSuccess, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return result;
        }
        finally { entry?.Gate.Release(); }
    }
    private static byte[] Message(string message) => JsonSerializer.SerializeToUtf8Bytes(new { error = message }, Json);
    private static async Task Reply(NetworkStream stream, int code, string type, byte[] body, CancellationToken token)
    {
        var reason = code switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed", 431 => "Request Header Fields Too Large", _ => "Service Unavailable" };
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); listener.Stop(); upstream.Dispose();
        // Release transport resources after cancellation completes, without blocking the game thread.
        _ = Task.Run(async () => {
            try { if (acceptTask != null) await acceptTask.ConfigureAwait(false); } catch { }
            try { await Task.WhenAll(requests.Values).ConfigureAwait(false); } catch { }
            foreach (var entry in cache.Values) entry.Gate.Dispose();
            slots.Dispose(); stop.Dispose();
        });
    }
}
