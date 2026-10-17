using BlockLogger.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using BlockLogger.WebDashboard;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Config;
using Newfies.ChatProtocol;

namespace BlockLogger.WebConsole;

public sealed class WebConsoleSnapshot
{
    public bool CommandInputEnabled { get; set; }
    public long CommandInputExpiresUnixMilliseconds { get; set; }
    public double CpuPercent { get; set; }
    public double RamMb { get; set; }
    public int PlayersOnline { get; set; }
    public string LogPath { get; set; } = "";
    public long LogLength { get; set; }
    public List<string> Lines { get; set; } = new();
    public bool PlayerChatIncluded { get; set; }
    public List<string> ChatLines { get; set; } = new();
    public BadBlockEntitySnapshot BlockEntityAlerts { get; set; } = new();
}

public sealed class WebConsoleService : IDisposable
{
    private const int MaxReadBytes = 512 * 1024;
    private const long TailCacheFreshnessMilliseconds = 3000;
    private readonly ICoreServerAPI api;
    private readonly System.Func<IServerPlayer,string>? displayName;
    private readonly Process process;
    private readonly object sync = new();
    private DateTime lastCpuWall = DateTime.UtcNow;
    private TimeSpan lastCpuTime;
    private double cpuPercent;
    private double ramMb;
    private int playersOnline;
    private long inputEnabledUntil;
    private bool disposed;
    private long lastSampleUnixMilliseconds;
    private const long SampleFreshnessMilliseconds = 2000;
    private string cachedTailPath = "";
    private long cachedTailLength = -1;
    private long cachedTailUnixMilliseconds;
    private List<string> cachedTailLines = new();
    private readonly Queue<string> publicChat = new();
    private bool chatStarted;

    public WebConsoleService(ICoreServerAPI api,System.Func<IServerPlayer,string>? displayName=null)
    {
        this.api = api;
        this.displayName=displayName;
        process = Process.GetCurrentProcess();
        lastCpuTime = process.TotalProcessorTime;
    }

    public void Start()
    {
        // No periodic sampler: console metrics are refreshed on demand when the Web Console
        // is actually opened/polled. This keeps the feature effectively idle when unused.
        Sample();
    }

    public void StartChatCapture()
    {
        if(chatStarted||disposed)return;
        api.Event.PlayerChat += OnPlayerChat;
        chatStarted=true;
    }

    private void OnPlayerChat(IServerPlayer player,int channelId,ref string message,ref string data,BoolRef consumed)
    {
        if(disposed||player==null||consumed.value||channelId!=GlobalConstants.GeneralChatGroup||string.IsNullOrWhiteSpace(message))return;
        var parsed=ChatMessageBoundary.Parse(message,player.PlayerName,displayName?.Invoke(player),player.Role?.Name);
        if(parsed.IsCommand)return;
        var presentation = new Vintagestory.API.Datastructures.TreeAttribute();
        presentation.SetString("playerUid", player.PlayerUID);
        presentation.SetInt("channelId", channelId);
        presentation.SetString("rawLine", message);
        presentation.SetString("body", parsed.Body);
        try { api.Event.PushEvent(ChatDisplayService.PresentationQueryEvent, presentation); } catch { return; }
        if (presentation.GetBool("blocked", false)) return;
        string visible = presentation.GetBool("available", false) ? presentation.GetString("visibleText", parsed.Body) : System.Net.WebUtility.HtmlDecode(parsed.Body);
        string text=visible.Replace("\r"," ").Replace("\n"," ");
        if(text.Length>2000)text=text[..2000];
        string name=(player.PlayerName??"Player").Replace("\r"," ").Replace("\n"," ");
        lock(sync){publicChat.Enqueue($"{DateTime.UtcNow:HH:mm:ss} UTC [Game Chat] {name}: {text}");while(publicChat.Count>500)publicChat.Dequeue();}
    }

    public WebConsoleSnapshot GetSnapshot(int lines,bool includePlayerChat=false)
    {
        EnsureFreshSample();
        lines = Math.Clamp(lines, 50, 1000);
        string logPath = ResolveCurrentLogPath();
        List<string> output = GetTailCached(logPath, lines, out long length);
        lock (sync)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool enabled = inputEnabledUntil > now;
            if (!enabled) inputEnabledUntil = 0;
            return new WebConsoleSnapshot
            {
                CommandInputEnabled = enabled,
                CommandInputExpiresUnixMilliseconds = inputEnabledUntil,
                CpuPercent = cpuPercent,
                RamMb = ramMb,
                PlayersOnline = playersOnline,
                LogPath = logPath,
                LogLength = length,
                Lines = output,
                PlayerChatIncluded = includePlayerChat,
                ChatLines = includePlayerChat ? publicChat.ToList() : new List<string>()
            };
        }
    }

    public bool SetInputEnabled(bool enabled)
    {
        lock (sync)
        {
            inputEnabledUntil = enabled ? DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds() : 0;
            return enabled;
        }
    }

    public bool IsInputEnabled()
    {
        lock (sync) return inputEnabledUntil > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public void TouchInputLease()
    {
        lock (sync)
        {
            if (inputEnabledUntil > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                inputEnabledUntil = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds();
        }
    }


    private void EnsureFreshSample()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - Interlocked.Read(ref lastSampleUnixMilliseconds) < SampleFreshnessMilliseconds) return;
        Sample();
    }

    private void Sample()
    {
        if (disposed) return;
        try
        {
            var now = DateTime.UtcNow;
            var cpu = process.TotalProcessorTime;
            double elapsed = Math.Max(0.001, (now - lastCpuWall).TotalSeconds);
            double used = Math.Max(0, (cpu - lastCpuTime).TotalSeconds);
            lastCpuWall = now;
            lastCpuTime = cpu;
            double pct = Math.Clamp(used / elapsed / Math.Max(1, Environment.ProcessorCount) * 100d, 0d, 100d);
            double ram = process.WorkingSet64 / 1048576d;
            int players = 0;
            try { players = api.World.AllOnlinePlayers?.Length ?? 0; } catch { }
            lock (sync) { cpuPercent = pct; ramMb = ram; playersOnline = players; }
            Interlocked.Exchange(ref lastSampleUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch { }
    }

    private string ResolveCurrentLogPath()
    {
        string root;
        try { root = api.GetOrCreateDataPath("Logs"); }
        catch
        {
            string modData = api.GetOrCreateDataPath("ModData");
            root = Path.Combine(Directory.GetParent(modData)?.FullName ?? modData, "Logs");
        }
        string preferred = Path.Combine(root, "server-main.log");
        if (File.Exists(preferred)) return preferred;
        try
        {
            string? newest = null;
            DateTime newestUtc = DateTime.MinValue;
            foreach (string candidate in Directory.EnumerateFiles(root, "server-main*.log", SearchOption.TopDirectoryOnly))
            {
                DateTime stamp;
                try { stamp = File.GetLastWriteTimeUtc(candidate); } catch { continue; }
                if (newest == null || stamp > newestUtc)
                {
                    newest = candidate;
                    newestUtc = stamp;
                }
            }
            return newest ?? preferred;
        }
        catch { return preferred; }
    }

    private List<string> GetTailCached(string path, int maxLines, out long fileLength)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long currentLength;
        try { currentLength = File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { currentLength = -1; }

        lock (sync)
        {
            if (string.Equals(cachedTailPath, path, StringComparison.Ordinal) &&
                now - cachedTailUnixMilliseconds < TailCacheFreshnessMilliseconds &&
                cachedTailLines.Count > 0)
            {
                fileLength = Math.Max(0, currentLength);
                return CopyTail(cachedTailLines, maxLines);
            }
        }

        List<string> fresh = Tail(path, 1000, out fileLength);
        lock (sync)
        {
            cachedTailPath = path;
            cachedTailLength = fileLength;
            cachedTailUnixMilliseconds = now;
            cachedTailLines = fresh;
            return CopyTail(cachedTailLines, maxLines);
        }
    }

    private static List<string> CopyTail(List<string> source, int maxLines)
    {
        int count = Math.Min(maxLines, source.Count);
        int start = source.Count - count;
        var result = new List<string>(count);
        for (int i = start; i < source.Count; i++) result.Add(source[i]);
        return result;
    }

    private static List<string> Tail(string path, int maxLines, out long fileLength)
    {
        fileLength = 0;
        try
        {
            if (!File.Exists(path)) return new List<string> { "server-main.log is not available yet." };
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fileLength = fs.Length;
            long start = Math.Max(0, fs.Length - MaxReadBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8, true, 4096, leaveOpen: false);
            string text = sr.ReadToEnd();
            if (start > 0)
            {
                int firstBreak = text.IndexOf('\n');
                if (firstBreak >= 0) text = text[(firstBreak + 1)..];
            }
            string[] parts = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int count = Math.Min(maxLines, parts.Length);
            int first = parts.Length - count;
            var result = new List<string>(count);
            for (int i = first; i < parts.Length; i++) result.Add(parts[i]);
            return result;
        }
        catch (Exception ex) { return new List<string> { $"Console mirror read failed: {ex.GetType().Name}: {ex.Message}" }; }
    }

    public void Dispose()
    {
        disposed = true;
        if(chatStarted){api.Event.PlayerChat -= OnPlayerChat;chatStarted=false;}
        lock(sync)publicChat.Clear();
        try { process.Dispose(); } catch { }
    }
}
